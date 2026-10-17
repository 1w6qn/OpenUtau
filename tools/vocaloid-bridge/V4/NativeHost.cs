using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace VocaloidBridge.V4 {
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
                        string statsPath=null;
                        bool paceRealtime=false;
                        RenderStats stats=null;
                        System.Diagnostics.Stopwatch wallWatch=null;
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
                                        // Real-time budget: the engine renders one 256-sample
                                        // frame at a time, so per-frame cost decides whether the
                                        // same audio can be streamed instead of batch-rendered.
                                        statsPath=Environment.GetEnvironmentVariable("SVS_STREAM_STATS");
                                        paceRealtime=Environment.GetEnvironmentVariable("SVS_STREAM_PACE")=="1";
                                        stats=null;
                                        if(statsPath!=null||paceRealtime){stats=new RenderStats();stats.BudgetMs=1000.0*256/44100.0;}
                                        wallWatch=System.Diagnostics.Stopwatch.StartNew();
                                        audio=NativeSynthesis.Render(api,engine.Handle,phrase.DaisyBuffer,null,stats,paceRealtime);
                                        wallWatch.Stop();
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
                                if(stats!=null){
                                    var payload=new Dictionary<string,object>{{"frames",stats.Frames},{"budget_ms",stats.BudgetMs},
                                        {"total_frame_ms",stats.TotalMs},{"p50_ms",stats.Percentile(0.5)},
                                        {"p99_ms",stats.Percentile(0.99)},{"max_ms",stats.MaxMs},
                                        {"late_frames",stats.LateFrames},{"realtime_factor",stats.RealtimeFactor},
                                        {"wall_seconds",wallWatch.Elapsed.TotalSeconds},{"paced",paceRealtime}};
                                    reply["FrameStats"]=payload;
                                    statsPath=Environment.GetEnvironmentVariable("SVS_STREAM_STATS");
                                    if(statsPath!=null)File.WriteAllText(statsPath,json.Serialize(payload));
                                }
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