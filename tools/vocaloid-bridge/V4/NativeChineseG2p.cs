using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VocaloidBridge.V4 {
    /// <summary>Uses the installed V4 Mandarin G2P. One pinyin syllable per note.</summary>
    public sealed class NativeChineseG2p : IDisposable {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int One(int handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Two(int handle, IntPtr buffer);
        [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet=CharSet.Ansi, ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32")] static extern bool FreeLibrary(IntPtr module);
        IntPtr module;
        readonly One init;
        readonly One delete;
        readonly Two put;
        readonly Two get;
        readonly int handle;

        public NativeChineseG2p(string root) {
            if (IntPtr.Size != 4) throw new InvalidOperationException("V4 G2P requires an x86 process.");
            module=LoadLibraryW(Path.Combine(root,"g2pa4_CHS.dll"));
            if(module==IntPtr.Zero) throw new InvalidOperationException("Cannot load V4 Mandarin G2P: "+Marshal.GetLastWin32Error());
            try {
                init=Bind<One>("g2paInit"); delete=Bind<One>("g2paDelete");
                put=Bind<Two>("g2paPut"); get=Bind<Two>("g2paGet");
                handle=Bind<One>("g2paCreate")(0);
                if(handle<0) throw new InvalidOperationException("Cannot create V4 Mandarin G2P: "+handle);
            } catch { FreeLibrary(module); module=IntPtr.Zero; throw; }
        }
        public string Phonemize(string lyric) {
            if(module==IntPtr.Zero) throw new ObjectDisposedException("NativeChineseG2p");
            string pinyin=Regex.Replace((lyric??"").Trim().ToLowerInvariant().Replace('ü','v'),"[1-5]$","");
            if(!Regex.IsMatch(pinyin,"^[a-z]{1,12}$"))
                throw new InvalidDataException("V4 Mandarin requires one pinyin syllable per note; use OpenUtau for automatic single-Hanzi conversion. Unsupported lyric: "+lyric);
            IntPtr input=Marshal.AllocHGlobal(72), output=Marshal.AllocHGlobal(4096);
            try {
                Marshal.Copy(new byte[72],0,input,72); Marshal.Copy(new byte[4096],0,output,4096);
                byte[] text=Encoding.Unicode.GetBytes(pinyin+"\0");
                Marshal.Copy(text,0,IntPtr.Add(input,8),text.Length);
                if(init(handle)!=0 || put(handle,input)!=0 || put(handle,IntPtr.Zero)!=0)
                    throw new InvalidOperationException("V4 Mandarin G2P rejected lyric: "+pinyin);
                int result=get(handle,output);
                if(result!=0 || Marshal.ReadInt32(output)!=1)
                    throw new InvalidDataException("The lyric must map to exactly one Mandarin syllable: "+pinyin);
                // G2PA_3OUT starts with count, then phoneme UTF-16 rows at +4;
                // the separate original-lyric rows start at +516. Verified on
                // a / ni / hao / tian against the installed g2pa4_CHS.dll.
                string phones=ReadBounded(IntPtr.Add(output,4),32).Trim();
                if(phones.Length==0 || (phones=="a" && pinyin!="a"))
                    throw new InvalidDataException("V4 Mandarin G2P returned its fallback vowel for unsupported pinyin: "+pinyin);
                return phones;
            } finally {Marshal.FreeHGlobal(input);Marshal.FreeHGlobal(output);}
        }
        static string ReadBounded(IntPtr p,int capacity) {
            for(int i=0;i<capacity;i++) if(Marshal.ReadInt16(p,i*2)==0) return Marshal.PtrToStringUni(p,i);
            throw new InvalidDataException("Unterminated V4 G2P output.");
        }
        T Bind<T>(string name) where T:class {
            IntPtr p=GetProcAddress(module,name);
            if(p==IntPtr.Zero) throw new MissingMethodException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p,typeof(T));
        }
        public void Dispose() {
            if(module==IntPtr.Zero) return;
            try {delete(handle);} finally {FreeLibrary(module);module=IntPtr.Zero;}
        }
    }
}
