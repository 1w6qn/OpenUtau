using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace VocaloidBridge.V4 {
    public sealed class V4Note {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public int Tone { get; set; }
        public string Lyric { get; set; }
    }
    public sealed class V4Curve {
        public double[] TimesSeconds { get; set; }
        public double[] Values { get; set; }
        public double Sample(double time) {
            int i=Array.BinarySearch(TimesSeconds,time);
            if(i>=0)return Values[i];i=~i;
            if(i==0)return Values[0];if(i==Values.Length)return Values[i-1];
            double t=(time-TimesSeconds[i-1])/(TimesSeconds[i]-TimesSeconds[i-1]);
            return Values[i-1]+t*(Values[i]-Values[i-1]);
        }
    }
    public sealed class V4Request {
        public string VoiceId { get; set; }
        public V4Note[] Notes { get; set; }
        public V4Curve Pitch { get; set; }
        public Dictionary<string,object> Automations { get; set; }
        public Dictionary<string,object> PartProperties { get; set; }
        public Dictionary<string,object>[] NoteProperties { get; set; }
        public void Validate() {
            if(Notes==null||Notes.Length<1||Notes.Length>10000)throw new InvalidDataException("A V4 phrase requires 1..10000 notes.");
            for(int i=0;i<Notes.Length;i++) {
                var n=Notes[i];
                if(n==null||!Finite(n.StartSeconds)||!Finite(n.EndSeconds)||n.StartSeconds<0||n.EndSeconds>300||n.EndSeconds<=n.StartSeconds||n.Tone<0||n.Tone>127||String.IsNullOrWhiteSpace(n.Lyric)||n.Lyric.Length>32||(i>0&&n.StartSeconds<Notes[i-1].EndSeconds-0.000001))
                    throw new InvalidDataException("V4 notes must be ordered, monophonic, start at or after zero, and fit within 300 seconds.");
            }
            if(Pitch==null||Pitch.Values==null||Pitch.TimesSeconds==null||Pitch.Values.Length<1||Pitch.Values.Length>200000||Pitch.Values.Length!=Pitch.TimesSeconds.Length)
                throw new InvalidDataException("An absolute MIDI pitch curve is required.");
            for(int i=0;i<Pitch.Values.Length;i++)if(!Finite(Pitch.Values[i])||!Finite(Pitch.TimesSeconds[i])||(i>0&&Pitch.TimesSeconds[i]<=Pitch.TimesSeconds[i-1]))throw new InvalidDataException("Invalid V4 pitch curve.");
            if((Automations!=null&&Automations.Count!=0)||(PartProperties!=null&&PartProperties.Count!=0)||(NoteProperties!=null&&NoteProperties.Any(p=>p==null||p.Count!=0)))
                throw new InvalidDataException("Direct V4 currently supports lyrics, absolute pitch and notes; engine-specific properties are not supported.");
        }
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
    }
    public sealed class V4Call {
        public int Protocol { get; set; }
        public long Id { get; set; }
        public string Operation { get; set; }
        public V4Request Request { get; set; }
    }
    public sealed class V4Config {
        public string EngineDirectory { get; set; }
        public double TailMs { get; set; }
        public double TimeoutSeconds { get; set; }
    }
    /// <summary>Protocol-compatible x86 .NET Framework host; stdout is binary only.</summary>
    public static class NativeHost {
        const int MaxHeader=8*1024*1024;
        public static void Run(string configPath) {
            Stream input=Console.OpenStandardInput(),output=Console.OpenStandardOutput();
            Console.SetOut(Console.Error);
            var json=new JavaScriptSerializer {MaxJsonLength=MaxHeader,RecursionLimit=64};
            var config=json.Deserialize<V4Config>(File.ReadAllText(configPath));
            string root=Path.GetFullPath(Path.IsPathRooted(config.EngineDirectory)?config.EngineDirectory:Path.Combine(Path.GetDirectoryName(configPath),config.EngineDirectory));
            using(var api=NativeApi.Load(root)) {
                byte a,b,c,d;double compatibility=20012012.0;
                Check(api.GetVersion(out a,out b,out c,out d,ref compatibility),"DSEGetVersion");
                if((a|(b<<8)|(c<<16)|(d<<24))!=NativeApi.V4Token)throw new InvalidOperationException("Unsupported V4 private ABI version.");
                IntPtr registryPattern=Marshal.StringToHGlobalAnsi("SOFTWARE\\VCLDASGN3\\%s"),databasePath=Marshal.StringToHGlobalAnsi(root);
                try { api.SetDbmPath(registryPattern,databasePath); }
                finally {Marshal.FreeHGlobal(registryPattern);Marshal.FreeHGlobal(databasePath);}
                Check(api.SetVVoiceTableV4(NativeApi.V4Token,IntPtr.Zero),"DSESetVVoiceTableV4");
                var voices=VoiceCatalog.Enumerate(root);
                using(var g2p=new NativeChineseG2p(root))
                using(var frontend=new NativeFrontend(root)) {
                    ConfiguredDse engine=null;
                    try {
                    while(true) {
                        byte[] frame=ReadFrame(input);if(frame==null)return;
                        var call=json.Deserialize<V4Call>(Encoding.UTF8.GetString(frame));
                        var reply=new Dictionary<string,object>{{"Protocol",1},{"Id",call.Id}};
                        NativeAudio audio=null;
                        try {
                            if(call.Protocol!=1)throw new InvalidDataException("Unsupported bridge protocol.");
                            if(call.Operation=="voices") {
                                // MIDI bank 4 identifies Mandarin, not the voicebank generation.
                                reply["Voices"]=voices.Select(v=>new {Id=v.ID,Name=v.Name,Language=v.Bank==4?"zh-CN":""}).ToArray();
                            } else if(call.Operation=="render") {
                                var request=call.Request;if(request==null)throw new InvalidDataException("Missing V4 render request.");request.Validate();
                                var voice=voices.SingleOrDefault(v=>String.Equals(v.ID,request.VoiceId,StringComparison.Ordinal));
                                if(voice==null)throw new InvalidDataException("Installed V4 voice ID not found: "+request.VoiceId);
                                if(voice.Bank!=4)throw new NotSupportedException("This direct V4 backend currently supports Mandarin voices (Bank 4).");
                                string scratch=Path.Combine(Path.GetTempPath(),"openutau-vocaloid-v4",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
                                string score=Path.Combine(scratch,"phrase.vsqx");
                                try {
                                    Console.Error.WriteLine("V4: writing score for request "+call.Id);
                                    V4ScoreWriter.Write(score,request,voice,g2p,config.TailMs);
                                    if(engine==null)engine=new ConfiguredDse(api);
                                    Console.Error.WriteLine("V4: encoding phrase for request "+call.Id);
                                    using(var phrase=frontend.Load(score)) {
                                        Console.Error.WriteLine("V4: synthesizing request "+call.Id);
                                        audio=NativeSynthesis.Render(api,engine.Handle,phrase.DaisyBuffer);
                                    }
                                    Console.Error.WriteLine("V4: completed request "+call.Id);
                                } finally {
                                    // The only files created by this operation are inside a fresh GUID directory.
                                    if(File.Exists(score))File.Delete(score);
                                    if(Directory.GetFileSystemEntries(scratch).Length==0)Directory.Delete(scratch);
                                }
                                if(audio.Samples.Length==0||audio.Samples.Length>16777216)throw new InvalidDataException("V4 returned invalid PCM length.");
                                if(!audio.Samples.Any(s=>Math.Abs(s)>0.000001f))throw new InvalidDataException("V4 returned silent PCM; synthesis is not verified.");
                                reply["StartSeconds"]=audio.StartSeconds-V4ScoreWriter.OriginSeconds;
                                reply["SampleRate"]=audio.SampleRate;reply["SampleCount"]=audio.Samples.Length;
                            } else throw new InvalidDataException("Unknown bridge operation.");
                        } catch(Exception e) {reply["Error"]=e.GetBaseException().Message;WriteFrame(output,json.Serialize(reply));return;}
                        WriteFrame(output,json.Serialize(reply));
                        if(audio!=null){byte[] pcm=new byte[audio.Samples.Length*4];Buffer.BlockCopy(audio.Samples,0,pcm,0,pcm.Length);output.Write(pcm,0,pcm.Length);output.Flush();}
                    }
                    } finally {if(engine!=null)engine.Dispose();}
                }
            }
        }
        sealed class ConfiguredDse : IDisposable {
            readonly NativeApi api;
            readonly IntPtr emptyPath;
            public uint Handle {get;private set;}
            public ConfiguredDse(NativeApi api) {
                this.api=api;int handle=api.Create(NativeApi.V4Token);
                if(handle<0)throw new InvalidOperationException("DSECreate failed: "+handle);
                Handle=(uint)handle;emptyPath=Marshal.StringToHGlobalAnsi("");
                try {IntPtr table;Check(api.SetStaticSetting(Handle,44100,256,emptyPath,0,1,0,out table),"DSESetStaticSetting");}
                catch {api.Delete(Handle);Marshal.FreeHGlobal(emptyPath);throw;}
            }
            public void Dispose(){try{api.Delete(Handle);}finally{Marshal.FreeHGlobal(emptyPath);}}
        }
        static void Check(int code,string name){if(code!=0)throw new InvalidOperationException(name+" failed: "+code);}
        static byte[] ReadFrame(Stream input) {
            byte[] length=new byte[4];int first=input.ReadByte();if(first<0)return null;length[0]=(byte)first;ReadExact(input,length,1,3);
            int n=BitConverter.ToInt32(length,0);if(n<2||n>MaxHeader)throw new InvalidDataException("Invalid V4 input frame length.");
            byte[] bytes=new byte[n];ReadExact(input,bytes,0,n);return bytes;
        }
        static void ReadExact(Stream input,byte[] buffer,int offset,int count){while(count>0){int n=input.Read(buffer,offset,count);if(n==0)throw new EndOfStreamException();offset+=n;count-=n;}}
        static void WriteFrame(Stream output,string json){byte[] bytes=Encoding.UTF8.GetBytes(json);if(bytes.Length>MaxHeader)throw new InvalidDataException("Oversized V4 reply.");byte[] n=BitConverter.GetBytes(bytes.Length);output.Write(n,0,4);output.Write(bytes,0,bytes.Length);output.Flush();}
    }
}
