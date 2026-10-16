// Synthetic transport fixture only. This is never a production voice engine.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VocaloidBridge;

public static class FixtureHost {
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync() {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        int count = 0;
        while (true) {
            HostCall call;
            try { call = await BridgeWire.ReadJsonAsync<HostCall>(input, CancellationToken.None); }
            catch (EndOfStreamException) { return; }
            var reply = new HostReply { Id = call.Id };
            float[]? samples = null;
            if (call.Operation == "voices") {
                reply.Voices = new[] { new BridgeVoice { Id = "fixture", Name = "SYNTHETIC TEST FIXTURE" } };
            } else {
                var request = call.Request!;
                string lyric = request.Notes[0].Lyric;
                if (lyric == "hang") await Task.Delay(30000);
                if (lyric == "slow") await Task.Delay(600);
                if (lyric == "crash") Environment.Exit(71);
                if (lyric == "error") reply.Error = "fixture engine failed";
                else {
                    reply.SampleRate = 48000;
                    reply.StartSeconds = -0.125;
                    reply.SampleCount = 4800;
                    if (lyric == "oversize") reply.SampleCount = int.MaxValue;
                    if (lyric == "stale") reply.Id--;
                    samples = new float[4800];
                    samples[0] = ++count;
                    samples[1] = Environment.ProcessId;
                    if (lyric == "nan") samples[2] = float.NaN;
                }
            }
            await BridgeWire.WriteJsonAsync(output, reply, CancellationToken.None);
            if (samples != null && reply.SampleCount == samples.Length) {
                byte[] pcm = new byte[samples.Length * 4];
                Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
                await output.WriteAsync(pcm.AsMemory());
                await output.FlushAsync();
            }
        }
    }
}
