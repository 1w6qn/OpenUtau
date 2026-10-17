using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VocaloidBridge {
    /// <summary>
    /// Client for the persistent VOCALOID frame server (tools/vocaloid-bridge/V4/run-stream-server.ps1).
    ///
    /// The server renders the phrase with the installed engine, one 256-sample frame at a
    /// time, and returns PCM windows on demand. Sequential reads continue the engine state,
    /// so a playback engine can stream in real time instead of waiting for a whole phrase.
    /// Measured on the development machine: 5 sequential windows concatenate to the batch
    /// render sample for sample, seek reproduces the same slice, and paced reads track the
    /// wall clock 1:1.
    ///
    /// Protocol: one JSON line per command; a "read" reply is a JSON line followed by
    /// exactly (samples * 2) bytes of little-endian int16 PCM.
    /// </summary>
    public sealed class VocaloidFrameSource : IDisposable {
        readonly Process process;
        readonly Stream input;
        readonly Stream output;
        readonly object gate = new object();
        bool disposed;

        public int SampleRate { get; private set; }
        public int FrameSize { get; private set; } = 256;
        public long TotalFrames { get; private set; }
        public double BudgetMs { get; private set; }
        public long FrameIndex { get; private set; }

        public VocaloidFrameSource(string hostExecutable, string hostScript, string configPath, string requestPath) {
            foreach (var file in new[] { hostExecutable, hostScript, configPath, requestPath }) {
                if (!File.Exists(file)) throw new FileNotFoundException("Frame server input was not found.", file);
            }
            var info = new ProcessStartInfo(hostExecutable) {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-ExecutionPolicy");
            info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(hostScript);
            info.ArgumentList.Add("-Config");
            info.ArgumentList.Add(configPath);
            info.ArgumentList.Add("-Request");
            info.ArgumentList.Add(requestPath);
            process = new Process { StartInfo = info };
            process.Start();
            input = process.StandardInput.BaseStream;
            output = process.StandardOutput.BaseStream;
            var ready = ReadJsonLine();
            if (ready?["ready"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Frame server did not report readiness: " + ready?.ToJsonString());
            SampleRate = ready!["sample_rate"]!.GetValue<int>();
            FrameSize = ready["frame_size"]!.GetValue<int>();
            TotalFrames = ready["total_frames"]!.GetValue<long>();
            BudgetMs = ready["budget_ms"]!.GetValue<double>();
        }

        /// <summary>Restart the engine and advance to the given absolute frame.</summary>
        public double Seek(long frame) {
            lock (gate) {
                var reply = Command(new JsonObject { ["op"] = "seek", ["frame"] = frame });
                if (reply?["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("Frame server seek failed.");
                FrameIndex = reply["frame"]!.GetValue<long>();
                return reply["preroll_ms"]?.GetValue<double>() ?? 0.0;
            }
        }

        /// <summary>
        /// Read the next window as mono float samples. When pace is true the server sleeps
        /// so the window finishes on the wall-clock budget for its frames.
        /// </summary>
        public float[] Read(int frames, bool pace) {
            lock (gate) {
                var reply = Command(new JsonObject { ["op"] = "read", ["frames"] = frames, ["pace"] = pace });
                if (reply?["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException("Frame server read failed.");
                var samples = reply["samples"]!.GetValue<int>();
                var pcm = ReadExactly(samples * 2);
                FrameIndex = reply["start_frame"]!.GetValue<long>() + reply["frames"]!.GetValue<long>();
                var result = new float[samples];
                for (var i = 0; i < samples; i++) {
                    short value = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                    result[i] = value / 32768f;
                }
                return result;
            }
        }

        public void Close() {
            lock (gate) {
                try { Command(new JsonObject { ["op"] = "close" }); } catch { }
            }
        }

        JsonNode? Command(JsonObject payload) {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString() + "\n");
            input.Write(bytes, 0, bytes.Length);
            input.Flush();
            return ReadJsonLine();
        }

        JsonNode? ReadJsonLine() {
            var buffer = new List<byte>(256);
            while (true) {
                var value = output.ReadByte();
                if (value < 0) throw new EndOfStreamException("Frame server closed its output.");
                if (value == '\n') break;
                buffer.Add((byte)value);
            }
            return JsonNode.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        byte[] ReadExactly(int count) {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count) {
                var read = output.Read(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException("Frame server returned a short PCM window.");
                offset += read;
            }
            return buffer;
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            try { Close(); } catch { }
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
        }
    }
}
