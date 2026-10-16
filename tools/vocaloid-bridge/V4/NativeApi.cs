using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VocaloidBridge.V4
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DseMidiEventRecord
    {
        public int SampleOffsetIn256;
        public uint PackedMidiMessage;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct DsePcmBuffer
    {
        // In: sample capacity. Out: number of int16 samples written.
        public int SampleCount;
        public IntPtr Samples;
    }

    // ABI facts are from the installed x86 DSE4.dll and DSCL4.dll call sites.
    // This class deliberately does not load or interpret any VoiceDB record.
    public sealed class NativeApi : IDisposable
    {
        public const int V4Token = 0x13000202;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetVersionDelegate(out byte major, out byte minor, out byte revision, out byte build, ref double compatibility);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SetDbmPathDelegate(IntPtr path1, IntPtr path2);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SetVVoiceTableV4Delegate(int token, IntPtr countAndRecords);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int CreateDelegate(int token);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int DeleteDelegate(uint instance);

        // DSCL's typed function pointer and stack call establish these eight x86
        // words. The DSE decompiler groups the middle six as three int64 values;
        // expose the exact caller layout to prevent CLR packing ambiguity. The
        // output pointer is opaque and must not be dereferenced here.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SetStaticSettingDelegate(uint instance, int sampleRate, int frameSize, IntPtr voicePath, int option0, int option1, int option2, out IntPtr voiceTable);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int StartDelegate(uint instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int StopDelegate(uint instance);

        // DSCL's caller supplies an event count and a pointer to 8-byte event
        // records. The output descriptor has an int32 capacity/count and int16*.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int DoStepSynthesisDelegate(uint instance, int eventCount, IntPtr eventRecords, ref DsePcmBuffer outputDescriptor);

        private IntPtr module;

        public string LibraryPath { get; private set; }
        public GetVersionDelegate GetVersion { get; private set; }
        public SetDbmPathDelegate SetDbmPath { get; private set; }
        public SetVVoiceTableV4Delegate SetVVoiceTableV4 { get; private set; }
        public CreateDelegate Create { get; private set; }
        public DeleteDelegate Delete { get; private set; }
        public SetStaticSettingDelegate SetStaticSetting { get; private set; }
        public StartDelegate Start { get; private set; }
        public StopDelegate Stop { get; private set; }
        public DoStepSynthesisDelegate DoStepSynthesis { get; private set; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectoryW(string path);

        private NativeApi(string root, IntPtr loadedModule)
        {
            LibraryPath = Path.Combine(root, "DSE4.dll");
            module = loadedModule;
            GetVersion = Bind<GetVersionDelegate>("DSEGetVersion");
            SetDbmPath = Bind<SetDbmPathDelegate>("DSESetDBMPath");
            SetVVoiceTableV4 = Bind<SetVVoiceTableV4Delegate>("DSESetVVoiceTableV4");
            Create = Bind<CreateDelegate>("DSECreate");
            Delete = Bind<DeleteDelegate>("DSEDelete");
            SetStaticSetting = Bind<SetStaticSettingDelegate>("DSESetStaticSetting");
            Start = Bind<StartDelegate>("DSEStart");
            Stop = Bind<StopDelegate>("DSEStop");
            DoStepSynthesis = Bind<DoStepSynthesisDelegate>("DSEDoStepSynthesis");
        }

        public static NativeApi Load(string root)
        {
            if (IntPtr.Size != 4)
                throw new InvalidOperationException("Vocaloid4 DSE4.dll requires a 32-bit process.");
            if (String.IsNullOrEmpty(root))
                throw new ArgumentException("A Vocaloid4P installation directory is required.", "root");
            root = Path.GetFullPath(root);
            string library = Path.Combine(root, "DSE4.dll");
            if (!File.Exists(library))
                throw new FileNotFoundException("DSE4.dll was not found in the configured installation directory.", library);
            Directory.SetCurrentDirectory(root);
            if (!SetDllDirectoryW(root))
                throw new InvalidOperationException("SetDllDirectory failed: " + Marshal.GetLastWin32Error());
            IntPtr loaded = LoadLibraryW(library);
            if (loaded == IntPtr.Zero)
                throw new InvalidOperationException("LoadLibrary(DSE4.dll) failed: " + Marshal.GetLastWin32Error());
            try
            {
                return new NativeApi(root, loaded);
            }
            catch
            {
                FreeLibrary(loaded);
                throw;
            }
        }

        private T Bind<T>(string symbol) where T : class
        {
            IntPtr address = GetProcAddress(module, symbol);
            if (address == IntPtr.Zero)
                throw new MissingMethodException("DSE4.dll export is missing: " + symbol);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        public void Dispose()
        {
            if (module != IntPtr.Zero) {
                FreeLibrary(module);
                module = IntPtr.Zero;
            }
        }
    }

    [DataContract]
    internal sealed class DiagnosticResult
    {
        [DataMember(Name = "ok")] public bool Ok;
        [DataMember(Name = "processBits")] public int ProcessBits;
        [DataMember(Name = "libraryPath")] public string LibraryPath;
        [DataMember(Name = "apiVersionCall")] public int? ApiVersionCall;
        [DataMember(Name = "reportedVersion")] public string ReportedVersion;
        [DataMember(Name = "compatibilityToken")] public string CompatibilityToken;
        [DataMember(Name = "createResult")] public int? CreateResult;
        [DataMember(Name = "voiceTableRegistrationResult")] public int? VoiceTableRegistrationResult;
        [DataMember(Name = "staticSettingResult")] public int? StaticSettingResult;
        [DataMember(Name = "voiceTableAvailable")] public bool VoiceTableAvailable;
        [DataMember(Name = "deleteResult")] public int? DeleteResult;
        [DataMember(Name = "error")] public string Error;
        [DataMember(Name = "notes")] public string[] Notes;
    }

    public static class V4DiagnosticHost
    {
        private static void WriteJson(DiagnosticResult value)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(DiagnosticResult));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                Console.Out.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }

        public static int Main(string[] args)
        {
            DiagnosticResult result = new DiagnosticResult();
            result.ProcessBits = IntPtr.Size * 8;
            result.Notes = new string[] {
                "Diagnostic only: no voice table records, score, or PCM rendering were read or attempted.",
                "Engine DLLs and voice data are loaded in place; DBM may create its normal user settings.",
                "DSESetDBMPath is optional in this diagnostic and its result is not used to judge engine availability."
            };
            try
            {
                if (args.Length != 1)
                    throw new ArgumentException("Usage: V4DiagnosticHost.exe <Vocaloid4P-root>");
                using (NativeApi api = NativeApi.Load(args[0]))
                {
                    result.LibraryPath = api.LibraryPath;
                    byte major, minor, revision, build;
                    double compatibility = 20012012.0;
                    int versionCall = api.GetVersion(out major, out minor, out revision, out build, ref compatibility);
                    result.ApiVersionCall = versionCall;
                    result.ReportedVersion = major + "." + minor + "." + revision + "." + build;
                    int reportedToken = major | (minor << 8) | (revision << 16) | (build << 24);
                    result.CompatibilityToken = reportedToken.ToString("X8");
                    if (versionCall == 0 && reportedToken == NativeApi.V4Token)
                    {
                        result.VoiceTableRegistrationResult = api.SetVVoiceTableV4(NativeApi.V4Token, IntPtr.Zero);
                        int instance = api.Create(NativeApi.V4Token);
                        result.CreateResult = instance;
                        if (instance >= 0)
                        {
                            IntPtr voicePath = Marshal.StringToHGlobalAnsi(String.Empty);
                            try
                            {
                                IntPtr voiceTable;
                                result.StaticSettingResult = api.SetStaticSetting(
                                    (uint)instance, 44100, 256, voicePath, 0, 1, 0, out voiceTable);
                                result.VoiceTableAvailable = voiceTable != IntPtr.Zero;
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(voicePath);
                                result.DeleteResult = api.Delete((uint)instance);
                            }
                        }
                    }
                    result.Ok = versionCall == 0 && result.VoiceTableRegistrationResult.HasValue && result.VoiceTableRegistrationResult.Value == 0 &&
                        result.CreateResult.HasValue && result.CreateResult.Value >= 0 &&
                        result.StaticSettingResult.HasValue && result.StaticSettingResult.Value == 0 && result.VoiceTableAvailable &&
                        result.DeleteResult.HasValue && result.DeleteResult.Value == 0;
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.GetType().FullName + ": " + ex.Message;
            }
            WriteJson(result);
            return result.Ok ? 0 : 1;
        }
    }
}
