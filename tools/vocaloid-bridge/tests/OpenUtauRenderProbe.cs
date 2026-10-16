using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core;
using OpenUtau.Core.Render;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Vocaloid;
using VocaloidBridge;

public static class OpenUtauAssemblyResolver {
    static string root = string.Empty;
    public static void Attach(string assemblyDirectory) {
        root = assemblyDirectory;
        AssemblyLoadContext.Default.Resolving += Resolve;
    }
    static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) {
        string path = Path.Combine(root, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}

public static class OpenUtauRenderProbe {
    static void Check(bool value, string message) {
        if (!value) throw new InvalidOperationException("FAILED: " + message);
        Console.WriteLine("PASS: " + message);
    }

    static void Set(object target, Type type, string field, object? value) {
        var info = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.FullName, field);
        info.SetValue(target, value);
    }

    static RenderPhrase MakePhrase(USinger singer, TimeAxis axis) {
        const int phraseTick = 460;
        const int endTick = 500;
        double phraseMs = axis.TickPosToMsPos(phraseTick);
        double endMs = axis.TickPosToMsPos(endTick);
        var noteType = typeof(RenderNote);
        var note = (RenderNote)RuntimeHelpers.GetUninitializedObject(noteType);
        Set(note, noteType, "lyric", "a");
        Set(note, noteType, "tone", 60);
        Set(note, noteType, "position", phraseTick);
        Set(note, noteType, "duration", endTick - phraseTick);
        Set(note, noteType, "end", endTick);
        Set(note, noteType, "positionMs", phraseMs);
        Set(note, noteType, "durationMs", endMs - phraseMs);
        Set(note, noteType, "endMs", endMs);

        var phraseType = typeof(RenderPhrase);
        var phrase = (RenderPhrase)RuntimeHelpers.GetUninitializedObject(phraseType);
        Set(phrase, phraseType, "singer", singer);
        Set(phrase, phraseType, "timeAxis", axis);
        Set(phrase, phraseType, "position", phraseTick);
        Set(phrase, phraseType, "duration", endTick - phraseTick);
        Set(phrase, phraseType, "end", endTick);
        Set(phrase, phraseType, "leading", 0);
        Set(phrase, phraseType, "positionMs", phraseMs);
        Set(phrase, phraseType, "durationMs", endMs - phraseMs);
        Set(phrase, phraseType, "endMs", endMs);
        Set(phrase, phraseType, "leadingMs", 0d);
        Set(phrase, phraseType, "notes", new[] { note });
        Set(phrase, phraseType, "<phones>k__BackingField", Array.Empty<RenderPhone>());
        Set(phrase, phraseType, "pitches", Enumerable.Range(0, 9).Select(i => 6000f + 100 * i).ToArray());
        Set(phrase, phraseType, "dynamics", Enumerable.Repeat(0.5f, 9).ToArray());
        Set(phrase, phraseType, "Layout", new PhraseLayout(phraseMs - 500, phraseMs - 500 + 500 + (endMs - phraseMs) + 1000, 500, 500 + (endMs - phraseMs) + 1000));
        return phrase;
    }

    public static int Run(USinger singer, string configPath) {
        var project = new UProject();
        project.tempos.Clear();
        project.tempos.Add(new UTempo(0, 120));
        project.tempos.Add(new UTempo(480, 60));
        project.timeAxis.BuildSegments(project);
        var phrase = MakePhrase(singer, project.timeAxis);

        var pitch = VocaloidRenderer.CreatePitchCurve(phrase);
        Check(pitch.Values.Length == 9 && pitch.Values[0] == 60 && pitch.Values[8] == 68,
            "renderer pitch maps absolute cents to semitones");
        Check(Math.Abs(pitch.TimesSeconds[4] - 0.0208333333333333) < 1e-9 &&
              Math.Abs(pitch.TimesSeconds[5] - 0.03125) < 1e-9,
            "renderer pitch time grid follows 120-to-60 BPM tempo change");

        var progress = new Progress(1);
        typeof(Progress).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(progress, Task.CompletedTask); // Suppress UI dispatch in this headless integration probe.
        using var cancellation = new CancellationTokenSource();
        var renderer = new VocaloidRenderer();
        var expected = renderer.Layout(phrase);
        var result = renderer.Render(phrase, progress, 0, cancellation, false, null).GetAwaiter().GetResult();
        Check(result.samples != null && result.samples.Length == (int)Math.Ceiling(expected.estimatedLengthMs * 44.1),
            "renderer returns the complete 44.1 kHz padded layout");
        Check(Math.Abs(result.leadingMs - 500) < 0.001 && Math.Abs(result.positionMs - phrase.positionMs) < 0.001,
            "renderer result position and head padding match the phrase layout");

        int pcmOffset = (int)Math.Round((0.375) * 44100); // backend starts at -125 ms; layout starts at -500 ms.
        var around = result.samples!.Skip(Math.Max(0, pcmOffset)).Take(32).ToArray();
        float peak = around.Max(value => Math.Abs(value));
        int firstAudible = Array.FindIndex(result.samples!, value => Math.Abs(value) > 0.01f);
        Check(Math.Abs(firstAudible - pcmOffset) <= 32,
            $"renderer places fixture audio at the -125 ms source offset (expected={pcmOffset}, actual={firstAudible})");
        var fullGainPhrase = MakePhrase(singer, project.timeAxis);
        Set(fullGainPhrase, typeof(RenderPhrase), "dynamics", Enumerable.Repeat(1f, 9).ToArray());
        var fullGainResult = renderer.Render(fullGainPhrase, progress, 0, cancellation, false, null).GetAwaiter().GetResult();
        float fullGainPeak = fullGainResult.samples!.Skip(pcmOffset).Take(32).Max(value => Math.Abs(value));
        Check(fullGainPeak > 0 && Math.Abs(peak / fullGainPeak - 0.5f) < 0.03f,
            "48 kHz fixture PCM is resampled and local DYN is applied exactly once");
        return 6;
    }
}
