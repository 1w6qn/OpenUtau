using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VocaloidBridge {
    public sealed class BridgeVoice {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Language { get; set; } = "";
        // Zero means the engine did not report the voicebank generation.
        public int Generation { get; set; }
        public string ComponentId { get; set; } = "";
        public string DatabasePath { get; set; } = "";
        public int LicenseCount { get; set; } = -1;
        public bool Ready { get; set; } = true;
    }

    public sealed class BridgeNote {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public int Tone { get; set; }
        public string Lyric { get; set; } = "";
    }

    public sealed class BridgeCurve {
        public double[] TimesSeconds { get; set; } = Array.Empty<double>();
        public double[] Values { get; set; } = Array.Empty<double>();

        public void Validate() {
            if (Values.Length == 0 || Values.Length != TimesSeconds.Length || Values.Length > 200000)
                throw new InvalidDataException("A curve must contain 1..200000 matching time/value pairs.");
            for (int i = 0; i < Values.Length; i++) {
                if (!double.IsFinite(Values[i]) || !double.IsFinite(TimesSeconds[i]) ||
                    (i > 0 && TimesSeconds[i] <= TimesSeconds[i - 1]))
                    throw new InvalidDataException("Curve points must be finite and times strictly increasing.");
            }
        }

        public double Sample(double time) {
            if (Values.Length == 0) throw new InvalidDataException("Empty curve.");
            int i = Array.BinarySearch(TimesSeconds, time);
            if (i >= 0) return Values[i];
            i = ~i;
            if (i == 0) return Values[0];
            if (i >= Values.Length) return Values[Values.Length - 1];
            double t = (time - TimesSeconds[i - 1]) / (TimesSeconds[i] - TimesSeconds[i - 1]);
            return Values[i - 1] + t * (Values[i] - Values[i - 1]);
        }
    }

    public sealed class BridgeRequest {
        public string VoiceId { get; set; } = "";
        public BridgeNote[] Notes { get; set; } = Array.Empty<BridgeNote>();
        public BridgeCurve Pitch { get; set; } = new BridgeCurve();
        public Dictionary<string, BridgeCurve> Automations { get; set; } = new Dictionary<string, BridgeCurve>();
        public Dictionary<string, JsonElement> PartProperties { get; set; } = new Dictionary<string, JsonElement>();
        public Dictionary<string, JsonElement>[] NoteProperties { get; set; } = Array.Empty<Dictionary<string, JsonElement>>();

        public void Validate() {
            if (string.IsNullOrWhiteSpace(VoiceId) || VoiceId.Length > 1024)
                throw new InvalidDataException("An exact installed voice ID is required.");
            if (Notes == null || Notes.Length == 0 || Notes.Length > 10000)
                throw new InvalidDataException("A phrase must contain 1..10000 notes.");
            for (int i = 0; i < Notes.Length; i++) {
                var n = Notes[i];
                if (n == null || !double.IsFinite(n.StartSeconds) || !double.IsFinite(n.EndSeconds) ||
                    n.EndSeconds <= n.StartSeconds || n.StartSeconds < -10 || n.EndSeconds > 300 ||
                    n.Tone < 0 || n.Tone > 127 || string.IsNullOrWhiteSpace(n.Lyric) || n.Lyric.Length > 1024 ||
                    (i > 0 && n.StartSeconds < Notes[i - 1].EndSeconds - 0.000001))
                    throw new InvalidDataException("Notes must be finite, ordered, monophonic and within a 300-second phrase.");
            }
            if (Pitch == null) throw new InvalidDataException("An absolute MIDI pitch curve is required.");
            Pitch.Validate();
            if (Automations == null || PartProperties == null || NoteProperties == null ||
                Automations.Count > 128 || PartProperties.Count > 128 ||
                (NoteProperties.Length != 0 && NoteProperties.Length != Notes.Length))
                throw new InvalidDataException("Invalid automation or property collection.");
            foreach (var c in Automations.Values) c.Validate();
        }

        public string CacheKey(string fingerprint) {
            // Sorted property keys make otherwise identical requests share a cache entry.
            var canonical = new {
                VoiceId, Notes, Pitch,
                Automations = Automations.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
                PartProperties = PartProperties.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
                NoteProperties = NoteProperties.Select(p => p.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()).ToArray(),
            };
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                fingerprint + "\n" + JsonSerializer.Serialize(canonical))));
        }
    }

    public sealed class BridgeAudio {
        public const int MaxSampleCount = 16 * 1024 * 1024;
        public double StartSeconds { get; set; }
        public int SampleRate { get; set; }
        public float[] Samples { get; set; } = Array.Empty<float>();
        public void Validate() {
            if (!double.IsFinite(StartSeconds) || Math.Abs(StartSeconds) > 600 ||
                SampleRate < 8000 || SampleRate > 192000 || Samples == null || Samples.Length == 0 ||
                Samples.Length > MaxSampleCount || Samples.Length > SampleRate * 320L || Samples.Any(s => !float.IsFinite(s)))
                throw new InvalidDataException("The synthesis engine returned invalid or excessive PCM audio.");
        }
        public BridgeAudio Clone() => new BridgeAudio {
            StartSeconds = StartSeconds, SampleRate = SampleRate, Samples = (float[])Samples.Clone(),
        };
    }

    public sealed class BridgeConfig {
        public string HostExecutable { get; set; } = "pwsh";
        public string HostScript { get; set; } = "run-host.ps1";
        public string EngineAssembly { get; set; } = "";
        public string EngineType { get; set; } = "";
        public string EngineDirectory { get; set; } = "";
        public string VoiceId { get; set; } = "";
        public string LyricLanguage { get; set; } = "";
        public string Revision { get; set; } = "1";
        public double TimeoutSeconds { get; set; } = 60;
        public double HeadMs { get; set; } = 500;
        public double TailMs { get; set; } = 1000;
        public long CacheBytes { get; set; } = 64 * 1024 * 1024;

        public static BridgeConfig Load(string path) {
            path = Path.GetFullPath(path);
            var c = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Empty bridge configuration.");
            string root = Path.GetDirectoryName(path)!;
            c.HostScript = Path.GetFullPath(c.HostScript, root);
            if (string.IsNullOrWhiteSpace(c.EngineAssembly))
                throw new InvalidDataException("EngineAssembly must point to the configured local engine or bridge DLL.");
            c.EngineAssembly = Path.GetFullPath(c.EngineAssembly, root);
            c.EngineDirectory = string.IsNullOrWhiteSpace(c.EngineDirectory)
                ? Path.GetDirectoryName(c.EngineAssembly)! : Path.GetFullPath(c.EngineDirectory, root);
            if (!double.IsFinite(c.TimeoutSeconds) || c.TimeoutSeconds < 1 || c.TimeoutSeconds > 600 ||
                !double.IsFinite(c.HeadMs) || c.HeadMs < 0 || c.HeadMs > 10000 ||
                !double.IsFinite(c.TailMs) || c.TailMs < 0 || c.TailMs > 10000 ||
                c.CacheBytes < 0 || c.CacheBytes > 512 * 1024 * 1024L || string.IsNullOrWhiteSpace(c.Revision))
                throw new InvalidDataException("Invalid timeout, padding, revision or cache limit.");
            return c;
        }
    }

    public sealed class HostCall {
        public int Protocol { get; set; } = 1;
        public long Id { get; set; }
        public string Operation { get; set; } = "";
        public BridgeRequest? Request { get; set; }
        public string? ImportDirectory { get; set; }
    }

    public sealed class HostReply {
        public int Protocol { get; set; } = 1;
        public long Id { get; set; }
        public string? Error { get; set; }
        public BridgeVoice[]? Voices { get; set; }
        public double StartSeconds { get; set; }
        public int SampleRate { get; set; }
        // PCM uses binary framing after this small JSON header, never a JSON float array.
        public int SampleCount { get; set; }
    }
}
