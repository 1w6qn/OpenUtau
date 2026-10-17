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
    /// Persistent frame server: holds one loaded phrase and serves sequential PCM
    /// windows over stdin/stdout so a client (OpenUtau's audio source) can stream
    /// in real time.
    /// Protocol: one JSON line per command; a "read" reply is a JSON line followed
    /// by exactly (samples * 2) bytes of little-endian int16 PCM.
    /// </summary>
    public static class StreamServer
    {
        const int SampleRate = 44100;
        const int FrameSize = 256;

        public static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception error)
            {
                string log = Path.Combine(Path.GetTempPath(), "streamserver-error.txt");
                File.WriteAllText(log, error.ToString());
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 3;
            }
        }

        static int Run(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: StreamServer.exe <config> <request>"); return 2; }
            var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };
            var config = json.Deserialize<V4Config>(File.ReadAllText(Path.GetFullPath(args[0])));
            var request = json.Deserialize<V4Request>(File.ReadAllText(Path.GetFullPath(args[1])));
            if (String.IsNullOrEmpty(request.VoiceId)) request.VoiceId = config.VoiceId;
            string root = Path.GetFullPath(config.EngineDirectory);

            using (var api = NativeApi.Load(root))
            {
                byte a, b, c, d; double compatibility = 20012012.0;
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
                    if (String.Equals(candidate.ID, request.VoiceId, StringComparison.Ordinal)) { voice = candidate; break; }
                if (voice == null) throw new InvalidDataException("Installed V4 voice not found: " + request.VoiceId);
                if (voice.Bank != 4) throw new NotSupportedException("Streaming server supports Mandarin voices (Bank 4).");

                int handle = api.Create(NativeApi.V4Token);
                if (handle < 0) throw new InvalidOperationException("DSECreate failed: " + handle);
                IntPtr emptyPath = Marshal.StringToHGlobalAnsi("");
                string scratch = Path.Combine(Path.GetTempPath(), "vocaloid-server-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                string score = Path.Combine(scratch, "phrase.vsqx");
                NativeFrontend frontend = null;
                FrameStream stream = null;
                try
                {
                    IntPtr table;
                    Check(api.SetStaticSetting((uint)handle, SampleRate, FrameSize, emptyPath, 0, 1, 0, out table), "DSESetStaticSetting");
                    var g2p = new NativeChineseG2p(root);
                    frontend = new NativeFrontend(root);
                    try
                    {
                        V4ScoreWriter.Write(score, request, voice, g2p, config.TailMs);
                        var phrase = frontend.Load(score);
                        stream = new FrameStream(api, (uint)handle, phrase.DaisyBuffer);
                        stream.PreRoll(0);
                        var stdout = Console.OpenStandardOutput();
                        var budget = 1000.0 * FrameSize / SampleRate;
                        var clock = Stopwatch.StartNew();
                        long anchorFrame = 0;
                        clock.Restart();
                        var counters = new List<double>();
                        WriteLine(stdout, json.Serialize(new Dictionary<string, object> {
                            {"ok", true}, {"ready", true}, {"sample_rate", SampleRate}, {"frame_size", FrameSize},
                            {"total_frames", stream.TotalFrames}, {"start_seconds", stream.StartSeconds}, {"budget_ms", budget}
                        }));
                        string line;
                        while ((line = Console.In.ReadLine()) != null)
                        {
                            if (line.Trim().Length == 0) continue;
                            var command = json.Deserialize<Dictionary<string, object>>(line);
                            string op = Convert.ToString(command.ContainsKey("op") ? command["op"] : "ping");
                            if (op == "close") { WriteLine(stdout, json.Serialize(new Dictionary<string, object> { { "ok", true }, { "closed", true } })); break; }
                            if (op == "ping") { WriteLine(stdout, json.Serialize(new Dictionary<string, object> { { "ok", true }, { "pong", true }, { "frame", stream.FrameIndex } })); continue; }
                            if (op == "seek")
                            {
                                long target = Convert.ToInt64(command.ContainsKey("frame") ? command["frame"] : 0);
                                if (stream != null) { stream.Dispose(); stream = null; }
                                // DSE keeps voice state across Start/Stop, so a seek needs a
                                // fresh engine instance to match the batch render exactly.
                                api.Delete((uint)handle);
                                handle = api.Create(NativeApi.V4Token);
                                if (handle < 0) throw new InvalidOperationException("DSECreate failed on seek: " + handle);
                                IntPtr freshTable;
                                Check(api.SetStaticSetting((uint)handle, SampleRate, FrameSize, emptyPath, 0, 1, 0, out freshTable), "DSESetStaticSetting(seek)");
                                stream = new FrameStream(api, (uint)handle, phrase.DaisyBuffer);
                                var watch = Stopwatch.StartNew();
                                stream.PreRoll(target);
                                watch.Stop();
                                clock.Restart();
                                anchorFrame = stream.FrameIndex;
                                WriteLine(stdout, json.Serialize(new Dictionary<string, object> {
                                    {"ok", true}, {"frame", stream.FrameIndex}, {"preroll_ms", watch.Elapsed.TotalMilliseconds}
                                }));
                                continue;
                            }
                            if (op == "read")
                            {
                                int frames = Convert.ToInt32(command.ContainsKey("frames") ? command["frames"] : 1);
                                bool pace = command.ContainsKey("pace") && Convert.ToBoolean(command["pace"]);
                                var samples = new List<float>();
                                var watch = Stopwatch.StartNew();
                                stream.ReadFrames(frames, samples);
                                watch.Stop();
                                long startFrame = stream.FrameIndex - (samples.Count / FrameSize);
                                counters.Add(watch.Elapsed.TotalMilliseconds);
                                double wall = 0.0;
                                if (pace)
                                {
                                    double target = (stream.FrameIndex - anchorFrame) * budget;
                                    double remaining = target - clock.Elapsed.TotalMilliseconds;
                                    if (remaining > 0.5) System.Threading.Thread.Sleep((int)remaining);
                                    wall = clock.Elapsed.TotalMilliseconds;
                                }
                                WriteLine(stdout, json.Serialize(new Dictionary<string, object> {
                                    {"ok", true}, {"start_frame", startFrame}, {"frames", samples.Count / FrameSize},
                                    {"samples", samples.Count}, {"render_ms", watch.Elapsed.TotalMilliseconds},
                                    {"paced", pace}, {"wall_ms", wall}, {"budget_ms", budget}
                                }));
                                var pcm = new byte[samples.Count * 2];
                                for (int i = 0; i < samples.Count; i++)
                                {
                                    double clamped = Math.Max(-1.0, Math.Min(1.0, samples[i]));
                                    short value = (short)Math.Round(clamped * 32767);
                                    pcm[i * 2] = (byte)(value & 0xFF);
                                    pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
                                }
                                stdout.Write(pcm, 0, pcm.Length);
                                stdout.Flush();
                                continue;
                            }
                            WriteLine(stdout, json.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "unknown op" } }));
                        }
                    }
                    finally { if (stream != null) stream.Dispose(); if (frontend != null) frontend.Dispose(); g2p.Dispose(); }
                }
                finally
                {
                    Marshal.FreeHGlobal(emptyPath);
                    api.Delete((uint)handle);
                    if (File.Exists(score)) File.Delete(score);
                    if (Directory.Exists(scratch) && Directory.GetFileSystemEntries(scratch).Length == 0) Directory.Delete(scratch);
                }
            }
            return 0;
        }

        static void WriteLine(Stream stdout, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }

        static void Check(int code, string name) { if (code != 0) throw new InvalidOperationException(name + " failed: " + code); }
    }
}
