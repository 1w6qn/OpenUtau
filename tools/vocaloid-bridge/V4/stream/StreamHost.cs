using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace VocaloidBridge.V4
{
    /// <summary>
    /// Standalone streaming driver: renders a window of one phrase with the
    /// installed VOCALOID 4 engine, paced to the wall clock when asked, and
    /// reports frame timings so "can this play in real time" is measurable.
    /// Usage: StreamHost.exe config.json request.json startSeconds seconds out.wav [pace]
    /// </summary>
    public static class StreamHost
    {
        public static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception error)
            {
                string log = Path.Combine(Path.GetTempPath(), "streamhost-error.txt");
                File.WriteAllText(log, error.ToString());
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 3;
            }
        }

        static int Run(string[] args)
        {
            if (args.Length < 5)
            {
                Console.Error.WriteLine("usage: StreamHost.exe <config> <request> <startSeconds> <seconds> <out.wav> [pace]");
                return 2;
            }
            string configPath = Path.GetFullPath(args[0]);
            string requestPath = Path.GetFullPath(args[1]);
            // Accept either seconds (e.g. 3.0) or absolute frame counts (e.g. f516)
            // so a caller can request contiguous, non-overlapping windows.
            double startSeconds = args[2].StartsWith("f", StringComparison.Ordinal)
                ? 0.0 : Double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            double seconds = args[3].StartsWith("f", StringComparison.Ordinal)
                ? 0.0 : Double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            string outputPath = Path.GetFullPath(args[4]);
            bool pace = args.Length > 5 && args[5] == "pace";

            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 };
            var config = json.Deserialize<V4Config>(File.ReadAllText(configPath));
            var request = json.Deserialize<V4Request>(File.ReadAllText(requestPath));
            if (String.IsNullOrEmpty(request.VoiceId)) request.VoiceId = config.VoiceId;
            if (String.IsNullOrEmpty(request.VoiceId)) throw new InvalidDataException("A voice ID is required (request or config).");
            string root = Path.GetFullPath(config.EngineDirectory);

            using (var api = NativeApi.Load(root))
            {
                byte a, b, c, d;
                double compatibility = 20012012.0;
                int version = api.GetVersion(out a, out b, out c, out d, ref compatibility);
                if (version != 0 || (a | (b << 8) | (c << 16) | (d << 24)) != NativeApi.V4Token)
                    throw new InvalidOperationException("Unsupported V4 private ABI version.");

                IntPtr registryPattern = Marshal.StringToHGlobalAnsi("SOFTWARE\\VCLDASGN3\\%s");
                IntPtr databasePath = Marshal.StringToHGlobalAnsi(root);
                try { api.SetDbmPath(registryPattern, databasePath); }
                finally { Marshal.FreeHGlobal(registryPattern); Marshal.FreeHGlobal(databasePath); }
                Check(api.SetVVoiceTableV4(NativeApi.V4Token, IntPtr.Zero), "DSESetVVoiceTableV4");

                NativeVoiceInfo voice = null;
                foreach (NativeVoiceInfo candidate in VoiceCatalog.Enumerate(root))
                {
                    if (String.Equals(candidate.ID, request.VoiceId, StringComparison.Ordinal)) { voice = candidate; break; }
                }
                if (voice == null) throw new InvalidDataException("Installed V4 voice not found: " + request.VoiceId);
                if (voice.Bank != 4) throw new NotSupportedException("Streaming probe supports Mandarin voices (Bank 4).");

                int handle = api.Create(NativeApi.V4Token);
                if (handle < 0) throw new InvalidOperationException("DSECreate failed: " + handle);
                IntPtr emptyPath = Marshal.StringToHGlobalAnsi("");
                string scratch = Path.Combine(Path.GetTempPath(), "vocaloid-stream-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                string score = Path.Combine(scratch, "phrase.vsqx");
                IntPtr phraseBuffer = IntPtr.Zero;
                try
                {
                    IntPtr table;
                    Check(api.SetStaticSetting((uint)handle, 44100, 256, emptyPath, 0, 1, 0, out table), "DSESetStaticSetting");

                    var g2p = new NativeChineseG2p(root);
                    var frontend = new NativeFrontend(root);
                    try
                    {
                        V4ScoreWriter.Write(score, request, voice, g2p, config.TailMs);
                        using (var phrase = frontend.Load(score))
                        {
                            var stats = new RenderStats { BudgetMs = 1000.0 * 256 / 44100.0 };
                            int startFrame = args[2].StartsWith("f", StringComparison.Ordinal)
                                ? Int32.Parse(args[2].Substring(1))
                                : (int)Math.Round(startSeconds * 44100.0 / 256.0);
                            int windowFrames = args[3].StartsWith("f", StringComparison.Ordinal)
                                ? Math.Max(1, Int32.Parse(args[3].Substring(1)))
                                : Math.Max(1, (int)Math.Round(seconds * 44100.0 / 256.0));
                            var wall = Stopwatch.StartNew();
                            var audio = NativeSynthesis.Render(api, (uint)handle, phrase.DaisyBuffer, null, stats, pace, startFrame, windowFrames);
                            wall.Stop();
                            WriteFloatWav(outputPath, audio.Samples, audio.SampleRate);
                            var payload = new Dictionary<string, object>
                            {
                                {"start_seconds", audio.StartSeconds},
                                {"seconds", (double)audio.Samples.Length / audio.SampleRate},
                                {"frames", stats.Frames},
                                {"budget_ms", stats.BudgetMs},
                                {"p50_ms", stats.Percentile(0.5)},
                                {"p99_ms", stats.Percentile(0.99)},
                                {"max_ms", stats.MaxMs},
                                {"late_frames", stats.LateFrames},
                                {"realtime_factor", stats.RealtimeFactor},
                                {"wall_seconds", wall.Elapsed.TotalSeconds},
                                {"paced", pace},
                                {"output", outputPath}
                            };
                            Console.Out.WriteLine(json.Serialize(payload));
                        }
                    }
                    finally
                    {
                        g2p.Dispose();
                        frontend.Dispose();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(emptyPath);
                    api.Delete((uint)handle);
                    if (File.Exists(score)) File.Delete(score);
                    if (Directory.Exists(scratch) && Directory.GetFileSystemEntries(scratch).Length == 0) Directory.Delete(scratch);
                    if (phraseBuffer != IntPtr.Zero) Marshal.FreeHGlobal(phraseBuffer);
                }
            }
            return 0;
        }

        static void Check(int code, string name)
        {
            if (code != 0) throw new InvalidOperationException(name + " failed: " + code);
        }

        static void WriteFloatWav(string path, float[] samples, int rate)
        {
            using (var stream = new BinaryWriter(File.Create(path)))
            {
                int bytes = samples.Length * 4;
                stream.Write(Encoding.ASCII.GetBytes("RIFF")); stream.Write(bytes + 36);
                stream.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); stream.Write(16);
                stream.Write((short)3); stream.Write((short)1); stream.Write(rate); stream.Write(rate * 4);
                stream.Write((short)4); stream.Write((short)32);
                stream.Write(Encoding.ASCII.GetBytes("data")); stream.Write(bytes);
                foreach (float sample in samples) stream.Write(sample);
            }
        }
    }
}
