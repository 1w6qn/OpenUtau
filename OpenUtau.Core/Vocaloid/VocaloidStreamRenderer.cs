using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using VocaloidBridge;
using UstxFormat = OpenUtau.Core.Format.Ustx;

namespace OpenUtau.Core.Vocaloid {
    /// <summary>
    /// Phrase renderer that pulls PCM from the persistent VOCALOID frame server
    /// (tools/vocaloid-bridge/V4/run-stream-server.ps1) instead of waiting for a
    /// one-shot host render. The server advances the engine one 256-sample frame
    /// at a time, so a phrase is filled in windows and the same connection can be
    /// reused for later windows of the same phrase.
    ///
    /// Enabled per singer by adding <c>StreamHostScript</c> to vocaloid-bridge.json.
    /// The engine-side measurements (frame cost, seek identity, paced reads) are in
    /// tools/vocaloid-bridge/docs/实时渲染可行性与接入.md.
    /// </summary>
    public sealed class VocaloidStreamRenderer : IRenderer {
        public const string Id = "VOCALOID-STREAM";
        // V4ScoreWriter places the score at this offset; the first engine frame
        // therefore starts 2 seconds before the phrase origin.
        const double V4OriginSeconds = 2.0;
        const int ChunkFrames = 32;

        public USingerType SingerType => VocaloidSinger.Type;
        public bool SupportsRenderPitch => false;
        public bool SupportsPhonemeEnvelope => false;
        public bool SupportsExpression(UExpressionDescriptor descriptor) =>
            descriptor != null && (descriptor.abbr == UstxFormat.PITD || descriptor.abbr == UstxFormat.DYN);

        public (double HeadMs, double TailMs) PhrasePadding(USinger singer, IEnumerable<UPhoneme> phonemes) {
            if (singer is not VocaloidSinger vocaloid) return (0d, 0d);
            BridgeConfig config = Config(vocaloid);
            return (config.HeadMs, config.TailMs);
        }

        // The stream renderer does not analyze rendered F0; the requested pitch curve is
        // already the engine input, so callers fall back to the project curve.
        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            return null;
        }

