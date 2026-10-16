using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using OpenUtau.App.ViewModels;
using OpenUtau.Core.Render;
using OpenUtau.Core.Vocaloid;

namespace OpenUtau.App.Controls {
    /// <summary>
    /// Draws cached Vocaloid audio-derived pitch on top of the score pitch.
    /// This helper is deliberately read-only and never starts a render.
    /// </summary>
    public static class VocaloidPitchDrawing {
        /// <summary>
        /// Draws voiced cached F0 points in the piano-roll pitch coordinates.
        /// A cache miss is a no-op; rendering populates the cache and the normal
        /// waveform refresh repaints the piano roll afterward.
        /// </summary>
        public static bool Draw(
            DrawingContext context,
            NotesViewModel viewModel,
            RenderPhrase phrase,
            int partPosition,
            IPen pen,
            double leftTick = double.NegativeInfinity,
            double rightTick = double.PositiveInfinity) {
            var part = viewModel.Part;
            if (part == null || part.trackNo < 0 || part.trackNo >= viewModel.Project.tracks.Count ||
                viewModel.Project.tracks[part.trackNo].RendererSettings.Renderer is not VocaloidRenderer renderer ||
                !renderer.TryGetRenderedPitchCached(phrase, out RenderPitchResult result) ||
                result?.ticks == null || result.tones == null) {
                return false;
            }

            int count = System.Math.Min(result.ticks.Length, result.tones.Length);
            if (result.voiced != null) {
                count = System.Math.Min(count, result.voiced.Length);
            }
            int first = 0, end = count;
            double tickOffset = phrase.position - partPosition;
            if (count > 0 && double.IsFinite(leftTick)) {
                int index = System.Array.BinarySearch(result.ticks, 0, count, (float)(leftTick - tickOffset));
                first = System.Math.Max(0, (index < 0 ? ~index : index) - 1);
            }
            if (count > 0 && double.IsFinite(rightTick)) {
                int index = System.Array.BinarySearch(result.ticks, 0, count, (float)(rightTick - tickOffset));
                end = System.Math.Min(count, (index < 0 ? ~index : index) + 2);
            }

            var segment = new Points();
            void FlushSegment() {
                if (segment.Count > 1) {
                    context.DrawGeometry(null, pen, new PolylineGeometry(segment, false));
                }
                segment = new Points();
            }

            for (int i = first; i < end; i++) {
                float tone = result.tones[i];
                bool voiced = result.voiced == null || result.voiced[i];
                if (!voiced || tone < 0 || float.IsNaN(tone) || float.IsInfinity(tone)) {
                    FlushSegment();
                    continue;
                }

                double tick = tickOffset + result.ticks[i];
                segment.Add(viewModel.TickToneToPoint(tick, tone - 0.5));
            }
            FlushSegment();
            return true;
        }
    }
}
