using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenUtau.Core;
using OpenUtau.Core.Format;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using VocaloidBridge;
using UstxFormat = OpenUtau.Core.Format.Ustx;
using Serilog;

namespace OpenUtau.Core.Vocaloid {
    /// <summary>Phrase renderer backed by the external Vocaloid compatibility host.</summary>
    public sealed class VocaloidRenderer : IRenderer {
        public const string Id = "VOCALOID-BRIDGE";
        static readonly HashSet<string> supported = new HashSet<string> { UstxFormat.PITD, UstxFormat.DYN };
        readonly object pitchLock = new object();
        readonly Dictionary<ulong, LinkedListNode<PitchEntry>> pitchCache = new();
        readonly LinkedList<PitchEntry> pitchLru = new();
        long pitchBytes;
        const long MaxPitchBytes = 8 * 1024 * 1024;
        sealed class PitchEntry {
            public ulong PhraseHash;
            public string Key = "";
            public BridgeSession Session = null!;
            public RenderPitchResult Result = null!;
            public long Bytes => Result.tones.LongLength * 9;
        }
        public USingerType SingerType => VocaloidSinger.Type;
        public bool SupportsRenderPitch => true;
        public bool SupportsPhonemeEnvelope => false;
        public bool SupportsExpression(UExpressionDescriptor descriptor) => descriptor != null && supported.Contains(descriptor.abbr);

        public (double HeadMs, double TailMs) PhrasePadding(USinger singer, IEnumerable<UPhoneme> phonemes) {
            if (singer is not VocaloidSinger vocaloid) return (HeadMs: 0d, TailMs: 0d);
            BridgeConfig config = Config(vocaloid);
            return (HeadMs: config.HeadMs, TailMs: config.TailMs);
        }

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

        static BridgeConfig Config(VocaloidSinger singer) => BridgeConfig.Load(System.IO.Path.Combine(singer.Location, "vocaloid-bridge.json"));

        public Task<RenderResult> Render(RenderPhrase phrase, Progress progress, int trackNo,
            CancellationTokenSource cancellation, bool isPreRender = false, RenderPhraseEvents? renderEvents = null) =>
            RenderCoreAsync(phrase, progress, trackNo, cancellation.Token);

        async Task<RenderResult> RenderCoreAsync(RenderPhrase phrase, Progress? progress, int trackNo, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if (phrase.singer is not VocaloidSinger singer) throw new InvalidOperationException("Vocaloid renderer requires a Vocaloid singer.");
            var notes = phrase.notes.Select(n => new BridgeNote {
                StartSeconds = (n.positionMs - phrase.positionMs) / 1000d,
                EndSeconds = (n.endMs - phrase.positionMs) / 1000d,
                Tone = n.tone,
                Lyric = n.lyric,
            }).ToArray();
            notes = NormalizeContinuationLyrics(notes);
            var config = Config(singer);
            if (string.Equals(config.LyricLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(config.LyricLanguage, "zh", StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrEmpty(config.LyricLanguage) && config.EngineType == "VOCALOID4" &&
                    config.VoiceId.StartsWith("4:", StringComparison.Ordinal))) {
                var lyrics = BaseChinesePhonemizer.Romanize(notes.Select(n => n.Lyric));
                for (int i = 0; i < notes.Length; i++) notes[i].Lyric = lyrics[i];
            }

            // OpenUtau's pitch grid begins at phrase.position - leading and advances in 5-tick steps.
            // Convert every point through TimeAxis so tempo changes are preserved in seconds.
            var pitch = CreatePitchCurve(phrase);
            var request = new BridgeRequest {
                VoiceId = config.VoiceId,
                Notes = notes,
                Pitch = pitch,
                NoteProperties = notes.Select(_ => new Dictionary<string, JsonElement>()).ToArray(),
            };

            string status = $"Track {trackNo + 1}: {Id} \"{string.Join(" ", phrase.notes.Select(n => n.lyric))}\"";
            progress?.Complete(0, status);
            var session = BridgeSession.Get(System.IO.Path.Combine(singer.Location, "vocaloid-bridge.json"));
            string pitchKey = request.CacheKey(session.Fingerprint);
            var audio = await session.RenderAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var result = Layout(phrase);
            long resampledCapacity = Math.Min(44100L * 320, (long)audio.Samples.LongLength * 44100L / audio.SampleRate + 8L);
            var samples = new float[checked((int)resampledCapacity)];
            int count;
            using (var stream = new System.IO.MemoryStream(ToPcmBytes(audio.Samples), writable: false))
            using (var raw = new RawSourceWaveStream(stream, WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, 1))) {
                ISampleProvider mono = raw.ToSampleProvider();
                ISampleProvider converted = audio.SampleRate == 44100 ? mono : new WdlResamplingSampleProvider(mono, 44100);
                count = converted.Read(samples, 0, samples.Length);
            }
            double targetStartSeconds = (phrase.Layout.StartMs - phrase.positionMs) / 1000d;
            int destinationLength = Math.Max(1, (int)Math.Ceiling(result.estimatedLengthMs * 44.1));
            result.samples = new float[destinationLength];
            result.positionMs = phrase.Layout.StartMs + phrase.Layout.LeadingMs;
            result.leadingMs = phrase.Layout.LeadingMs;
            int destinationOffset = (int)Math.Round((audio.StartSeconds - targetStartSeconds) * 44100);
            int sourceStart = Math.Max(0, -destinationOffset);
            int destinationStart = Math.Max(0, destinationOffset);
            int copyCount = Math.Min(count - sourceStart, destinationLength - destinationStart);
            if (copyCount > 0) Array.Copy(samples, sourceStart, result.samples, destinationStart, copyCount);
            // Analyze before DYN: a muted track must still have its real synthesized pitch available.
            if (!HasPitch(phrase.hash, pitchKey)) {
                try {
                    var contour = VocaloidPitchAnalyzer.Analyze(result.samples, 44100, targetStartSeconds, token);
                    var renderedPitch = new RenderPitchResult {
                        ticks = contour.TimesSeconds.Select(t => (float)(phrase.timeAxis.MsPosToTickPos(
                            phrase.positionMs + t * 1000) - phrase.position)).ToArray(),
                        tones = contour.Tones,
                        voiced = contour.Voiced,
                    };
                    token.ThrowIfCancellationRequested();
                    StorePitch(phrase.hash, pitchKey, session, renderedPitch);
                } catch (OperationCanceledException) { throw; }
                catch (Exception e) { Log.Warning(e, "VOCALOID synthesized audio is ready, but pitch analysis failed."); }
            }
            if (result.samples.Length > 0) Renderers.ApplyDynamics(phrase, result);
            progress?.Complete(phrase.phones.Length, status);
            return result;
        }

