using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace VocaloidBridge.V5 {
    // Runs in an isolated x64 .NET Framework process. Keep stdout reserved for BridgeWire.
    public static class NativeHost {
        const int MaxHeader = 8 * 1024 * 1024;
        const int Rate = 44100;
        const double TicksPerSecond = 960.0;
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetDllDirectory(string path);

        public static int Main(string[] args) { try { if(args==null||args.Length!=1)throw new ArgumentException("Usage: V5NativeHost.exe <config.json>");Run(args[0]);return 0;} catch(Exception ex){Console.Error.WriteLine(ex.GetBaseException().Message);return 1;} }

        public static void Run(string configPath) {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("VOCALOID 5 host requires x64.");
            var json = new JavaScriptSerializer { MaxJsonLength=MaxHeader, RecursionLimit=64 };
            var config = (Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(configPath));
            string configDir=Path.GetDirectoryName(Path.GetFullPath(configPath));
            string root=Resolve(configDir, GetString(config,"EngineDirectory", ""));
            string editor=Path.Combine(root,"Editor");
            if (!File.Exists(Path.Combine(editor,"VOCALOID5.dll"))) editor=root;
            if (!File.Exists(Path.Combine(editor,"VOCALOID5.dll"))) throw new FileNotFoundException("VOCALOID5.dll was not found in EngineDirectory.",Path.Combine(editor,"VOCALOID5.dll"));
            string temp=Path.Combine(Path.GetTempPath(),"openutau-vocaloid-v5",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TEMP",temp);Environment.SetEnvironmentVariable("TMP",temp);
            Environment.CurrentDirectory=editor;
            Environment.SetEnvironmentVariable("PATH",editor+";"+root+";"+Environment.GetEnvironmentVariable("PATH"));
            if(!SetDllDirectory(editor))throw new InvalidOperationException("SetDllDirectory failed: "+Marshal.GetLastWin32Error());
            Console.SetOut(Console.Error);

            Assembly api=Assembly.LoadFrom(Path.Combine(editor,"VOCALOID5.dll"));
            object dbm=null,dse=null,manager=null;
            try {
                CreateManagers(api,out dbm,out dse,out manager);
                var voices=EnumerateVoices(dbm);
                Stream input=Console.OpenStandardInput(),output=Console.OpenStandardOutput();
                while(true) {
                    byte[] frame=ReadFrame(input);if(frame==null)return;
                    Dictionary<string,object> call=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(frame));
                    long id=GetLong(call,"Id",0);string operation=GetString(call,"Operation","");
                    var reply=new Dictionary<string,object>{{"Protocol",1},{"Id",id}};
                    float[] pcm=null;
                    try {
                        if(GetLong(call,"Protocol",0)!=1)throw new InvalidDataException("Unsupported bridge protocol.");
                        if(operation=="voices") reply["Voices"]=EnumerateVoices(dbm);
                        else if(operation=="import") {
                            string importDirectory=GetString(call,"ImportDirectory","");
                            if(String.IsNullOrWhiteSpace(importDirectory))throw new InvalidDataException("ImportDirectory is required for voicebank import.");
                            string selected=Path.GetFullPath(importDirectory);
                            var imported=ImportVoiceBanks(selected,dbm);
                            TryDispose(manager);TryDispose(dse);TryDispose(dbm);manager=null;dse=null;dbm=null;
                            CreateManagers(api,out dbm,out dse,out manager);
                            var refreshed=EnumerateVoices(dbm);
                            var refreshedById=refreshed.Cast<Dictionary<string,object>>().ToDictionary(v=>Convert.ToString(v["Id"]),StringComparer.OrdinalIgnoreCase);
                            var selectedVoices=new List<object>();
                            foreach(string component in imported){Dictionary<string,object> voice;if(refreshedById.TryGetValue("v5:"+component,out voice))selectedVoices.Add(voice);else throw new InvalidOperationException("V5 did not recognize imported component "+component+" after refreshing its database manager.");}
                            reply["Voices"]=selectedVoices.ToArray();reply["ImportedCount"]=selectedVoices.Count;
                        }
                        else if(operation=="render") {
                            var request=GetMap(call,"Request");if(request==null)throw new InvalidDataException("Missing V5 render request.");
                            var rendered=Render(api,manager,dbm,request,temp,GetDouble(config,"HeadMs",500),GetDouble(config,"TailMs",1000));
                            pcm=rendered.Samples;
                            if(pcm.Length<1||pcm.Length>16*1024*1024||!pcm.Any(x=>Math.Abs(x)>0.000001f))throw new InvalidDataException("VOCALOID 5 returned empty or silent PCM.");
                            reply["StartSeconds"]=rendered.StartSeconds;reply["SampleRate"]=rendered.SampleRate;reply["SampleCount"]=pcm.Length;
                        } else throw new InvalidDataException("Unknown bridge operation.");
                    } catch(Exception ex) {reply["Error"]=ex.GetBaseException().Message;WriteFrame(output,json.Serialize(reply));return;}
                    WriteFrame(output,json.Serialize(reply));
                    if(pcm!=null){byte[] data=new byte[checked(pcm.Length*4)];Buffer.BlockCopy(pcm,0,data,0,data.Length);output.Write(data,0,data.Length);output.Flush();}
                }
            } finally {
                TryDispose(manager);TryDispose(dse);TryDispose(dbm);
                try { if(Directory.Exists(temp)&&Directory.GetFileSystemEntries(temp).Length==0)Directory.Delete(temp); } catch { }
            }
        }

        sealed class Audio { public double StartSeconds;public int SampleRate;public float[] Samples; }
        static Audio Render(Assembly api,object manager,object dbm,Dictionary<string,object> request,string temp,double headMs,double tailMs) {
            string voiceId=GetString(request,"VoiceId","");if(!voiceId.StartsWith("v5:",StringComparison.Ordinal))throw new InvalidDataException("V5 voice IDs must use the v5:<component-id> form.");
            string compId=voiceId.Substring(3);object bank=Call(dbm,"GetVoiceBankByCompID",compId);if(bank==null)throw new InvalidDataException("Installed V5 voicebank not found: "+voiceId);
            ulong licenses=Convert.ToUInt64(Property(bank,"NumLicenses"));if(licenses==0)throw new InvalidOperationException("Voicebank "+voiceId+" is visible to VOCALOID 5 but has no V5 synthesis license (NumLicenses=0). Importing folder metadata does not migrate a license; register it through Yamaha's official V5 process before rendering.");
            if(Convert.ToInt32(Property(bank,"LangID"))!=4)throw new NotSupportedException("This V5 backend currently supports Simplified-Chinese voicebanks only.");
            var automations=GetMap(request,"Automations");var partProperties=GetMap(request,"PartProperties");
            if((automations!=null&&automations.Count!=0)||(partProperties!=null&&partProperties.Count!=0))throw new NotSupportedException("V5 direct rendering currently does not support part automations or part properties.");
            object notesObject=request.ContainsKey("Notes")?request["Notes"]:null;var notes=notesObject as ArrayList;
            if(notes==null||notes.Count<1||notes.Count>10000)throw new InvalidDataException("A V5 phrase requires 1..10000 notes.");
            object notePropsObject;var noteProps=request.TryGetValue("NoteProperties",out notePropsObject)?notePropsObject as ArrayList:null;
            if(noteProps!=null&&noteProps.Count!=0&&noteProps.Count!=notes.Count)throw new InvalidDataException("NoteProperties must be empty or have one entry per note.");
            if(noteProps!=null)foreach(object p in noteProps){var map=p as Dictionary<string,object>;if(map==null||map.Count!=0)throw new NotSupportedException("V5 direct rendering currently does not support per-note properties.");}
            double head=Math.Max(0,headMs)/1000.0,tail=Math.Max(0,tailMs)/1000.0;
            if(!Finite(head)||!Finite(tail)||head>10||tail>10)throw new InvalidDataException("V5 head/tail padding must be within 0..10000 ms.");
            double last=0;foreach(object item in notes){var n=item as Dictionary<string,object>;if(n==null)throw new InvalidDataException("Invalid V5 note object.");double start=GetDouble(n,"StartSeconds",-1),end=GetDouble(n,"EndSeconds",-1);int tone=(int)GetLong(n,"Tone",-1);string lyric=GetString(n,"Lyric","");if(!Finite(start)||!Finite(end)||start<0||start<last-1e-6||end<=start||end>300||tone<0||tone>127||String.IsNullOrWhiteSpace(lyric)||lyric.Length>1024)throw new InvalidDataException("V5 notes must be finite, ordered, monophonic and within 300 seconds.");last=end;}
            int totalTicks=checked((int)Math.Ceiling((head+last+tail)*TicksPerSecond));
            Type rateType=api.GetType("Yamaha.VOCALOID.VSM.VSMSamplingRate",true),dataType=api.GetType("Yamaha.VOCALOID.VSM.VSMSequenceData",true);
            object sequence=Call(manager,"CreateSequence",New(dataType,Enum.ToObject(rateType,Rate),(ulong)8,(ulong)32));
            if(sequence==null)throw new InvalidOperationException("Could not create V5 sequence.");
            string wav=Path.Combine(temp,Guid.NewGuid().ToString("N")+".wav");
            try {
                Type rel=api.GetType("Yamaha.VOCALOID.VSM.VSMRelTick",true),abs=api.GetType("Yamaha.VOCALOID.VSM.VSMAbsTick",true),trackEnum=api.GetType("Yamaha.VOCALOID.VSM.VSMTrackType",true);
                Call(sequence,"InsertTempo",New(rel,0),120);
                object track=Call(sequence,"InsertTrackEx",(ulong)0,Enum.ToObject(trackEnum,0),"OpenUtau V5");
                object midiTrack=New(api.GetType("Yamaha.VOCALOID.VSM.WIVSMMidiTrack",true),track);
                object part=Call(midiTrack,"InsertPart",New(abs,0),New(rel,totalTicks),"OpenUtau phrase");
                if(!Convert.ToBoolean(Call(part,"SetVoiceBankID",compId))||!Convert.ToBoolean(Call(part,"SetStyleName","")))throw new InvalidOperationException("Could not assign V5 voicebank or clear its default style.");
                object expression=Call(part,"GetDefaultNoteExpression");Type eventType=api.GetType("Yamaha.VOCALOID.VSM.VSMNoteEvent",true);
                var noteWrappers=new List<KeyValuePair<object,string>>();
                foreach(object item in notes){var n=(Dictionary<string,object>)item;double start=GetDouble(n,"StartSeconds",0),end=GetDouble(n,"EndSeconds",0);int tone=(int)GetLong(n,"Tone",60);string lyric=GetString(n,"Lyric","");
                    int startTick=ToTick(head+start),duration=ToTick(head+end)-startTick;
                    if(duration<1)throw new InvalidDataException("A V5 note became shorter than one engine tick after time quantization.");
                    object inserted=Call(part,"InsertNote",New(rel,startTick),New(eventType,duration,tone,100),expression,lyric,"a",true);
                    if(inserted==null)throw new InvalidDataException("V5 rejected seed note for lyric '"+lyric+"': "+Property(part,"LastError"));
                    PropertyInfo vibrato=inserted.GetType().GetProperty("VibratoType");if(vibrato==null)throw new MissingMemberException(inserted.GetType().FullName,"VibratoType");vibrato.SetValue(inserted,Enum.Parse(vibrato.PropertyType,"None"),null);
                    noteWrappers.Add(new KeyValuePair<object,string>(inserted,lyric));
                }
                if(!Convert.ToBoolean(Call(sequence,"Commit",true)))throw new InvalidOperationException("V5 seed-note commit failed.");
                object g2p=CreateG2pIfAvailable(Path.GetDirectoryName(api.Location));
                if(g2p==null)throw new NotSupportedException("The V5 Simplified-Chinese G2P helper is not available.");
                try { foreach(var pair in noteWrappers){string phones=Phonemize(g2p,pair.Key,pair.Value);if(String.IsNullOrWhiteSpace(phones))throw new InvalidDataException("V5 G2P returned empty phonemes for '"+pair.Value+"'.");} }
                finally {TryDispose(g2p);}
                SetPitch(api,part,request,notes,head,rel);
                if(!Convert.ToBoolean(Call(sequence,"Commit",true)))throw new InvalidOperationException("V5 G2P/curve commit failed.");
                Call(part,"NormalizeNotes");Call(part,"SetValidatedDvqm");
                object result=Call(part,"Render",Path.GetFullPath(wav));
                if(result==null||!String.Equals(result.ToString(),"NoError",StringComparison.Ordinal))throw new InvalidOperationException("VOCALOID 5 render failed: "+result+" ("+Property(part,"LastError")+").");
                Audio audio=ReadWav(wav);audio.StartSeconds=-head;return audio;
            } finally {try{Call(sequence,"Close");}catch{} if(File.Exists(wav))File.Delete(wav);}
        }

        static void SetPitch(Assembly api,object part,Dictionary<string,object> request,ArrayList notes,double head,Type relType) {
            Dictionary<string,object> curve=GetMap(request,"Pitch");if(curve==null)throw new InvalidDataException("An absolute MIDI pitch curve is required.");double[] times=GetDoubleArray(curve,"TimesSeconds"),values=GetDoubleArray(curve,"Values");if(times.Length==0||times.Length>200000||times.Length!=values.Length)throw new InvalidDataException("Invalid absolute pitch curve.");for(int i=0;i<times.Length;i++)if(!Finite(times[i])||!Finite(values[i])||times[i]<-10||times[i]>300||Math.Abs(values[i])>256||(i>0&&times[i]<=times[i-1]))throw new InvalidDataException("V5 pitch points must be finite, bounded and strictly increasing.");
            Type controller=api.GetType("Yamaha.VOCALOID.VSM.VSMControllerType",true);object sensType=Enum.ToObject(controller,2),pitchType=Enum.ToObject(controller,4);
            object sens=Call(part,"InsertController",New(relType,0),sensType,24);if(sens==null)throw new InvalidOperationException("Could not set V5 pitch-bend sensitivity: "+Property(part,"LastError"));
            var points=new SortedDictionary<int,double>();
            for(int i=0;i<times.Length;i++){int soundingTone;if(ToneAtOrNull(notes,times[i],out soundingTone)){int tick=checked((int)Math.Round((head+times[i])*TicksPerSecond));points[tick]=values[i];}}
            foreach(object raw in notes){var n=(Dictionary<string,object>)raw;double start=GetDouble(n,"StartSeconds",0),end=GetDouble(n,"EndSeconds",0);int a=ToTick(head+start),b=ToTick(head+end);points[a]=Sample(times,values,start);if(b>a+1)points[b-1]=Sample(times,values,Math.Max(start,(b-1)/TicksPerSecond-head));}
            foreach(var p in points){int tone;if(!ToneAtTick(notes,p.Key,head,out tone))continue;double excursion=p.Value-tone;if(Math.Abs(excursion)>24.000001)throw new NotSupportedException("V5 pitch bend exceeds the configured 24-semitone range.");double bend=excursion/24.0*8192.0;int v=(int)Math.Max(-8192,Math.Min(8191,Math.Round(bend)));object c=Call(part,"InsertController",New(relType,p.Key),pitchType,v);if(c==null)throw new InvalidOperationException("V5 rejected pitch-bend value at tick "+p.Key+": "+Property(part,"LastError"));}
        }
        static bool ToneAtOrNull(ArrayList notes,double time,out int tone){foreach(object raw in notes){var n=(Dictionary<string,object>)raw;if(time>=GetDouble(n,"StartSeconds",0)-1e-6&&time<GetDouble(n,"EndSeconds",0)-1e-6){tone=(int)GetLong(n,"Tone",60);return true;}}tone=0;return false;}
        static int ToTick(double seconds){return checked((int)Math.Round(seconds*TicksPerSecond));}
        static bool ToneAtTick(ArrayList notes,int tick,double head,out int tone){foreach(object raw in notes){var n=(Dictionary<string,object>)raw;if(tick>=ToTick(head+GetDouble(n,"StartSeconds",0))&&tick<ToTick(head+GetDouble(n,"EndSeconds",0))){tone=(int)GetLong(n,"Tone",60);return true;}}tone=0;return false;}
        static double Sample(double[] t,double[] v,double x){int i=Array.BinarySearch(t,x);if(i>=0)return v[i];i=~i;if(i==0)return v[0];if(i>=v.Length)return v[v.Length-1];double f=(x-t[i-1])/(t[i]-t[i-1]);return v[i-1]+f*(v[i]-v[i-1]);}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}

        static object CreateG2pIfAvailable(string editorPath){Type t=typeof(NativeHost).Assembly.GetType("VocaloidBridge.V5.NativeChineseG2p",false);if(t==null)return null;return Activator.CreateInstance(t,new object[]{editorPath});}
        static string Phonemize(object g2p,object note,string lyric){MethodInfo m=g2p.GetType().GetMethod("Phonemize");if(m==null)throw new MissingMethodException(g2p.GetType().FullName,"Phonemize(object,string)");object value=m.Invoke(g2p,new object[]{note,lyric});return value as string;}

        sealed class LegacyBank { public string Id,Name,Path,Drp,Date,BankName;public int Generation; }
        static void CreateManagers(Assembly api,out object dbm,out object dse,out object manager){
            dbm=null;dse=null;manager=null;
            Type dbmIf=api.GetType("Yamaha.VOCALOID.VDM.DatabaseManagerIF",true),dbmError=api.GetType("Yamaha.VOCALOID.VDM.VDMError",true);
            object[] args={"VOCALOID5",Enum.ToObject(dbmError,0)};dbm=dbmIf.GetMethod("CreateDatabaseManager",BindingFlags.Public|BindingFlags.Static).Invoke(null,args);
            if(dbm==null)throw new InvalidOperationException("VOCALOID 5 database manager could not be created: "+args[1]);
            Type dseIf=api.GetType("Yamaha.VOCALOID.DSE.DSEManagerIF",true);dse=dseIf.GetMethod("CreateManager",BindingFlags.Public|BindingFlags.Static).Invoke(null,new[]{dbm});
            if(dse==null)throw new InvalidOperationException("VOCALOID 5 synthesis manager could not be created.");
            Type vsmIf=api.GetType("Yamaha.VOCALOID.VSM.WVSMModuleIF",true);manager=vsmIf.GetMethod("CreateManager",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{"VOCALOID5"});
            if(manager==null)throw new InvalidOperationException("VOCALOID 5 VSM manager initialization failed: "+vsmIf.GetMethod("LastError",BindingFlags.Public|BindingFlags.Static).Invoke(null,null));
            Call(manager,"SetDatabaseManager",dbm);Call(manager,"SetDSEManager",dse);
        }

        static List<string> ImportVoiceBanks(string selected,object dbm){
            if(!Directory.Exists(selected))throw new DirectoryNotFoundException("Selected voicebank import folder does not exist: "+selected);
            var matches=new List<LegacyBank>();
            // Existing V5 banks are recognized from the VDM catalog and require no registry write.
            ulong count=Convert.ToUInt64(Property(dbm,"NumVoiceBanks"));if(count>1024)throw new InvalidDataException("Unreasonable V5 voicebank count.");
            for(ulong i=0;i<count;i++){object bank=Call(dbm,"GetVoiceBankByIndex",i);if(bank==null)continue;string path=Convert.ToString(Property(bank,"Path"));if(!MatchesSelectedDirectory(selected,path))continue;string id=Convert.ToString(Property(bank,"CompID"));if(!String.IsNullOrWhiteSpace(id))matches.Add(new LegacyBank{Id=id});}
            var legacy=ReadRegisteredLegacyBanks();
            foreach(var bank in legacy){if(!MatchesSelectedDirectory(selected,bank.Path,bank.Id))continue;if(matches.Any(x=>String.Equals(x.Id,bank.Id,StringComparison.OrdinalIgnoreCase)))continue;VerifyLegacyFiles(bank);RegisterV5Metadata(bank);matches.Add(bank);}
            if(matches.Count==0)throw new InvalidDataException("No supported installed voicebank matches the selected folder. Select a registered V3/V4 voicebank directory or a VOCALOID5 VoiceDB folder; unregistered loose files cannot be identified safely.");
            return matches.Select(x=>x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static List<LegacyBank> ReadRegisteredLegacyBanks(){
            var result=new List<LegacyBank>();
            using(RegistryKey hklm=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry32)){
                AddLegacyTree(result,hklm,"SOFTWARE\\VCLDASGN3\\DATABASE\\VOICE3",3);
                AddLegacyTree(result,hklm,"SOFTWARE\\VCLDASGN3\\DATABASE\\VOICE4",4);
                AddLegacyTree(result,hklm,"SOFTWARE\\VOCALOID4\\DATABASE",4);
                AddLegacyTree(result,hklm,"SOFTWARE\\POCALOID4\\DATABASE",4);
            }
            return result;
        }
        static void AddLegacyTree(List<LegacyBank> list,RegistryKey hklm,string tree,int generation){
            using(RegistryKey parent=hklm.OpenSubKey(tree,false)){if(parent==null)return;foreach(string id in parent.GetSubKeyNames()){if(String.IsNullOrWhiteSpace(id)||id.IndexOf('\\')>=0||id.IndexOf('/')>=0)continue;using(RegistryKey key=parent.OpenSubKey(id,false)){if(key==null)continue;object installed=key.GetValue("INSTALLED"),rawPath=key.GetValue("PATH"),name=key.GetValue("NAME"),drp=key.GetValue("DRP"),date=key.GetValue("TIME");if(installed==null||Convert.ToInt32(installed,CultureInfo.InvariantCulture)==0||rawPath==null||name==null||drp==null||date==null)continue;string path=Convert.ToString(rawPath,CultureInfo.InvariantCulture);if(!Path.IsPathRooted(path))continue;list.Add(new LegacyBank{Id=id,Generation=generation,Name=Convert.ToString(name,CultureInfo.InvariantCulture),Path=Path.GetFullPath(path),Drp=Convert.ToString(drp,CultureInfo.InvariantCulture),Date=Convert.ToString(date,CultureInfo.InvariantCulture)});}}}
        }
        static void VerifyLegacyFiles(LegacyBank bank){string component=Path.Combine(bank.Path,bank.Id);string basePath=Directory.Exists(component)?component:bank.Path;string[] ddbs=Directory.GetFiles(basePath,"*.ddb",SearchOption.TopDirectoryOnly);var pairs=new List<string>();foreach(string ddb in ddbs){string ddi=Path.ChangeExtension(ddb,".ddi");if(File.Exists(ddi))pairs.Add(ddb);}if(pairs.Count!=1)throw new InvalidDataException("Registered V"+bank.Generation+" bank "+bank.Id+" must have exactly one matching DDB/DDI pair in its registered component directory.");bank.BankName=Path.GetFileNameWithoutExtension(pairs[0]);}
        static void RegisterV5Metadata(LegacyBank bank){
            try { using(RegistryKey root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) using(RegistryKey components=root.CreateSubKey("SOFTWARE\\VOCALOID5\\Voice\\Components",true)) {
                if(components==null)throw new UnauthorizedAccessException("Could not open the VOCALOID5 component registry location.");
                using(RegistryKey existing=components.OpenSubKey(bank.Id,false)){if(existing!=null){EnsureSame(existing,"BankName",bank.BankName);EnsureSame(existing,"Name",bank.Name);EnsureSame(existing,"Path",bank.Path);EnsureSame(existing,"DRP",bank.Drp);EnsureSame(existing,"Date",bank.Date);using(RegistryKey ver=existing.OpenSubKey("Version",false)){if(ver!=null){object major=ver.GetValue("Major");if(major!=null&&Convert.ToInt32(major,CultureInfo.InvariantCulture)!=bank.Generation)throw new InvalidOperationException("A conflicting VOCALOID 5 component registration already exists for "+bank.Id+".");}}}}
                // Fill only missing ordinary metadata; preserve all unrelated values and subkeys.
                using(RegistryKey key=components.CreateSubKey(bank.Id,true)){if(key==null)throw new UnauthorizedAccessException("Could not create VOCALOID5 metadata for "+bank.Id+".");SetStringIfMissing(key,"BankName",bank.BankName);SetStringIfMissing(key,"DRP",bank.Drp);SetStringIfMissing(key,"Name",bank.Name);SetStringIfMissing(key,"Path",bank.Path);SetStringIfMissing(key,"Date",bank.Date);using(RegistryKey version=key.CreateSubKey("Version",true)){SetDwordIfMissing(version,"Major",bank.Generation);SetDwordIfMissing(version,"Minor",0);SetDwordIfMissing(version,"Revision",0);}}
            }} catch(UnauthorizedAccessException ex){throw new UnauthorizedAccessException("Windows denied writing ordinary VOCALOID5 voicebank metadata. Run the import from an account permitted to register voicebanks, then retry. License or activation data was not changed.",ex);} catch(System.Security.SecurityException ex){throw new UnauthorizedAccessException("Windows security policy denied writing ordinary VOCALOID5 voicebank metadata. License or activation data was not changed.",ex);}
        }
        static void EnsureSame(RegistryKey key,string value,string expected){object actual=key.GetValue(value);if(actual!=null&&!String.Equals(Convert.ToString(actual,CultureInfo.InvariantCulture),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("A conflicting VOCALOID5 component registration already exists ("+value+") for "+key.Name+".");}
        static void SetStringIfMissing(RegistryKey key,string name,string value){if(key.GetValue(name)==null)key.SetValue(name,value,RegistryValueKind.String);}
        static void SetDwordIfMissing(RegistryKey key,string name,int value){if(key.GetValue(name)==null)key.SetValue(name,value,RegistryValueKind.DWord);}
        static bool MatchesSelectedDirectory(string selected,string bankPath){return MatchesSelectedDirectory(selected,bankPath,null);}
        static bool MatchesSelectedDirectory(string selected,string bankPath,string componentId){if(String.IsNullOrWhiteSpace(bankPath))return false;string s=NormalizeDirectory(selected),p=NormalizeDirectory(bankPath);if(String.Equals(s,p,StringComparison.OrdinalIgnoreCase)||IsAncestor(s,p))return true;if(String.Equals(Path.GetFileName(s),"VoiceDB",StringComparison.OrdinalIgnoreCase)&&IsAncestor(s,p))return true;if(componentId!=null&&String.Equals(s,NormalizeDirectory(Path.Combine(p,componentId)),StringComparison.OrdinalIgnoreCase))return true;return false;}
        static string NormalizeDirectory(string p){return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);}
        static bool IsAncestor(string parent,string child){string prefix=parent+Path.DirectorySeparatorChar;return child.StartsWith(prefix,StringComparison.OrdinalIgnoreCase);}

        static Audio ReadWav(string path){using(var s=File.OpenRead(path)){var r=new BinaryReader(s);if(new string(r.ReadChars(4))!="RIFF")throw new InvalidDataException("V5 output is not RIFF/WAVE.");r.ReadUInt32();if(new string(r.ReadChars(4))!="WAVE")throw new InvalidDataException("V5 output is not WAVE.");ushort format=0,channels=0,bits=0;int rate=0;byte[] data=null;while(s.Position+8<=s.Length){string id=new string(r.ReadChars(4));uint size=r.ReadUInt32();if(size>s.Length-s.Position)throw new InvalidDataException("Truncated V5 WAV chunk.");if(id=="data"&&size>128u*1024u*1024u)throw new InvalidDataException("V5 WAV data exceeds the 16M-frame safety limit.");long end=s.Position+size;if(id=="fmt "){format=r.ReadUInt16();channels=r.ReadUInt16();rate=r.ReadInt32();r.ReadInt32();r.ReadUInt16();bits=r.ReadUInt16();}else if(id=="data"){data=r.ReadBytes(checked((int)size));}s.Position=end+(size&1);}if(data==null||channels<1||channels>2||rate<8000||rate>192000||(!(format==1&&bits==16)&&!(format==3&&bits==32)))throw new InvalidDataException("Unsupported V5 WAV format.");int bytes=bits/8,frameBytes=bytes*channels;if(data.Length%frameBytes!=0)throw new InvalidDataException("V5 WAV data is not frame aligned.");int frames=data.Length/frameBytes;if(frames<1||frames>16*1024*1024)throw new InvalidDataException("V5 WAV sample count exceeds the safety limit.");float[] samples=new float[frames];for(int i=0;i<frames;i++){double sum=0;for(int c=0;c<channels;c++){int o=(i*channels+c)*bytes;double x=format==1?BitConverter.ToInt16(data,o)/32768.0:BitConverter.ToSingle(data,o);if(Double.IsNaN(x)||Double.IsInfinity(x))throw new InvalidDataException("V5 WAV contains non-finite PCM.");sum+=x;}samples[i]=(float)(sum/channels);}return new Audio{StartSeconds=0,SampleRate=rate,Samples=samples};}}
        static object[] EnumerateVoices(object dbm){ulong n=Convert.ToUInt64(Property(dbm,"NumVoiceBanks"));if(n>1024)throw new InvalidDataException("Unreasonable V5 voicebank count.");var result=new List<object>();for(ulong i=0;i<n;i++){object bank=Call(dbm,"GetVoiceBankByIndex",i);if(bank==null)continue;string id=Convert.ToString(Property(bank,"CompID"));if(String.IsNullOrWhiteSpace(id))continue;int lang=Convert.ToInt32(Property(bank,"LangID"));int generation=VoiceGeneration(bank);ulong licenses=Convert.ToUInt64(Property(bank,"NumLicenses"));result.Add(new Dictionary<string,object>{{"Id","v5:"+id},{"Name",Convert.ToString(Property(bank,"Name"))},{"Language",lang==4?"zh-CN":""},{"Generation",generation},{"ComponentId",id},{"DatabasePath",Convert.ToString(Property(bank,"Path"))},{"LicenseCount",licenses},{"Ready",licenses>0}});}return result.ToArray();}
        static int VoiceGeneration(object bank){MethodInfo version=bank.GetType().GetMethod("Version",new[]{typeof(int).MakeByRefType(),typeof(int).MakeByRefType(),typeof(int).MakeByRefType()});if(version==null)return 0;object[] values={0,0,0};try{if(!Convert.ToBoolean(version.Invoke(bank,values)))return 0;int generation=Convert.ToInt32(values[0]);return generation>=3&&generation<=6?generation:0;}catch{return 0;}}
        static object Call(object target,string name,params object[] args){foreach(MethodInfo m in target.GetType().GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static).Where(x=>x.Name==name&&x.GetParameters().Length==args.Length)){try{return m.Invoke(target,args);}catch(ArgumentException){}}throw new MissingMethodException(target.GetType().FullName,name+"/"+args.Length);}
        static object New(Type type,params object[] args){return Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,args,CultureInfo.InvariantCulture);}
        static object Property(object o,string name){return o.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.Instance).GetValue(o,null);}
        static void TryDispose(object o){if(o==null)return;try{IDisposable d=o as IDisposable;if(d!=null)d.Dispose();else{MethodInfo m=o.GetType().GetMethod("Dispose",Type.EmptyTypes);if(m!=null)m.Invoke(o,null);}}catch{}}
        static Dictionary<string,object> GetMap(Dictionary<string,object> m,string key){return m.ContainsKey(key)?m[key] as Dictionary<string,object>:null;}
        static string GetString(Dictionary<string,object> m,string k,string d){object v;return m.TryGetValue(k,out v)&&v!=null?Convert.ToString(v,CultureInfo.InvariantCulture):d;}
        static long GetLong(Dictionary<string,object> m,string k,long d){object v;return m.TryGetValue(k,out v)&&v!=null?Convert.ToInt64(v,CultureInfo.InvariantCulture):d;}
        static double GetDouble(Dictionary<string,object> m,string k,double d){object v;return m.TryGetValue(k,out v)&&v!=null?Convert.ToDouble(v,CultureInfo.InvariantCulture):d;}
        static double[] GetDoubleArray(Dictionary<string,object> m,string key){object x;var a=m.TryGetValue(key,out x)?x as ArrayList:null;if(a==null)return new double[0];return a.Cast<object>().Select(v=>Convert.ToDouble(v,CultureInfo.InvariantCulture)).ToArray();}
        static string Resolve(string basePath,string path){return Path.GetFullPath(Path.IsPathRooted(path)?path:Path.Combine(basePath,path));}
        static byte[] ReadFrame(Stream s){byte[] n=new byte[4];int b=s.ReadByte();if(b<0)return null;n[0]=(byte)b;ReadExact(s,n,1,3);int count=BitConverter.ToInt32(n,0);if(count<2||count>MaxHeader)throw new InvalidDataException("Invalid input frame length.");byte[] data=new byte[count];ReadExact(s,data,0,count);return data;}
        static void ReadExact(Stream s,byte[] b,int o,int n){while(n>0){int k=s.Read(b,o,n);if(k==0)throw new EndOfStreamException();o+=k;n-=k;}}
        static void WriteFrame(Stream s,string json){byte[] b=Encoding.UTF8.GetBytes(json);if(b.Length>MaxHeader)throw new InvalidDataException("Oversized reply header.");byte[] n=BitConverter.GetBytes(b.Length);s.Write(n,0,4);s.Write(b,0,b.Length);s.Flush();}
    }
}
