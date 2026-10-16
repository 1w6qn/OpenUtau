using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VocaloidBridge {
    public static class BridgeWire {
        public const int MaxHeaderBytes = 8 * 1024 * 1024;

        public static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken ct) {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
            if (payload.Length > MaxHeaderBytes) throw new InvalidDataException("Bridge message is too large.");
            byte[] length = BitConverter.GetBytes(payload.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(length);
            await stream.WriteAsync(length.AsMemory(), ct).ConfigureAwait(false);
            await stream.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct) {
            byte[] length = new byte[4];
            await stream.ReadExactlyAsync(length.AsMemory(), ct).ConfigureAwait(false);
            if (!BitConverter.IsLittleEndian) Array.Reverse(length);
            int count = BitConverter.ToInt32(length, 0);
            if (count < 2 || count > MaxHeaderBytes) throw new InvalidDataException("Invalid bridge frame length.");
            byte[] payload = new byte[count];
            await stream.ReadExactlyAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Null bridge message.");
        }
    }

    /// <summary>One warm, serialized engine process per configuration. No native engine runs in OpenUtau.</summary>
    public sealed class BridgeSession : IDisposable {
        static readonly object sessionsLock = new object();
        static readonly Dictionary<string, BridgeSession> sessions = new Dictionary<string, BridgeSession>(StringComparer.OrdinalIgnoreCase);
        static BridgeSession() {
            AppDomain.CurrentDomain.ProcessExit += (_, __) => {
                lock (sessionsLock) foreach (var s in sessions.Values) s.AbortProcess();
            };
        }

        readonly string configPath;
        readonly string fingerprint;
        readonly BridgeConfig config;
        readonly SemaphoreSlim lane = new SemaphoreSlim(1, 1);
        readonly object processLock = new object();
        readonly Dictionary<string, LinkedListNode<CacheEntry>> cache = new Dictionary<string, LinkedListNode<CacheEntry>>();
        readonly LinkedList<CacheEntry> lru = new LinkedList<CacheEntry>();
        long cacheBytes;
        long nextId;
        Process? process;
        string stderr = "";
        bool disposed;
        public string Fingerprint => fingerprint;
        public bool IsDisposed => Volatile.Read(ref disposed);
        sealed class CacheEntry {
            public string Key = "";
            public BridgeAudio Audio = new BridgeAudio();
            public long Bytes => Audio.Samples.LongLength * sizeof(float);
        }

        BridgeSession(string path, BridgeConfig c, string stamp) {
            configPath = path; config = c; fingerprint = stamp;
        }

        public static BridgeSession Get(string path) {
            path = Path.GetFullPath(path);
            var c = BridgeConfig.Load(path);
            // Configuration + engine/SDK file identity. For voice database changes, bump Revision.
            var stamp = new StringBuilder(File.ReadAllText(path));
            stamp.Append("\nprotocol=1\n");
            if (Directory.Exists(c.EngineDirectory)) {
                foreach (var file in Directory.GetFiles(c.EngineDirectory, "*.dll").OrderBy(p => p, StringComparer.Ordinal)) {
                    var f = new FileInfo(file);
                    stamp.Append(f.FullName).Append(':').Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
                }
            }
            foreach (var file in new[] { c.EngineAssembly, c.HostScript }) {
                if (File.Exists(file)) stamp.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
            }
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString())));
            lock (sessionsLock) {
                if (sessions.TryGetValue(path, out var current) && current.fingerprint == fingerprint && !current.disposed)
                    return current;
                current?.Dispose();
                var created = new BridgeSession(path, c, fingerprint);
                sessions[path] = created;
                return created;
            }
        }

        public async Task<BridgeVoice[]> ListVoicesAsync(CancellationToken ct) {
            await lane.WaitAsync(ct).ConfigureAwait(false);
            try {
                var reply = await CallAsync(new HostCall { Operation = "voices" }, ct).ConfigureAwait(false);
                return reply.Voices ?? throw new InvalidDataException("Engine did not return a voice list.");
            } finally { lane.Release(); }
        }

        public async Task<BridgeVoice[]> ImportVoicesAsync(string directory, CancellationToken ct) {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Voicebank directory was not found: " + directory);
            await lane.WaitAsync(ct).ConfigureAwait(false);
            try {
                var reply = await CallAsync(new HostCall { Operation = "import", ImportDirectory = Path.GetFullPath(directory) }, ct).ConfigureAwait(false);
                // Registration can change voice availability: cached audio must not survive an import.
                cache.Clear(); lru.Clear(); cacheBytes = 0;
                return reply.Voices ?? throw new InvalidDataException("Engine did not return imported voice information.");
            } finally { lane.Release(); }
        }

        public async Task<BridgeAudio> RenderAsync(BridgeRequest request, CancellationToken ct) {
            request.Validate();
            string key = request.CacheKey(fingerprint);
            await lane.WaitAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            try {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                if (cache.TryGetValue(key, out var node)) {
                    lru.Remove(node); lru.AddLast(node);
                    return node.Value.Audio.Clone();
                }
                var reply = await CallAsync(new HostCall { Operation = "render", Request = request }, timeout.Token).ConfigureAwait(false);
                if (reply.SampleCount <= 0 || reply.SampleRate < 8000 || reply.SampleRate > 192000 ||
                    reply.SampleCount > BridgeAudio.MaxSampleCount ||
                    reply.SampleCount > reply.SampleRate * 320L)
                    throw new InvalidDataException("Engine returned an invalid PCM length or sample rate.");
                byte[] data = new byte[checked(reply.SampleCount * sizeof(float))];
                try {
                    await process!.StandardOutput.BaseStream.ReadExactlyAsync(data.AsMemory(), timeout.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    throw new TimeoutException("VOCALOID synthesis timed out while transferring audio.");
                }
                if (!BitConverter.IsLittleEndian) {
                    for (int i = 0; i < data.Length; i += 4) Array.Reverse(data, i, 4);
                }
                var result = new BridgeAudio { StartSeconds = reply.StartSeconds, SampleRate = reply.SampleRate, Samples = new float[reply.SampleCount] };
                Buffer.BlockCopy(data, 0, result.Samples, 0, data.Length);
                result.Validate();
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                PutCache(key, result);
                return result.Clone();
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested) {
                AbortProcess();
                throw new TimeoutException("VOCALOID synthesis exceeded the configured timeout.");
            } catch {
                // Cancellation, corrupt output and native failures all invalidate the entire engine state.
                AbortProcess();
                throw;
            } finally { lane.Release(); }
        }

        async Task<HostReply> CallAsync(HostCall call, CancellationToken ct) {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            try {
                StartProcess();
                call.Id = ++nextId;
                var p = process!;
                await BridgeWire.WriteJsonAsync(p.StandardInput.BaseStream, call, timeout.Token).ConfigureAwait(false);
                var reply = await BridgeWire.ReadJsonAsync<HostReply>(p.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false);
                if (reply.Id != call.Id || reply.Protocol != 1) throw new InvalidDataException("Stale or incompatible engine reply.");
                if (reply.Error != null) throw new InvalidOperationException("VOCALOID bridge: " + reply.Error);
                return reply;
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                AbortProcess();
                throw new TimeoutException("VOCALOID synthesis exceeded the configured timeout.");
            } catch (EndOfStreamException ex) {
                string details;
                lock (processLock) details = stderr;
                AbortProcess();
                throw new IOException("VOCALOID engine process ended unexpectedly. " + details, ex);
            } catch {
                AbortProcess();
                throw;
            }
        }

        void StartProcess() {
            lock (processLock) {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (process != null && !process.HasExited) return;
                process?.Dispose(); process = null;
                if (!File.Exists(config.HostScript)) throw new FileNotFoundException("Bridge host script was not found.", config.HostScript);
                if (!File.Exists(config.EngineAssembly)) throw new FileNotFoundException("The configured VOCALOID engine or bridge DLL was not found.", config.EngineAssembly);
                var info = new ProcessStartInfo(config.HostExecutable) {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = config.EngineDirectory,
                };
                info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-File"); info.ArgumentList.Add(config.HostScript);
                info.ArgumentList.Add("-Config"); info.ArgumentList.Add(configPath);
                var p = new Process { StartInfo = info };
                stderr = "";
                p.ErrorDataReceived += (_, e) => {
                    if (e.Data == null) return;
                    lock (processLock) {
                        stderr += e.Data + "\n";
                        if (stderr.Length > 4096) stderr = stderr.Substring(stderr.Length - 4096);
                    }
                };
                if (!p.Start()) { p.Dispose(); throw new IOException("Cannot start VOCALOID engine host."); }
                process = p;
                p.BeginErrorReadLine();
            }
        }

        void PutCache(string key, BridgeAudio audio) {
            long bytes = audio.Samples.LongLength * sizeof(float);
            if (bytes > config.CacheBytes) return;
            while (cacheBytes + bytes > config.CacheBytes && lru.First != null) {
                var old = lru.First.Value;
                cacheBytes -= old.Bytes; cache.Remove(old.Key); lru.RemoveFirst();
            }
            var entry = new CacheEntry { Key = key, Audio = audio };
            cache[key] = lru.AddLast(entry); cacheBytes += bytes;
        }

        void AbortProcess() {
            lock (processLock) {
                if (process == null) return;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose(); process = null;
            }
        }

        public void Dispose() {
            lock (processLock) disposed = true;
            AbortProcess();
            // The lane may still be unwinding a cancelled render. Do not dispose it here.
        }
    }
}
