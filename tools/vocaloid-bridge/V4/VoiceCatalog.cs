using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VocaloidBridge.V4
{
    /// <summary>A VOCALOID virtual voice selected by its MIDI bank/program pair.</summary>
    public sealed class NativeVoiceInfo
    {
        public string ID { get; private set; }
        public string Name { get; private set; }
        public int Bank { get; private set; }
        public int Program { get; private set; }
        public string ComponentId { get; private set; }

        internal NativeVoiceInfo(string id, string name, int bank, int program, string componentId)
        {
            ID = id;
            Name = name;
            Bank = bank;
            Program = program;
            ComponentId = componentId;
        }
    }

    /// <summary>
    /// Reads the installed VOCALOID 4 virtual-voice catalog through dbm4.dll's
    /// exported APIs. This must run in an x86 helper process like DSE4.dll.
    /// </summary>
    public static class VoiceCatalog
    {
        private const int MaxBank = 127;
        private const int MaxProgram = 127;
        private const int NameCapacity = 256;
        private const int VoiceInfoCapacity = 4096;
        private const int ComponentIdOffset = 1068;
        private const int NotFound = unchecked((int)0x830A5004);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetVirtualVoiceNameDelegate(int bank, int program, IntPtr name, int capacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetVirtualVoiceInfoDelegate(int bank, int program, IntPtr voiceInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectoryW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetDllDirectoryW(uint bufferLength, System.Text.StringBuilder buffer);

        /// <summary>
        /// Enumerates installed voices in bank/program order. The catalog key
        /// ID is "bank:program" so composite voices remain distinct even when
        /// they share a primary component ID.
        /// </summary>
        public static IReadOnlyList<NativeVoiceInfo> Enumerate(string vocaloidRoot)
        {
            if (IntPtr.Size != 4)
                throw new InvalidOperationException("dbm4.dll requires a 32-bit helper process.");
            if (String.IsNullOrWhiteSpace(vocaloidRoot))
                throw new ArgumentException("A VOCALOID 4 installation directory is required.", "vocaloidRoot");

            string root = Path.GetFullPath(vocaloidRoot);
            string libraryPath = Path.Combine(root, "dbm4.dll");
            if (!File.Exists(libraryPath))
                throw new FileNotFoundException("dbm4.dll was not found in the configured installation directory.", libraryPath);

            string originalDllDirectory = GetDllDirectory();
            if (!SetDllDirectoryW(root))
                throw new InvalidOperationException("SetDllDirectory failed: " + Marshal.GetLastWin32Error());

            IntPtr module = IntPtr.Zero;
            IntPtr nameBuffer = IntPtr.Zero;
            IntPtr voiceInfo = IntPtr.Zero;
            try
            {
                module = LoadLibraryW(libraryPath);
                if (module == IntPtr.Zero)
                    throw new InvalidOperationException("LoadLibrary(dbm4.dll) failed: " + Marshal.GetLastWin32Error());

                InitDelegate init = Bind<InitDelegate>(module, "DBM_Init");
                GetVirtualVoiceNameDelegate getName = Bind<GetVirtualVoiceNameDelegate>(module, "DBM_GetVirtualVoiceName");
                GetVirtualVoiceInfoDelegate getInfo = Bind<GetVirtualVoiceInfoDelegate>(module, "DBM_GetVirtualVoiceInfo");

                int initResult = init();
                if (initResult != 0)
                    throw new InvalidOperationException("DBM_Init failed: " + FormatResult(initResult));

                nameBuffer = Marshal.AllocHGlobal(NameCapacity * sizeof(char));
                voiceInfo = Marshal.AllocHGlobal(VoiceInfoCapacity);
                var voices = new List<NativeVoiceInfo>();
                for (int bank = 0; bank <= MaxBank; bank++)
                {
                    for (int program = 0; program <= MaxProgram; program++)
                    {
                        Clear(nameBuffer, NameCapacity * sizeof(char));
                        int nameResult = getName(bank, program, nameBuffer, NameCapacity);
                        if (nameResult == NotFound)
                            continue;
                        if (nameResult != 0)
                            throw new InvalidOperationException("DBM_GetVirtualVoiceName failed for " + bank + ":" + program + ": " + FormatResult(nameResult));

                        string name = Marshal.PtrToStringUni(nameBuffer) ?? String.Empty;
                        if (name.Length == 0)
                            throw new InvalidDataException("DBM_GetVirtualVoiceName returned an empty name for " + bank + ":" + program + ".");

                        // DSCL4's CVoiceDB::GetVoiceData/CreateVoiceDataMap call
                        // DBM_GetVirtualVoiceInfo(bank, program, outInfo). Its
                        // wrapper copies the component ID into wchar[260] at
                        // byte offset 1068. Keep the rest of that private info
                        // record opaque.
                        Clear(voiceInfo, VoiceInfoCapacity);
                        int infoResult = getInfo(bank, program, voiceInfo);
                        if (infoResult != 0)
                            throw new InvalidOperationException("DBM_GetVirtualVoiceInfo failed for " + bank + ":" + program + ": " + FormatResult(infoResult));
                        string componentId = Marshal.PtrToStringUni(IntPtr.Add(voiceInfo, ComponentIdOffset)) ?? String.Empty;
                        if (componentId.Length == 0)
                            throw new InvalidDataException("DBM_GetVirtualVoiceInfo returned an empty component ID for " + bank + ":" + program + ".");

                        voices.Add(new NativeVoiceInfo(
                            bank.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + program.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            name, bank, program, componentId));
                    }
                }
                return voices.AsReadOnly();
            }
            finally
            {
                if (voiceInfo != IntPtr.Zero) Marshal.FreeHGlobal(voiceInfo);
                if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
                if (module != IntPtr.Zero) FreeLibrary(module);
                SetDllDirectoryW(String.IsNullOrEmpty(originalDllDirectory) ? null : originalDllDirectory);
            }
        }

        private static string GetDllDirectory()
        {
            var buffer = new System.Text.StringBuilder(32768);
            uint length = GetDllDirectoryW((uint)buffer.Capacity, buffer);
            if (length == 0 || length >= buffer.Capacity)
                return String.Empty;
            return buffer.ToString();
        }

        private static void Clear(IntPtr memory, int byteCount)
        {
            for (int offset = 0; offset < byteCount; offset++)
                Marshal.WriteByte(memory, offset, 0);
        }

        private static T Bind<T>(IntPtr module, string symbol) where T : class
        {
            IntPtr address = GetProcAddress(module, symbol);
            if (address == IntPtr.Zero)
                throw new MissingMethodException("dbm4.dll export is missing: " + symbol);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static string FormatResult(int result)
        {
            return result + " (0x" + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }
}