        /// <summary>Converts OpenUtau's absolute-cent 5-tick pitch grid to phrase-relative seconds and semitones.</summary>
        public static BridgeCurve CreatePitchCurve(RenderPhrase phrase) {
            int pitchStartTick = phrase.position - phrase.leading;
            const int pitchStep = 5;
            return new BridgeCurve {
                TimesSeconds = Enumerable.Range(0, phrase.pitches.Length)
                    .Select(i => (phrase.timeAxis.TickPosToMsPos(pitchStartTick + i * pitchStep) - phrase.positionMs) / 1000d).ToArray(),
                Values = phrase.pitches.Select(value => (double)value / 100d).ToArray(),
            };
        }

        static byte[] ToPcmBytes(float[] samples) {
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        static BridgeNote[] NormalizeContinuationLyrics(BridgeNote[] notes) {
            string? prior = null;
            var normalized = new List<BridgeNote>(notes.Length);
            for (int i = 0; i < notes.Length; i++) {
                string lyric = notes[i].Lyric?.Trim() ?? string.Empty;
                if (lyric == "~" || (lyric.StartsWith("+", StringComparison.Ordinal) && lyric != "+"))
                    throw new InvalidOperationException($"The continuation lyric '{lyric}' has no supported backend mapping.");
                if (lyric == "+") {
                    if (string.IsNullOrEmpty(prior)) throw new InvalidOperationException($"Continuation lyric '{lyric}' has no preceding sung note.");
                    if (normalized.Count == 0 || Math.Abs(normalized[^1].EndSeconds - notes[i].StartSeconds) > 0.000001)
                        throw new InvalidOperationException("'+' can only extend the immediately preceding contiguous sung note.");
                    normalized[^1].EndSeconds = notes[i].EndSeconds;
                    continue;
                }
                if (lyric.Length == 0 || lyric.Any(char.IsControl))
                    throw new InvalidOperationException("Vocaloid lyrics cannot be empty or contain control characters.");
                if (lyric == "R" || lyric == "-" || lyric.Equals("br", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The special lyric '{lyric}' is not supported by this backend and cannot be mapped to a Vocaloid note.");
                notes[i].Lyric = lyric;
                prior = lyric;
                normalized.Add(notes[i]);
            }
            return normalized.ToArray();
        }

        public UExpressionDescriptor[] GetSuggestedExpressions(USinger singer, URenderSettings renderSettings) => Array.Empty<UExpressionDescriptor>();
        bool HasPitch(ulong hash, string key) {
            lock (pitchLock) {
                return pitchCache.TryGetValue(hash, out var node) && node.Value.Key == key && !node.Value.Session.IsDisposed;
            }
        }

        void StorePitch(ulong hash, string key, BridgeSession session, RenderPitchResult result) {
            lock (pitchLock) {
                if (pitchCache.Remove(hash, out var old)) { pitchBytes -= old.Value.Bytes; pitchLru.Remove(old); }
                var entry = new PitchEntry { PhraseHash = hash, Key = key, Session = session, Result = result };
                var node = pitchLru.AddFirst(entry);
                pitchCache[hash] = node;
                pitchBytes += entry.Bytes;
                while (pitchBytes > MaxPitchBytes || pitchCache.Count > 128) {
                    var last = pitchLru.Last!;
                    pitchCache.Remove(last.Value.PhraseHash);
                    pitchBytes -= last.Value.Bytes;
                    pitchLru.RemoveLast();
                }
            }
        }

        /// <summary>Read-only snapshot for drawing; never touches disk or starts synthesis. Do not mutate its arrays.</summary>
        public bool TryGetRenderedPitchCached(RenderPhrase phrase, out RenderPitchResult result) {
            lock (pitchLock) {
                if (pitchCache.TryGetValue(phrase.hash, out var node) && !node.Value.Session.IsDisposed) {
                    pitchLru.Remove(node);
                    pitchLru.AddFirst(node);
                    result = node.Value.Result;
                    return true;
                }
            }
            result = null!;
            return false;
        }

        public RenderPitchResult LoadRenderedPitch(RenderPhrase phrase) {
            // A manual Ctrl+R before the first preview also works: render once to obtain actual PCM/F0.
            RenderCoreAsync(phrase, null, 0, CancellationToken.None).GetAwaiter().GetResult();
            if (!TryGetRenderedPitchCached(phrase, out var result))
                throw new InvalidOperationException("VOCALOID pitch analysis is unavailable. Check the OpenUtau log and worldline library.");
            return new RenderPitchResult { ticks = (float[])result.ticks.Clone(), tones = (float[])result.tones.Clone(),
                voiced = result.voiced == null ? null : (bool[])result.voiced.Clone() };
        }
        public override string ToString() => Id;
    }
}
