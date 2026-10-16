using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VocaloidBridge;

namespace OpenUtau.Core.Vocaloid {
    public sealed class VocaloidVoicebankImportResult {
        public BridgeVoice[] Voices { get; init; } = Array.Empty<BridgeVoice>();
        public string SingerDirectory { get; init; } = "";
    }

    /// <summary>Registers installed voice data with V5 and creates lightweight OpenUtau proxy singers.</summary>
    public static class VocaloidVoicebankImporter {
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string FindEngineDirectory() {
            foreach (var singer in SingerManager.Inst.Singers.Values.OfType<VocaloidSinger>().ToArray()) {
                try {
                    var config = BridgeConfig.Load(Path.Combine(singer.Location, "vocaloid-bridge.json"));
                    if (config.EngineType == "VOCALOID5") return EditorDirectory(config.EngineDirectory);
                } catch (IOException) { } catch (JsonException) { } catch (UnauthorizedAccessException) { }
            }
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string name in new[] { "VOCALOID5", "Yamaha/VOCALOID5", "POCALOID5ESV" }) {
                try { return EditorDirectory(Path.Combine(programFiles, name)); } catch (DirectoryNotFoundException) { }
            }
            return "";
        }

        public static async Task<VocaloidVoicebankImportResult> ImportAsync(
            string engineDirectory, string voiceDirectory, CancellationToken cancellation) {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VOCALOID 5 voicebank import requires Windows.");
            string editor = EditorDirectory(engineDirectory);
            string voiceRoot = Path.GetFullPath(voiceDirectory);
            if (!Directory.Exists(voiceRoot)) throw new DirectoryNotFoundException("Voicebank directory was not found: " + voiceRoot);
            string hostScript = FindHostScript();
            string singerRoot = Path.GetFullPath(PathManager.Inst.SingersInstallPath);
            string scratch = Path.Combine(PathManager.Inst.CachePath, "vocaloid-import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            var config = new BridgeConfig {
                HostExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                HostScript = hostScript, EngineAssembly = Path.Combine(editor, "VOCALOID5.dll"),
                EngineDirectory = editor, EngineType = "VOCALOID5", LyricLanguage = "zh-CN", TimeoutSeconds = 120,
            };
            string configPath = Path.Combine(scratch, "import.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, JsonOptions), cancellation).ConfigureAwait(false);
            BridgeSession? session = null;
            try {
                session = BridgeSession.Get(configPath);
                var voices = await session.ImportVoicesAsync(voiceRoot, cancellation).ConfigureAwait(false);
                if (voices.Length == 0) throw new InvalidDataException("No installed VOCALOID voicebanks were found in the selected directory.");
                foreach (var voice in voices) {
                    if (voice.Language != "zh-CN") throw new NotSupportedException("The current bridge supports Mandarin voicebanks only: " + voice.Name);
                    ValidateDestination(singerRoot, voice);
                }
                foreach (var voice in voices) {
                    cancellation.ThrowIfCancellationRequested();
                    WriteSinger(singerRoot, voice, config);
                }
                return new VocaloidVoicebankImportResult { Voices = voices, SingerDirectory = singerRoot };
            } finally {
                session?.Dispose();
                if (File.Exists(configPath)) File.Delete(configPath);
                if (Directory.Exists(scratch) && Directory.GetFileSystemEntries(scratch).Length == 0) Directory.Delete(scratch);
            }
        }

        static string EditorDirectory(string path) {
            if (string.IsNullOrWhiteSpace(path)) throw new DirectoryNotFoundException("Select the VOCALOID 5 engine directory.");
            path = Path.GetFullPath(path);
            if (File.Exists(Path.Combine(path, "Editor", "VOCALOID5.dll"))) path = Path.Combine(path, "Editor");
            if (!File.Exists(Path.Combine(path, "VOCALOID5.dll")))
                throw new DirectoryNotFoundException("VOCALOID5.dll was not found in the selected engine directory.");
            return path;
        }

        static string FindHostScript() {
            foreach (string root in new[] { PathManager.Inst.RootPath, AppContext.BaseDirectory }) {
                string candidate = Path.Combine(root, "VocaloidBridge", "V5", "run-native-host.ps1");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                var directory = new DirectoryInfo(root);
                for (int i = 0; i < 8 && directory != null; i++, directory = directory.Parent) {
                    foreach (string prefix in new[] { "tools", "plugins" }) {
                        candidate = Path.Combine(directory.FullName, prefix, "vocaloid-bridge", "V5", "run-native-host.ps1");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            throw new FileNotFoundException("The VOCALOID 5 bridge helper is missing. Reinstall the complete bridge build.");
        }

        static string Destination(string singerRoot, string voiceId) => Path.Combine(singerRoot, "vocaloid-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(voiceId))).Substring(0, 16).ToLowerInvariant());

        static void ValidateDestination(string singerRoot, BridgeVoice voice) {
            if (!voice.Id.StartsWith("v5:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(voice.Name))
                throw new InvalidDataException("The engine returned an invalid imported voice ID/name.");
            string destination = Destination(singerRoot, voice.Id);
            if (!Directory.Exists(destination)) return;
            string path = Path.Combine(destination, "vocaloid-bridge.json");
            if (!File.Exists(path) || BridgeConfig.Load(path).VoiceId != voice.Id)
                throw new IOException("An unrelated singer directory already exists: " + destination);
        }

        static void WriteSinger(string singerRoot, BridgeVoice voice, BridgeConfig defaults) {
            string destination = Destination(singerRoot, voice.Id);
            Directory.CreateDirectory(destination);
            string path = Path.Combine(destination, "vocaloid-bridge.json");
            var config = File.Exists(path) ? BridgeConfig.Load(path) : new BridgeConfig();
            config.HostExecutable = defaults.HostExecutable; config.HostScript = Path.GetRelativePath(destination, defaults.HostScript);
            config.EngineAssembly = defaults.EngineAssembly; config.EngineDirectory = defaults.EngineDirectory;
            config.EngineType = "VOCALOID5"; config.VoiceId = voice.Id; config.LyricLanguage = voice.Language;
            config.Revision = Guid.NewGuid().ToString("N");
            config.TimeoutSeconds = Math.Max(config.TimeoutSeconds, 120);
            string name = voice.Name.Replace('\r', ' ').Replace('\n', ' ');
            string displayName = voice.Ready ? name : name + " [待渲染验证]";
            // Preserve any user-authored artwork/name metadata on idempotent re-import.
            string character = Path.Combine(destination, "character.txt");
            string voiceMetadata = Path.Combine(destination, "vocaloid-voice.json");
            bool updateName = !File.Exists(character);
            if (!updateName && File.Exists(voiceMetadata)) {
                try {
                    var previous = JsonSerializer.Deserialize<BridgeVoice>(File.ReadAllText(voiceMetadata));
                    if (previous != null && !string.IsNullOrWhiteSpace(previous.Name)) {
                        string oldBaseName = previous.Name.Replace('\r', ' ').Replace('\n', ' ');
                        string[] generatedNames = {
                            oldBaseName,
                            oldBaseName + " [待 V5 授权]",
                            oldBaseName + " [待渲染验证]",
                        };
                        var existingLines = File.ReadAllLines(character);
                        updateName = generatedNames.Any(oldName => existingLines.Contains("name=" + oldName));
                    }
                } catch (JsonException) { }
            }
            if (updateName) {
                File.WriteAllText(character, "name=" + displayName + "\nauthor=VOCALOID local engine bridge\n");
                File.WriteAllText(Path.Combine(destination, "character.yaml"), "name: " + JsonSerializer.Serialize(displayName) +
                    "\nsinger_type: vocaloid\ntext_file_encoding: utf-8\ndefault_phonemizer: OpenUtau.Core.DefaultPhonemizer\n");
            }
            File.WriteAllText(voiceMetadata, JsonSerializer.Serialize(voice, JsonOptions));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
                File.Move(temporary, path, true);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
