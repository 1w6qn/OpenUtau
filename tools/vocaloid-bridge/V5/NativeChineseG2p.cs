using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace VocaloidBridge.V5 {
    /// <summary>
    /// Adapts the installed VOCALOID 5 Simplified-Chinese G2P manager. The
    /// vendor API operates on a real VSM note, so callers must first insert a
    /// valid seed note and commit its sequence before calling Phonemize.
    /// Input is one OpenUtau-Romanized Mandarin pinyin syllable per note.
    /// </summary>
    public sealed class NativeChineseG2p : IDisposable {
        readonly Assembly api;
        object managerInterface;
        object manager;
        bool disposed;

        public NativeChineseG2p(string editorDirectory) {
            if (String.IsNullOrWhiteSpace(editorDirectory)) throw new ArgumentException("VOCALOID5 Editor directory is required.", "editorDirectory");
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("VOCALOID5 G2P requires an x64 process.");
            string apiPath = Path.Combine(Path.GetFullPath(editorDirectory), "VOCALOID5.dll");
            if (!File.Exists(apiPath)) throw new FileNotFoundException("VOCALOID5.dll was not found in the Editor directory.", apiPath);

            api = Assembly.LoadFrom(apiPath);
            Type interfaceType = api.GetType("Yamaha.VOCALOID.G2PA.G2PAManagerIF", true);
            Type languageType = api.GetType("Yamaha.VOCALOID.G2PA.G2PAManagerLangID", true);
            managerInterface = Activator.CreateInstance(interfaceType);
            try {
                MethodInfo create = interfaceType.GetMethod("CreateManager", new[] { languageType, typeof(string) });
                if (create == null) throw new MissingMethodException(interfaceType.FullName, "CreateManager(G2PAManagerLangID,string)");
                manager = create.Invoke(managerInterface, new[] { Enum.ToObject(languageType, 4), (object)Path.GetFullPath(editorDirectory) });
                if (manager == null) throw new InvalidOperationException("VOCALOID5 Simplified-Chinese G2P manager could not be created.");
            } catch {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Runs G2P on a VSM note already inserted in a committed sequence and
        /// returns the resulting space-separated VOCALOID phoneme string.
        /// </summary>
        public string Phonemize(object note, string lyric) {
            if (disposed) throw new ObjectDisposedException("NativeChineseG2p");
            if (note == null) throw new ArgumentNullException("note");
            if (note.GetType().FullName != "Yamaha.VOCALOID.VSM.WIVSMNote")
                throw new ArgumentException("A VOCALOID 5 WIVSMNote is required; G2P operates on a native note handle.", "note");

            string pinyin = NormalizePinyin(lyric);
            MethodInfo toPointer = note.GetType().GetMethod("op_Explicit", BindingFlags.Public | BindingFlags.Static, null, new[] { note.GetType() }, null);
            if (toPointer == null) throw new MissingMethodException(note.GetType().FullName, "op_Explicit(IntPtr)");
            IntPtr noteHandle = (IntPtr)toPointer.Invoke(null, new[] { note });
            if (noteHandle == IntPtr.Zero) throw new InvalidOperationException("The VSM note has no native handle.");

            MethodInfo setLyrics = manager.GetType().GetMethod("SetLyrics", new[] { typeof(IntPtr), typeof(string) });
            if (setLyrics == null) throw new MissingMethodException(manager.GetType().FullName, "SetLyrics(IntPtr,string)");
            if (!(bool)setLyrics.Invoke(manager, new object[] { noteHandle, pinyin }))
                throw new InvalidDataException("VOCALOID5 G2P rejected Mandarin pinyin: " + pinyin);

            PropertyInfo phonemesProperty = note.GetType().GetProperty("Phonemes", BindingFlags.Public | BindingFlags.Instance);
            string phonemes = phonemesProperty == null ? null : phonemesProperty.GetValue(note, null) as string;
            if (String.IsNullOrWhiteSpace(phonemes))
                throw new InvalidDataException("VOCALOID5 G2P returned no phonemes for pinyin: " + pinyin);
            if (!String.Equals(pinyin, "a", StringComparison.OrdinalIgnoreCase) && String.Equals(phonemes.Trim(), "a", StringComparison.Ordinal))
                throw new InvalidDataException("VOCALOID5 G2P returned its fallback vowel for unsupported pinyin: " + pinyin);
            return phonemes;
        }

        static string NormalizePinyin(string lyric) {
            string pinyin = (lyric ?? String.Empty).Trim().ToLowerInvariant().Replace('ü', 'v');
            pinyin = Regex.Replace(pinyin, "[1-5]$", String.Empty);
            if (!Regex.IsMatch(pinyin, "^[a-z]{1,12}$"))
                throw new InvalidDataException("VOCALOID5 Mandarin G2P requires one romanized pinyin syllable per note; unsupported input: " + lyric);
            return pinyin;
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            DisposeObject(manager);
            manager = null;
            DisposeObject(managerInterface);
            managerInterface = null;
        }

        static void DisposeObject(object value) {
            if (value == null) return;
            try {
                IDisposable disposable = value as IDisposable;
                if (disposable != null) disposable.Dispose();
                else {
                    MethodInfo dispose = value.GetType().GetMethod("Dispose", Type.EmptyTypes);
                    if (dispose != null) dispose.Invoke(value, null);
                }
            } catch { }
        }
    }
}
