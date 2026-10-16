using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VocaloidBridge {
    public static class EngineHost {
        public static void Run(string configPath) => RunAsync(configPath).GetAwaiter().GetResult();

        static async Task RunAsync(string configPath) {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            // Managed extension logs must not enter the framed PCM protocol.
            Console.SetOut(Console.Error);
            var config = BridgeConfig.Load(configPath);
            using var backend = new LegacyTuneLabBackend(config.EngineAssembly, config.EngineType, config.EngineDirectory);
            while (true) {
                HostCall call;
                try { call = await BridgeWire.ReadJsonAsync<HostCall>(input, CancellationToken.None).ConfigureAwait(false); }
                catch (EndOfStreamException) { return; }
                var reply = new HostReply { Id = call.Id };
                BridgeAudio? audio = null;
                try {
                    if (call.Protocol != 1) throw new InvalidDataException("Unsupported bridge protocol version.");
                    if (call.Operation == "voices") reply.Voices = backend.ListVoices().ToArray();
                    else if (call.Operation == "render") {
                        var request = call.Request ?? throw new InvalidDataException("Missing render request.");
                        request.Validate();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(config.TimeoutSeconds));
                        audio = await backend.RenderAsync(request, timeout.Token).ConfigureAwait(false);
                        audio.Validate();
                        reply.StartSeconds = audio.StartSeconds;
                        reply.SampleRate = audio.SampleRate;
                        reply.SampleCount = audio.Samples.Length;
                    } else throw new InvalidDataException("Unknown bridge operation.");
                } catch (Exception ex) {
                    // Native state is no longer trusted. Report exactly once, then leave this process.
                    reply.Error = ex.GetBaseException().Message;
                    await BridgeWire.WriteJsonAsync(output, reply, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                await BridgeWire.WriteJsonAsync(output, reply, CancellationToken.None).ConfigureAwait(false);
                if (audio != null) {
                    byte[] pcm = new byte[audio.Samples.Length * sizeof(float)];
                    Buffer.BlockCopy(audio.Samples, 0, pcm, 0, pcm.Length);
                    if (!BitConverter.IsLittleEndian) for (int i = 0; i < pcm.Length; i += 4) Array.Reverse(pcm, i, 4);
                    await output.WriteAsync(pcm.AsMemory()).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
