using System;
using System.Collections.Generic;
using System.Threading;

namespace VocaloidBridge {
    /// <summary>
    /// Ring buffer between the VOCALOID frame server and a playback thread.
    ///
    /// A pump thread keeps the ring filled a few windows ahead; the audio thread
    /// only copies samples, so it never blocks on the engine. Underruns are
    /// counted instead of hidden: a silent gap is reported, not papered over.
    /// The frame reader is injected so this class can be tested without an engine.
    /// </summary>
    public sealed class VocaloidLiveSource : IDisposable {
        readonly Func<int, float[]> readFrames;
        readonly int frameSize;
        readonly float[] ring;
        readonly int ringFrames;
        readonly object gate = new object();
        Thread? pump;
        long writeFrame;
        long readFrame;
        long underrunFrames;
        long pumpedFrames;
        volatile bool running;
        volatile bool failed;

        public VocaloidLiveSource(Func<int, float[]> readFrames, int sampleRate = 44100, int frameSize = 256,
            int ringFrames = 64) {
            if (readFrames == null) throw new ArgumentNullException(nameof(readFrames));
            if (sampleRate != 44100) throw new NotSupportedException("The VOCALOID engine renders 44.1 kHz mono.");
            if (frameSize != 256) throw new NotSupportedException("The VOCALOID engine renders 256-sample frames.");
            if (ringFrames < 4 || ringFrames > 4096) throw new ArgumentOutOfRangeException(nameof(ringFrames));
            this.readFrames = readFrames;
            this.frameSize = frameSize;
            this.ringFrames = ringFrames;
            ring = new float[checked(ringFrames * frameSize)];
        }

        public int SampleRate => 44100;
        public int FrameSize => frameSize;
        public long PumpedFrames => Interlocked.Read(ref pumpedFrames);
        public long UnderrunFrames => Interlocked.Read(ref underrunFrames);
        public long ReadFrame => Interlocked.Read(ref readFrame);
        public bool Failed => failed;
        /// <summary>Frames buffered ahead of the playhead.</summary>
        public long LeadFrames => Interlocked.Read(ref writeFrame) - Interlocked.Read(ref readFrame);

        public void Start() {
            if (running) return;
            running = true;
            pump = new Thread(Pump) { IsBackground = true, Name = "vocaloid-frame-pump" };
            pump.Start();
        }

        void Pump() {
            int ahead = ringFrames - 4;
            var scratch = new List<float>();
            while (running) {
                if (LeadFrames >= ahead) { Thread.Sleep(2); continue; }
                float[] window;
                try { window = readFrames(1); } catch { failed = true; return; }
                if (window == null || window.Length == 0) { Thread.Sleep(2); continue; }
                if (window.Length % frameSize != 0) {
                    // Pad a short tail so ring frames stay aligned.
                    var padded = new float[(window.Length / frameSize + 1) * frameSize];
                    Array.Copy(window, padded, window.Length);
                    window = padded;
                }
                int frames = window.Length / frameSize;
                lock (gate) {
                    for (int f = 0; f < frames; f++) {
                        long slot = (writeFrame + f) % ringFrames;
                        Array.Copy(window, f * frameSize, ring, slot * frameSize, frameSize);
                    }
                    writeFrame += frames;
                }
                Interlocked.Add(ref pumpedFrames, frames);
            }
        }

        /// <summary>Copy the next samples for the audio thread; missing data returns silence.</summary>
        public int Read(float[] destination, int offset, int count) {
            int written = 0;
            lock (gate) {
                long available = writeFrame - readFrame;
                int max = (int)Math.Min(count, available * frameSize);
                while (written < max) {
                    long slot = (readFrame + written / frameSize) % ringFrames;
                    int inFrame = written % frameSize;
                    int chunk = Math.Min(frameSize - inFrame, max - written);
                    Array.Copy(ring, slot * frameSize + inFrame, destination, offset + written, chunk);
                    written += chunk;
                }
                readFrame += written / frameSize;
            }
            for (int i = written; i < count; i++) destination[offset + i] = 0f;
            if (written < count) Interlocked.Add(ref underrunFrames, (count - written + frameSize - 1) / frameSize);
            return written;
        }

        public void Stop() {
            running = false;
            pump?.Join(1000);
            pump = null;
        }

        public void Dispose() { Stop(); }
    }
}
