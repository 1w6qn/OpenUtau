using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace OpenUtau.Core.Vocaloid.Tests {
    public static class VocaloidPitchAnalyzerProbe {
        public static int Run(string v3Wav, string v5Wav) {
            int checks = 0;
            const int rate = 44100;

            var silence = new float[rate];
            var silenceContour = VocaloidPitchAnalyzer.Analyze(silence, rate, 0.375, CancellationToken.None);
            Check(silenceContour.Tones.Length == silenceContour.TimesSeconds.Length && silenceContour.Tones.Length == silenceContour.Voiced.Length, "silence frame arrays align", ref checks);
            Check(!silenceContour.Voiced.Any(x => x) && silenceContour.Tones.All(x => x == -1f), "silence stays explicitly unvoiced", ref checks);
            Check(Math.Abs(silenceContour.TimesSeconds[0] - 0.375) < 1e-12 && Math.Abs(silenceContour.TimesSeconds[1] - 0.385) < 1e-12, "frame times preserve phrase-relative sample start", ref checks);

            float[] sine = Tone(rate, 1.5, t => 220.0);
            var sineWatch = Stopwatch.StartNew();
            var sineContour = VocaloidPitchAnalyzer.Analyze(sine, rate, 0, CancellationToken.None);
            sineWatch.Stop();
            double sineMidi = Median(sineContour, 0.2, 1.2);
            Check(Math.Abs(sineMidi - 57.0) < 0.5, "220 Hz sine is analyzed near MIDI 57", ref checks);
            Check(VoicedRatio(sineContour, 0.2, 1.2) > 0.85, "sustained sine frames remain voiced", ref checks);

            float[] chirp = Tone(rate, 2.0, t => 110.0 + 110.0 * t);
            var chirpContour = VocaloidPitchAnalyzer.Analyze(chirp, rate, 0, CancellationToken.None);
            double chirpStart = Median(chirpContour, 0.25, 0.65);
            double chirpEnd = Median(chirpContour, 1.3, 1.7);
            Check(chirpEnd - chirpStart > 7.0 && chirpEnd - chirpStart < 14.0, "chirp F0 rises with the source instead of copying target notes", ref checks);

            CheckPreCanceled();
            checks++;

            var actual = new List<string>();
            if (!String.IsNullOrWhiteSpace(v3Wav) && File.Exists(v3Wav)) actual.Add(v3Wav);
            if (!String.IsNullOrWhiteSpace(v5Wav) && File.Exists(v5Wav)) actual.Add(v5Wav);
            if (actual.Count == 0) throw new FileNotFoundException("Pass at least one real OpenUtau V3/V5 float WAV.");
            foreach (string path in actual) {
                var wav = ReadFloatWav(path);
                var watch = Stopwatch.StartNew();
                var contour = VocaloidPitchAnalyzer.Analyze(wav.Samples, wav.SampleRate, 0, CancellationToken.None);
                watch.Stop();
                int voiced = contour.Voiced.Count(x => x);
                Check(contour.Tones.Length > 0 && voiced > 0, Path.GetFileName(path) + " contains analyzer-detected voiced frames", ref checks);
                Check(contour.Tones.All(x => x == -1f || (!Single.IsNaN(x) && !Single.IsInfinity(x))), Path.GetFileName(path) + " has finite tones and explicit unvoiced markers", ref checks);
                Console.WriteLine(String.Format("real_pcm={0} rate={1} samples={2} f0_frames={3} voiced={4} elapsed_ms={5}", Path.GetFileName(path), wav.SampleRate, wav.Samples.Length, contour.Tones.Length, voiced, watch.ElapsedMilliseconds));
                if (Path.GetFileName(path).Equals("openutau-v3-demo.wav", StringComparison.OrdinalIgnoreCase)) {
                    CheckNotes(contour, new[] { 1.3, 1.9, 2.5 }, new[] { 60.0, 62.0, 64.0 }, "V3", ref checks);
                } else if (Path.GetFileName(path).Equals("openutau-v5-demo.wav", StringComparison.OrdinalIgnoreCase)) {
                    CheckNotes(contour,
                        new[] { 1.3, 1.9, 2.6, 4.3, 4.9, 5.6, 7.3, 7.9, 8.6 },
                        new[] { 60.0, 62.0, 64.0, 60.5, 62.5, 64.5, 59.5, 61.5, 63.5 },
                        "V5", ref checks);
                }
            }

            Console.WriteLine("PASS: " + checks + " VocaloidPitchAnalyzer checks; synthetic sine_ms=" + sineWatch.ElapsedMilliseconds + ", chirp_frames=" + chirpContour.Tones.Length);
            return checks;
        }

        static void CheckPreCanceled() {
            using (var source = new CancellationTokenSource()) {
                source.Cancel();
                try {
                    VocaloidPitchAnalyzer.Analyze(new float[4410], 44100, 0, source.Token);
                    throw new Exception("Pre-canceled pitch analysis did not cancel.");
                } catch (OperationCanceledException) { }
            }
        }

        static float[] Tone(int sampleRate, double seconds, Func<double, double> frequencyAt) {
            int length = (int)(sampleRate * seconds);
            var samples = new float[length];
            double phase = 0;
            for (int i = 0; i < length; ++i) {
                double t = (double)i / sampleRate;
                double frequency = frequencyAt(t);
                samples[i] = (float)(0.2 * Math.Sin(phase));
                phase += 2 * Math.PI * frequency / sampleRate;
            }
            return samples;
        }

        static double Median(VocaloidPitchContour contour, double from, double to) {
            double[] values = contour.Tones.Where((x, i) => contour.Voiced[i] && contour.TimesSeconds[i] >= from && contour.TimesSeconds[i] <= to)
                .Select(x => (double)x).OrderBy(x => x).ToArray();
            if (values.Length == 0) throw new Exception("Expected voiced frames between " + from + " and " + to + " seconds.");
            return values[values.Length / 2];
        }

        static double VoicedRatio(VocaloidPitchContour contour, double from, double to) {
            int total = 0, voiced = 0;
            for (int i = 0; i < contour.TimesSeconds.Length; ++i) {
                if (contour.TimesSeconds[i] < from || contour.TimesSeconds[i] > to) continue;
                total++;
                if (contour.Voiced[i]) voiced++;
            }
            return total == 0 ? 0 : (double)voiced / total;
        }

        static void CheckNotes(VocaloidPitchContour contour, double[] centers, double[] expected, string label, ref int checks) {
            for (int i = 0; i < centers.Length; ++i) {
                double measured = Median(contour, centers[i] - 0.08, centers[i] + 0.08);
                Check(Math.Abs(measured - expected[i]) <= 0.5,
                    label + " real PCM pitch at " + centers[i] + "s (" + measured + " MIDI, expected " + expected[i] + ")", ref checks);
            }
        }

        sealed class FloatWav { public int SampleRate; public float[] Samples = Array.Empty<float>(); }

        static FloatWav ReadFloatWav(string path) {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream)) {
                if (new String(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException(path + " is not RIFF.");
                reader.ReadUInt32();
                if (new String(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException(path + " is not WAVE.");
                ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
                int sampleRate = 0;
                byte[] data = Array.Empty<byte>();
                while (stream.Position + 8 <= stream.Length) {
                    string id = new String(reader.ReadChars(4));
                    uint size = reader.ReadUInt32();
                    if (size > stream.Length - stream.Position) throw new InvalidDataException("Truncated WAV chunk in " + path + ".");
                    long end = stream.Position + size;
                    if (id == "fmt ") {
                        format = reader.ReadUInt16(); channels = reader.ReadUInt16(); sampleRate = reader.ReadInt32();
                        reader.ReadInt32(); blockAlign = reader.ReadUInt16(); bits = reader.ReadUInt16();
                    } else if (id == "data") data = reader.ReadBytes(checked((int)size));
                    stream.Position = end + (size & 1);
                }
                if (data == null || channels == 0 || sampleRate < 8000 || sampleRate > 192000 ||
                    !((format == 3 && bits == 32) || (format == 1 && bits == 16)) || blockAlign != channels * (bits / 8))
                    throw new InvalidDataException("Unsupported WAV format in " + path + ".");
                int bytesPerSample = bits / 8;
                int frames = data.Length / blockAlign;
                var mono = new float[frames];
                for (int frame = 0; frame < frames; ++frame) {
                    double sum = 0;
                    for (int channel = 0; channel < channels; ++channel) {
                        int offset = frame * blockAlign + channel * bytesPerSample;
                        sum += format == 3 ? BitConverter.ToSingle(data, offset) : BitConverter.ToInt16(data, offset) / 32768.0;
                    }
                    mono[frame] = (float)(sum / channels);
                }
                return new FloatWav { SampleRate = sampleRate, Samples = mono };
            }
        }

        static void Check(bool condition, string message, ref int checks) {
            if (!condition) throw new Exception("Pitch analyzer check failed: " + message);
            checks++;
        }
    }
}
