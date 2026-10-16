using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenUtau.Core;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Vocaloid;
using VocaloidBridge;

public static class VocaloidPitchPipelineProbe {
    public static int Run(string generation, string output) {
        var project = DocManager.Inst.Project;
        var rows = new List<object>();
        foreach (var part in project.parts.OfType<UVoicePart>()) {
            // This is the actual piano-roll renderer lookup, not a fresh instance.
            var renderer = project.tracks[part.trackNo].RendererSettings.Renderer as VocaloidRenderer;
            if (renderer == null || !renderer.SupportsRenderPitch) throw new Exception("VOCALOID render-pitch support is disabled.");
            foreach (var phrase in part.renderPhrases) {
                if (!renderer.TryGetRenderedPitchCached(phrase, out var pitch))
                    throw new Exception("Automatic F0 was not published to the piano-roll renderer cache.");
                if (pitch.ticks.Length != pitch.tones.Length || pitch.voiced == null || pitch.voiced.Length != pitch.tones.Length)
                    throw new Exception("Pitch arrays do not align.");
                if (pitch.voiced[0] || pitch.tones[0] != -1) throw new Exception("Silent preroll must remain unvoiced.");
                var checks = new List<object>();
                foreach (var note in phrase.notes) {
                    double center = (note.positionMs + note.endMs) / 2;
                    double offset = generation == "V5" ? new[] { 0.0, 0.5, -0.5 }[part.trackNo] : 0;
                    double expected = note.tone + offset;
                    var measured = Enumerable.Range(0, pitch.tones.Length)
                        .Where(i => pitch.voiced[i] && Math.Abs(phrase.timeAxis.TickPosToMsPos(phrase.position + pitch.ticks[i]) - center) < 60)
                        .Select(i => pitch.tones[i]).OrderBy(x => x).ToArray();
                    if (measured.Length < 3) throw new Exception("Not enough voiced frames at the sustained note center.");
                    float median = measured[measured.Length / 2];
                    if (Math.Abs(median - expected) > 0.5) throw new Exception($"Wrong rendered pitch: expected {expected}, got {median}.");
                    checks.Add(new { expected_midi = expected, measured_midi = median, absolute_ms = center });
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var manual = renderer.LoadRenderedPitch(phrase);
                watch.Stop();
                manual.tones[0] = 999;
                if (!renderer.TryGetRenderedPitchCached(phrase, out var again) || !ReferenceEquals(pitch, again) || again.tones[0] != -1)
                    throw new Exception("Manual pitch loading reanalyzed the cached phrase or exposed mutable cached arrays.");
                rows.Add(new { track = part.trackNo, frames = pitch.tones.Length, voiced_frames = pitch.voiced.Count(v => v),
                    cached_manual_load_ms = watch.Elapsed.TotalMilliseconds, notes = checks,
                    ticks = pitch.ticks, tones = pitch.tones, voiced = pitch.voiced });
                Console.WriteLine($"PASS: {generation} track {part.trackNo}, {pitch.tones.Length} frames, {pitch.voiced.Count(v => v)} voiced; note pitch/time, cached Ctrl+R and clone isolation verified.");
                BridgeSession.Get(Path.Combine(phrase.singer.Location, "vocaloid-bridge.json")).Dispose();
                if (renderer.TryGetRenderedPitchCached(phrase, out _)) throw new Exception("Disposed/replaced synthesis sessions must invalidate F0 display.");
            }
        }
        int expectedPhrases = generation == "V3" ? 1 : generation == "V4" ? 2 : 3;
        if (rows.Count != expectedPhrases) throw new Exception($"Expected {expectedPhrases} automatically analyzed phrases, got {rows.Count}.");
        File.WriteAllText(output, JsonSerializer.Serialize(new { status = "real-vocaloid-auto-pitch-verified", generation, phrases = rows }, new JsonSerializerOptions { WriteIndented = true }));
        return rows.Count;
    }
}
