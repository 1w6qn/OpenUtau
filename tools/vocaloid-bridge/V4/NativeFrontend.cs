using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VocaloidBridge.V4
{
    /// <summary>
    /// Loads a VSQX through the installed VOCALOID 4 frontend and encodes its
    /// first musical part into the Daisy format consumed by DSCL4.
    /// This ABI is intentionally restricted to the verified x86 Vsq4.dll build.
    /// </summary>
    public sealed class NativeFrontend : IDisposable
    {
        private const string VerifiedVsq4Sha256 = "DB43895E6028602BE70EC1A792D5F081B7BC358F7C74EBAF714F94BEF3D33E7A";
        private const int PolicySize = 0x48;
        private static readonly object NativeLock = new object();

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void Ctor(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void Dtor(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int Initialize(IntPtr self, IntPtr policy);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int IsInitialized(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr LoadVsq(IntPtr self, IntPtr path);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate uint GetErrorCode(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr GetErrorMessage(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr NoArgsPtr(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int ListCount(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr ListAt(IntPtr self, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DynamicCast(IntPtr obj, int vfDelta, IntPtr sourceType, IntPtr targetType, int isReference);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateTimeMap(IntPtr masterTrack);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr EncodePart(IntPtr self, IntPtr encodeArgs);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void DeleteDaisy(IntPtr self, int deleteFlag);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteArray(IntPtr ptr);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetDllDirectoryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);

        private readonly string root;
        private readonly IntPtr vsqModule;
        private readonly IntPtr dscModule;
        private readonly IntPtr manager;
        private bool disposed;

        public NativeFrontend(string engineRoot)
        {
            if (IntPtr.Size != 4) throw new PlatformNotSupportedException("The installed VOCALOID 4 frontend ABI is x86-only; run this bridge with 32-bit .NET Framework.");
            if (String.IsNullOrWhiteSpace(engineRoot)) throw new ArgumentException("VOCALOID 4 engine directory is required.", "engineRoot");
            root = Path.GetFullPath(engineRoot);
            string vsqPath = Path.Combine(root, "Vsq4.dll");
            string dscPath = Path.Combine(root, "DSCL4.dll");
            if (!File.Exists(vsqPath) || !File.Exists(dscPath)) throw new FileNotFoundException("Vsq4.dll and DSCL4.dll must both exist in the engine directory.", vsqPath);
            string hash;
            using (var stream = File.OpenRead(vsqPath))
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
            if (!String.Equals(hash, VerifiedVsq4Sha256, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Unsupported Vsq4.dll build (SHA-256 " + hash + "); frontend offsets are verified only for the installed VOCALOID 4 build.");

            lock (NativeLock)
            {
                Directory.SetCurrentDirectory(root);
                if (!SetDllDirectoryW(root)) throw new InvalidOperationException("SetDllDirectory failed: " + Marshal.GetLastWin32Error());
                vsqModule = LoadLibraryW(vsqPath);
                if (vsqModule == IntPtr.Zero) throw new InvalidOperationException("Could not load Vsq4.dll: " + Marshal.GetLastWin32Error());
                dscModule = LoadLibraryW(dscPath);
                if (dscModule == IntPtr.Zero) throw new InvalidOperationException("Could not load DSCL4.dll: " + Marshal.GetLastWin32Error());
                manager = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(manager, 0);
                try
                {
                    Bind<Ctor>(vsqModule, "??0CVSVsqManagerIF@@QAE@XZ")(manager);
                    InitializeManager();
                }
                catch
                {
                    try { Bind<Dtor>(vsqModule, "??1CVSVsqManagerIF@@QAE@XZ")(manager); } catch { }
                    Marshal.FreeHGlobal(manager);
                    throw;
                }
            }
        }

        public NativePhrase Load(string vsqxPath)
        {
            if (disposed) throw new ObjectDisposedException("NativeFrontend");
            if (String.IsNullOrWhiteSpace(vsqxPath)) throw new ArgumentException("VSQX path is required.", "vsqxPath");
            lock (NativeLock)
            {
                if (disposed) throw new ObjectDisposedException("NativeFrontend");
                IntPtr path = Marshal.StringToHGlobalUni(Path.GetFullPath(vsqxPath));
                IntPtr encoder = IntPtr.Zero, args = IntPtr.Zero, timeMap = IntPtr.Zero, daisy = IntPtr.Zero;
                try
                {
                    IntPtr rootObject = Bind<LoadVsq>(vsqModule, "?fileLoadVsqFile@CVSVsqManagerIF@@QAEPAVIVSVsqRoot@@PB_W@Z")(manager, path);
                    if (rootObject == IntPtr.Zero)
                    {
                        uint code = Bind<GetErrorCode>(vsqModule, "?sysGetLastError@CVSVsqManagerIF@@QBEIXZ")(manager);
                        IntPtr message = Bind<GetErrorMessage>(vsqModule, "?sysGetLastErrorMsg@CVSVsqManagerIF@@QBEPB_WXZ")(manager);
                        throw new InvalidDataException("VOCALOID rejected VSQX (0x" + code.ToString("X8") + "): " + (message == IntPtr.Zero ? "no error detail" : Marshal.PtrToStringUni(message)));
                    }

                    IntPtr master = Bind<NoArgsPtr>(vsqModule, "?vsqGetCurrentMasterTrack@CVSVsqManagerIF@@QAEPAVIVSMasterTrack@@XZ")(manager);
                    if (master == IntPtr.Zero) throw new InvalidDataException("VSQX has no master track.");
                    IntPtr part = FindFirstMusicalPart(rootObject);
                    IntPtr msvcr = GetModuleHandleW("MSVCR110.dll");
                    if (msvcr == IntPtr.Zero) msvcr = LoadLibraryW(Path.Combine(root, "MSVCR110.dll"));
                    if (msvcr == IntPtr.Zero) throw new InvalidOperationException("MSVCR110.dll is not loaded by the VOCALOID engine.");
                    IntPtr nativePart = Bind<DynamicCast>(msvcr, "__RTDynamicCast")(part, 0, IntPtr.Add(vsqModule, 0x8f3d0), IntPtr.Add(vsqModule, 0x8fd9c), 0);
                    if (nativePart == IntPtr.Zero) throw new InvalidDataException("Could not cast the first VSQ part to IVSMusicalPart.");

                    timeMap = Bind<CreateTimeMap>(dscModule, "?CreateVSSequenceTimeMap@CxDaisyMidiFile@@SAPAUtagSequenceTimeMap@@PBVIVSMasterTrack@@@Z")(master);
                    if (timeMap == IntPtr.Zero) throw new InvalidOperationException("DSCL4 could not create a sequence time map.");
                    encoder = Marshal.AllocHGlobal(0x80);
                    for (int i = 0; i < 0x80; i += 4) Marshal.WriteInt32(encoder, i, 0);
                    Bind<Ctor>(dscModule, "??0CxDaisyMidiFile@@QAE@XZ")(encoder);
                    args = Marshal.AllocHGlobal(8);
                    Marshal.WriteIntPtr(args, 0, nativePart);
                    Marshal.WriteIntPtr(args, 4, timeMap);
                    daisy = Bind<EncodePart>(dscModule, "?EncodeMusicalPart@CxDaisyMidiFile@@QAEPAVCDaisyMidiBuffer@@PAUtagDaisyMidiV3EncodeStruct@@@Z")(encoder, args);
                    if (daisy == IntPtr.Zero) throw new InvalidDataException("DSCL4 could not encode the first musical part.");
                    NativePhrase result = new NativePhrase(daisy);
                    daisy = IntPtr.Zero;
                    return result;
                }
                finally
                {
                    if (daisy != IntPtr.Zero) DeleteBuffer(daisy);
                    if (args != IntPtr.Zero) Marshal.FreeHGlobal(args);
                    if (encoder != IntPtr.Zero)
                    {
                        try { Bind<Dtor>(dscModule, "??1CxDaisyMidiFile@@UAE@XZ")(encoder); } finally { Marshal.FreeHGlobal(encoder); }
                    }
                    if (timeMap != IntPtr.Zero)
                    {
                        IntPtr cr = GetModuleHandleW("MSVCR110.dll");
                        if (cr != IntPtr.Zero) Bind<DeleteArray>(cr, "??_V@YAXPAX@Z")(timeMap);
                    }
                    Marshal.FreeHGlobal(path);
                }
            }
        }

        private void InitializeManager()
        {
            IntPtr policy = Marshal.AllocHGlobal(PolicySize), policyRoot = Marshal.StringToHGlobalUni(root);
            try
            {
                for (int i = 0; i < PolicySize; i += 4) Marshal.WriteInt32(policy, i, 0);
                int[] limits = { -1, 16, -1, 1, 1, -1, 1, -1, -1, 60, -1, -1 };
                for (int i = 0; i < limits.Length; i++) Marshal.WriteInt32(policy, i * 4, limits[i]);
                Marshal.WriteInt32(policy, 0x20, -1);
                Marshal.WriteInt32(policy, 0x24, 60);
                Marshal.WriteInt32(policy, 0x28, -1);
                Marshal.WriteInt32(policy, 0x2c, -1);
                Marshal.WriteIntPtr(policy, 0x30, policyRoot);
                Marshal.WriteInt32(policy, 0x40, root.Length);
                Marshal.WriteInt32(policy, 0x44, root.Length);
                int result = Bind<Initialize>(vsqModule, "?sysInitialize@CVSVsqManagerIF@@QAEHABUVSVsqManagementPolicy@@@Z")(manager, policy);
                if (result != 1 || Bind<IsInitialized>(vsqModule, "?sysIsInitialized@CVSVsqManagerIF@@QBEHXZ")(manager) == 0)
                    throw new InvalidOperationException("VOCALOID frontend sysInitialize failed (" + result + ").");
            }
            finally { Marshal.FreeHGlobal(policyRoot); Marshal.FreeHGlobal(policy); }
        }

        private IntPtr FindFirstMusicalPart(IntPtr rootObject)
        {
            IntPtr sequence = CallVirtual(rootObject, 5);
            if (sequence == IntPtr.Zero) throw new InvalidDataException("VSQX has no sequence.");
            IntPtr tracks = CallVirtual(sequence, 8);
            IntPtr track = FirstListItem(tracks, "track");
            IntPtr parts = CallVirtual(track, 25);
            return FirstListItem(parts, "musical part");
        }

        private static IntPtr CallVirtual(IntPtr instance, int slot)
        {
            if (instance == IntPtr.Zero) throw new InvalidDataException("VSQX object tree is incomplete.");
            IntPtr function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * 4);
            return Marshal.GetDelegateForFunctionPointer<NoArgsPtr>(function)(instance);
        }

        private static IntPtr FirstListItem(IntPtr list, string kind)
        {
            if (list == IntPtr.Zero) throw new InvalidDataException("VSQX has no " + kind + " list.");
            IntPtr vtable = Marshal.ReadIntPtr(list);
            int count = Marshal.GetDelegateForFunctionPointer<ListCount>(Marshal.ReadIntPtr(vtable, 0x14))(list);
            if (count < 1) throw new InvalidDataException("VSQX has no " + kind + ".");
            return Marshal.GetDelegateForFunctionPointer<ListAt>(Marshal.ReadIntPtr(vtable, 0x18))(list, 0);
        }

        private static T Bind<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException("Missing VOCALOID frontend export: " + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        internal static void DeleteBuffer(IntPtr buffer)
        {
            IntPtr vtable = Marshal.ReadIntPtr(buffer);
            Marshal.GetDelegateForFunctionPointer<DeleteDaisy>(Marshal.ReadIntPtr(vtable))(buffer, 1);
        }

        internal static void ReleaseBuffer(IntPtr buffer)
        {
            lock (NativeLock) DeleteBuffer(buffer);
        }

        public void Dispose()
        {
            lock (NativeLock)
            {
                if (disposed) return;
                disposed = true;
                try { Bind<Dtor>(vsqModule, "??1CVSVsqManagerIF@@QAE@XZ")(manager); }
                finally { Marshal.FreeHGlobal(manager); }
            }
        }

    }

    public sealed class NativePhrase : IDisposable
    {
        private IntPtr buffer;
        public IntPtr DaisyBuffer { get { if (buffer == IntPtr.Zero) throw new ObjectDisposedException("NativePhrase"); return buffer; } }
        internal NativePhrase(IntPtr daisyBuffer) { buffer = daisyBuffer; }
        public void Dispose()
        {
            IntPtr value = System.Threading.Interlocked.Exchange(ref buffer, IntPtr.Zero);
            if (value != IntPtr.Zero) NativeFrontend.ReleaseBuffer(value);
        }
    }
}
