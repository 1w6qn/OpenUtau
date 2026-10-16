using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenUtau.Classic;
using OpenUtau.Core.Ustx;
using VocaloidBridge;

namespace OpenUtau.Core.Vocaloid {
    /// <summary>Classic voicebank metadata plus a Vocaloid bridge configuration.</summary>
    public sealed class VocaloidSinger : USinger {
        public static readonly USingerType Type = (USingerType)0x7;
        readonly Voicebank voicebank;
        readonly List<UOto> otos = new List<UOto>();
        readonly List<USubbank> subbanks = new List<USubbank>();
        readonly List<string> errors = new List<string>();
        readonly string configPath;

        public override string Id => voicebank.Id;
        public override string Name => voicebank.Name;
        public override Dictionary<string, string> LocalizedNames => voicebank.LocalizedNames;
        public override IList<string> SearchTerms => voicebank.SearchTerms;
        public override USingerType SingerType => Type;
        public override string BasePath => voicebank.BasePath;
        public override string Author => voicebank.Author;
        public override string Voice => voicebank.Voice;
        public override string Location => Path.GetDirectoryName(voicebank.File) ?? string.Empty;
        public override string Web => voicebank.Web;
        public override string Version => voicebank.Version;
        public override string OtherInfo => voicebank.OtherInfo;
        public override IList<string> Errors => errors;
        public override string Avatar => string.IsNullOrEmpty(voicebank.Image) ? null! : Path.Combine(Location, voicebank.Image);
        public override string Portrait => string.IsNullOrEmpty(voicebank.Portrait) ? null! : Path.Combine(Location, voicebank.Portrait);
        public override float PortraitOpacity => voicebank.PortraitOpacity;
        public override int PortraitHeight => voicebank.PortraitHeight;
        public override string Sample => string.IsNullOrEmpty(voicebank.Sample) ? null! : Path.Combine(Location, voicebank.Sample);
        public override string DefaultPhonemizer => "OpenUtau.Core.DefaultPhonemizer";
        public override Encoding TextFileEncoding => voicebank.TextFileEncoding ?? Encoding.UTF8;
        public override IList<USubbank> Subbanks => subbanks;
        public override IList<UOto> Otos => otos;

        public VocaloidSinger(Voicebank voicebank) {
            this.voicebank = voicebank ?? throw new ArgumentNullException(nameof(voicebank));
            configPath = Path.Combine(Location, "vocaloid-bridge.json");
            found = File.Exists(configPath);
            subbanks.Add(new USubbank(new Subbank { Prefix = "", Suffix = "", ToneRanges = new[] { "C1-B7" }, Color = "" }));
            if (!found) errors.Add($"Missing Vocaloid bridge configuration: {configPath}");
        }

        public override void EnsureLoaded() {
            if (!Loaded) Reload();
        }

        public override void Reload() {
            loaded = false;
            otos.Clear();
            errors.Clear();
            found = File.Exists(configPath);
            if (!found) {
                errors.Add($"Missing Vocaloid bridge configuration: {configPath}");
                return;
            }
            try {
                BridgeConfig.Load(configPath);
                loaded = true;
            } catch (Exception e) {
                errors.Add(e.Message);
            }
        }

        public override bool TryGetOto(string phoneme, out UOto oto) {
            if (!string.IsNullOrWhiteSpace(phoneme)) {
                oto = UOto.OfDummy(phoneme);
                return true;
            }
            oto = null!;
            return false;
        }

        public override Dictionary<string, UOto> GetSuggestions(string text, bool isAlias) =>
            new Dictionary<string, UOto>(StringComparer.OrdinalIgnoreCase);

        public override byte[] LoadPortrait() => string.IsNullOrEmpty(Portrait) || !File.Exists(Portrait) ? null! : File.ReadAllBytes(Portrait);
        public override byte[] LoadSample() => string.IsNullOrEmpty(Sample) || !File.Exists(Sample) ? null! : File.ReadAllBytes(Sample);
    }
}
