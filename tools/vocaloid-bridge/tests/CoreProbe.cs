using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VocaloidBridge;

public static class CoreProbe {
    static int checks;
    static void Check(bool condition, string name) {
        if (!condition) throw new Exception("FAILED: " + name);
        checks++; Console.WriteLine("PASS: " + name);
    }
    static BridgeRequest Request(string lyric = "a", int tone = 60) => new BridgeRequest {
        VoiceId = "fixture",
        Notes = new[] { new BridgeNote { StartSeconds = 0, EndSeconds = 0.1, Tone = tone, Lyric = lyric } },
        Pitch = new BridgeCurve { TimesSeconds = new[] { 0.0, 0.1 }, Values = new[] { (double)tone, (double)tone } },
    };
    static async Task<Exception> Reject(Func<Task> action, string label) {
        Exception? rejected = null;
        try { await action(); } catch (Exception ex) { rejected = ex; }
        Check(rejected != null, label);
        return rejected!;
    }
    public static int Run(string root, string scratch, string pwsh) => RunAsync(root, scratch, pwsh).GetAwaiter().GetResult();
    static async Task<int> RunAsync(string root, string scratch, string pwsh) {
        Directory.CreateDirectory(scratch);
        var curve = new BridgeCurve { TimesSeconds = new[] { 0.0, 0.5, 1.0 }, Values = new[] { 60.0, 64.0, 62.0 } };
        curve.Validate();
        Check(curve.Sample(-1) == 60 && curve.Sample(2) == 62 && curve.Sample(0.25) == 62, "curve interpolation and endpoint holds");
        var invalid = Request(); invalid.Notes[0].EndSeconds = double.NaN;
        await Reject(() => { invalid.Validate(); return Task.CompletedTask; }, "non-finite note rejection");
        invalid = Request(); invalid.Pitch.TimesSeconds[1] = 0;
        await Reject(() => { invalid.Validate(); return Task.CompletedTask; }, "duplicate curve-time rejection");
        invalid = Request(); invalid.Notes = new[] { invalid.Notes[0], invalid.Notes[0] };
        await Reject(() => { invalid.Validate(); return Task.CompletedTask; }, "overlapping note rejection");

        string path = Path.Combine(scratch, "test-config.json");
        var config = new BridgeConfig {
            HostExecutable = pwsh, HostScript = Path.Combine(root, "tests", "fixture-host.ps1"),
            EngineAssembly = Path.Combine(root, "tests", "FixtureHost.cs"), EngineDirectory = root,
            VoiceId = "fixture", TimeoutSeconds = 5, CacheBytes = 4800 * 4 * 2,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        using var session = BridgeSession.Get(path);
        var voices = await session.ListVoicesAsync(CancellationToken.None);
        Check(voices.Length == 1 && voices[0].Id == "fixture", "framed voice enumeration");
        var first = await session.RenderAsync(Request(), CancellationToken.None);
        float pid = first.Samples[1];
        Check(first.SampleRate == 48000 && first.StartSeconds == -0.125 && first.Samples.Length == 4800, "PCM framing, sample rate and start offset");
        first.Samples[0] = -100;
        var cached = await session.RenderAsync(Request(), CancellationToken.None);
        Check(cached.Samples[0] == 1 && cached.Samples[1] == pid, "cache hit and caller mutation isolation");
        var second = await session.RenderAsync(Request(tone: 62), CancellationToken.None);
        Check(second.Samples[0] == 2 && second.Samples[1] == pid, "pitch invalidation and warm process reuse");
        await session.RenderAsync(Request(tone: 64), CancellationToken.None);
        var evicted = await session.RenderAsync(Request(), CancellationToken.None);
        Check(evicted.Samples[0] == 4, "bounded LRU eviction");

        using (var cancel = new CancellationTokenSource()) {
            var pending = session.RenderAsync(Request("slow"), CancellationToken.None);
            var queued = session.RenderAsync(Request("queued"), cancel.Token);
            cancel.Cancel();
            await Reject(async () => { await queued; }, "queued render cancellation");
            var completed = await pending;
            Check(completed.Samples[1] == pid, "queued cancellation preserves active engine");
        }
        using (var cancel = new CancellationTokenSource(200)) {
            var watch = Stopwatch.StartNew();
            await Reject(async () => { await session.RenderAsync(Request("hang"), cancel.Token); }, "active render cancellation");
            Check(watch.Elapsed.TotalSeconds < 3, "bounded cancellation latency");
        }
        var restarted = await session.RenderAsync(Request("after-cancel"), CancellationToken.None);
        Check(restarted.Samples[0] == 1 && restarted.Samples[1] != pid, "process recovery after cancellation");

        foreach (string failure in new[] { "crash", "error", "nan", "stale", "oversize" }) {
            await Reject(async () => { await session.RenderAsync(Request(failure), CancellationToken.None); }, failure + " rejection");
            var recovery = await session.RenderAsync(Request("after-" + failure), CancellationToken.None);
            Check(recovery.Samples[0] == 1, failure + " restart without caching bad audio");
        }
        var timeoutWatch = Stopwatch.StartNew();
        var timeoutError = await Reject(async () => { await session.RenderAsync(Request("hang"), CancellationToken.None); }, "hung engine timeout");
        Check(timeoutError is TimeoutException, "timeout remains distinct from user cancellation");
        Check(timeoutWatch.Elapsed.TotalSeconds < 9, "timeout includes startup and synthesis");
        config.Revision = "2";
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        using var changed = BridgeSession.Get(path);
        Check(!ReferenceEquals(session, changed), "configuration revision replaces session");
        var fresh = await changed.RenderAsync(Request(), CancellationToken.None);
        Check(fresh.Samples[0] == 1, "configuration change invalidates cache");
        return checks;
    }
}