        public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) =>
            Array.Empty<UExpressionDescriptor>();

        public override string ToString() => Id;

        public RenderResult Layout(RenderPhrase phrase) {
            var padding = phrase.singer is VocaloidSinger singer
                ? PhrasePadding(singer, Array.Empty<UPhoneme>())
                : (HeadMs: 0d, TailMs: 0d);
            return new RenderResult {
                positionMs = phrase.positionMs,
                leadingMs = padding.HeadMs,
                estimatedLengthMs = Math.Max(0, padding.HeadMs + phrase.durationMs + padding.TailMs),
            };
        }

        static BridgeConfig Config(VocaloidSinger singer) =>
            BridgeConfig.Load(Path.Combine(singer.Location, "vocaloid-bridge.json"));

        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
            CancellationTokenSource cancellation, bool isPreRender = false, RenderPhraseEvents? renderEvents = null) =>
            Task.Run(() => RenderCore(phrase, progress, trackNo, cancellation.Token), cancellation.Token);

        RenderResult RenderCore(RenderPhrase phrase, Progress? progress, int trackNo, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if (phrase.singer is not VocaloidSinger singer)
                throw new InvalidOperationException("Vocaloid stream renderer requires a Vocaloid singer.");
            BridgeConfig config = Config(singer);
            if (string.IsNullOrWhiteSpace(config.StreamHostScript))
                throw new InvalidOperationException(
                    "This singer has no StreamHostScript configured; add tools/vocaloid-bridge/V4/run-stream-server.ps1 to vocaloid-bridge.json.");
            if (!string.Equals(config.EngineType, "VOCALOID4", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("The frame server drives the VOCALOID 4 engine; VOCALOID5 voicebanks use the offline renderer.");

            string status = $"+" + "Track " + (trackNo + 1) + ": " + Id + " \"" + string.Join(" ", phrase.notes.Select(n => n.lyric)) + "\"";
            progress?.Complete(0, status);

            var request = BuildRequest(phrase, config);
            var result = Layout(phrase);
            int destinationLength = Math.Max(1, (int)Math.Ceiling(result.estimatedLengthMs * 44.1));
            double targetStartSeconds = (phrase.Layout.StartMs - phrase.positionMs) / 1000d;
            int destinationOffset = (int)Math.Round((-V4OriginSeconds - targetStartSeconds) * 44100);
            int destinationStart = Math.Max(0, destinationOffset);
            int sourceStart = Math.Max(0, -destinationOffset);
            int needed = destinationLength - destinationStart + sourceStart;
            if (needed <= 0) return result;

            string scratch = Path.Combine(Path.GetTempPath(), "vocaloid-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try {
                string configPath = Path.Combine(scratch, "config.json");
                string requestPath = Path.Combine(scratch, "request.json");
                File.WriteAllText(configPath, JsonSerializer.Serialize(config));
                File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
                using var source = new VocaloidFrameSource(config.HostExecutable, config.StreamHostScript,
                    configPath, requestPath);
                float[] buffer = new float[checked(needed + 8)];
                int filled = 0;
                while (filled < needed) {
                    token.ThrowIfCancellationRequested();
                    float[] window = source.Read(ChunkFrames, pace: false);
                    if (window.Length == 0) break;
                    int copy = Math.Min(window.Length, buffer.Length - filled);
                    Array.Copy(window, 0, buffer, filled, copy);
                    filled += copy;
                }
                result.samples = new float[destinationLength];
                result.positionMs = phrase.Layout.StartMs + phrase.Layout.LeadingMs;
                result.leadingMs = phrase.Layout.LeadingMs;
                int copyCount = Math.Min(filled - sourceStart, destinationLength - destinationStart);
                if (copyCount > 0) Array.Copy(buffer, sourceStart, result.samples, destinationStart, copyCount);
            } finally {
                try { Directory.Delete(scratch, true); } catch { }
            }
            if (result.samples.Length > 0) Renderers.ApplyDynamics(phrase, result);
            progress?.Complete(phrase.phones.Length, status);
            return result;
        }

        BridgeRequest BuildRequest(RenderPhrase phrase, BridgeConfig config) {
            var notes = phrase.notes.Select(n => new BridgeNote {
                StartSeconds = (n.positionMs - phrase.positionMs) / 1000d,
                EndSeconds = (n.endMs - phrase.positionMs) / 1000d,
                Tone = n.tone,
                Lyric = n.lyric,
            }).ToArray();
            notes = MergeContinuations(notes);
            if (string.Equals(config.LyricLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(config.LyricLanguage, "zh", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrEmpty(config.LyricLanguage) && config.EngineType == "VOCALOID4" &&
                    config.VoiceId.StartsWith("4:", StringComparison.Ordinal))) {
                var lyrics = BaseChinesePhonemizer.Romanize(notes.Select(n => n.Lyric));
                for (int i = 0; i < notes.Length; i++) notes[i].Lyric = lyrics[i];
            }
            var request = new BridgeRequest {
                VoiceId = config.VoiceId,
                Notes = notes,
                Pitch = VocaloidRenderer.CreatePitchCurve(phrase),
                NoteProperties = notes.Select(_ => new Dictionary<string, JsonElement>()).ToArray(),
            };
            request.Validate();
            return request;
        }

        static BridgeNote[] MergeContinuations(BridgeNote[] notes) {
            string? prior = null;
            var merged = new List<BridgeNote>(notes.Length);
            foreach (var note in notes) {
                string lyric = note.Lyric?.Trim() ?? string.Empty;
                if (lyric == "+") {
                    if (merged.Count == 0 || Math.Abs(merged[^1].EndSeconds - note.StartSeconds) > 0.000001)
                        throw new InvalidOperationException("'+' can only extend the immediately preceding contiguous note.");
                    merged[^1].EndSeconds = note.EndSeconds;
                    continue;
                }
                if (lyric.StartsWith("+", StringComparison.Ordinal) || lyric == "~")
                    throw new InvalidOperationException("The continuation lyric '" + lyric + "' has no supported backend mapping.");
                if (string.IsNullOrEmpty(lyric)) throw new InvalidOperationException("A sung note needs a lyric.");
                prior = lyric;
                merged.Add(note);
            }
            return merged.ToArray();
        }
    }
}
