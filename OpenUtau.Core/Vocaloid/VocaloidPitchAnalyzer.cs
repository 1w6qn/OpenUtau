using System;
using System.Threading;
using OpenUtau.Core.Render;

namespace OpenUtau.Core.Vocaloid {
    /// <summary>F0-derived pitch from rendered audio, with unvoiced frames kept explicit.</summary>
    public sealed class VocaloidPitchContour {
        public double[] TimesSeconds { get; private set; }
        public float[] Tones { get; private set; }
        public bool[] Voiced { get; private set; }

        internal VocaloidPitchContour(double[] timesSeconds, float[] tones, bool[] voiced) {
            TimesSeconds = timesSeconds;
            Tones = tones;
            Voiced = voiced;
        }
    }

    /// <summary>Analyzes actual VOCALOID PCM using OpenUtau's bundled WORLDLINE DIO F0 extractor.</summary>
    public static class VocaloidPitchAnalyzer {
        const double FramePeriodMs = 10.0;
        const int MaxDurationSeconds = 320;
        const double MinF0Hz = 40.0;
        const double MaxF0Hz = 1500.0;

        /// <param name="startSeconds">Phrase-relative time of the first PCM sample.</param>
        public static VocaloidPitchContour Analyze(float[] samples, int sampleRate, double startSeconds, CancellationToken token) {
            if (samples == null) throw new ArgumentNullException("samples");
            if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentOutOfRangeException("sampleRate", "Pitch analysis supports sample rates from 8 kHz through 192 kHz.");
            if (samples.Length == 0) throw new ArgumentException("Pitch analysis requires non-empty PCM.", "samples");
            if (samples.Length > (long)sampleRate * MaxDurationSeconds) throw new ArgumentOutOfRangeException("samples", "Pitch analysis is limited to 320 seconds of PCM.");
            if (Double.IsNaN(startSeconds) || Double.IsInfinity(startSeconds)) throw new ArgumentOutOfRangeException("startSeconds", "The phrase-relative sample start must be finite.");

            token.ThrowIfCancellationRequested();
            for (int i = 0; i < samples.Length; ++i) {
                if (Single.IsNaN(samples[i]) || Single.IsInfinity(samples[i]))
                    throw new ArgumentException("Pitch analysis PCM contains a non-finite sample at index " + i + ".", "samples");
            }

            // Worldline.F0 uses F0FrameCount internally, sizes native output itself,
            // and returns null when the bundled native library cannot analyze audio.
            double[] f0 = Worldline.F0(samples, sampleRate, FramePeriodMs, method: 0);
            token.ThrowIfCancellationRequested();
            if (f0 == null || f0.Length == 0)
                throw new InvalidOperationException("OpenUtau WORLDLINE F0 analysis failed. Ensure worldline.dll is available beside the OpenUtau runtime.");
            int maxFrames = checked((int)Math.Ceiling((double)samples.Length / sampleRate * 1000.0 / FramePeriodMs) + 2);
            if (f0.Length > maxFrames)
                throw new InvalidOperationException("OpenUtau WORLDLINE returned an unreasonable F0 frame count: " + f0.Length + ".");

            var times = new double[f0.Length];
            var tones = new float[f0.Length];
            var voiced = new bool[f0.Length];
            for (int i = 0; i < f0.Length; ++i) {
                times[i] = startSeconds + i * (FramePeriodMs / 1000.0);
                if (Double.IsNaN(times[i]) || Double.IsInfinity(times[i]))
                    throw new InvalidOperationException("Pitch frame time overflowed the phrase-relative time range.");
                double hz = f0[i];
                bool isVoiced = !Double.IsNaN(hz) && !Double.IsInfinity(hz) && hz >= MinF0Hz && hz <= MaxF0Hz;
                voiced[i] = isVoiced;
                tones[i] = isVoiced ? (float)(69.0 + 12.0 * Math.Log(hz / 440.0, 2.0)) : -1f;
            }
            return new VocaloidPitchContour(times, tones, voiced);
        }
    }
}
