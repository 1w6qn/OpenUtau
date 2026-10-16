using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VocaloidBridge.V4 {
    /// <summary>Writes a temporary native VSQ4 phrase with exact seconds mapped to a fixed tempo.</summary>
    public static class V4ScoreWriter {
        public const double OriginSeconds=2.0;
        const int TicksPerSecond=960;
        static readonly XNamespace Ns="http://www.yamaha.co.jp/vocaloid/schema/vsq4/";
        static XElement E(string name,object value){return new XElement(Ns+name,value);}
        static XElement C(string name,string value){return E(name,new XCData(value));}
        static XElement V(string id,int value){return new XElement(Ns+"v",new XAttribute("id",id),value);}
        static XElement Parameters(){return new XElement(Ns+"vPrm",E("bre",0),E("bri",0),E("cle",0),E("gen",0),E("ope",0));}
        static XElement Style(string name){return new XElement(Ns+name,V("accent",0),V("bendDep",0),V("bendLen",0),V("decay",0),V("fallPort",0),V("opening",127),V("risePort",0));}
        static XElement Cc(int tick,string id,int value){return new XElement(Ns+"cc",E("t",tick),V(id,value));}
        public static void Write(string path,V4Request request,NativeVoiceInfo voice,NativeChineseG2p g2p,double tailMs) {
            request.Validate();
            int duration=checked((int)Math.Ceiling((request.Notes.Last().EndSeconds+Math.Max(0,Math.Min(tailMs,10000))/1000)*TicksPerSecond));
            var part=new XElement(Ns+"vsPart",E("t",(int)(OriginSeconds*TicksPerSecond)),E("playTime",duration),C("name","OpenUtau phrase"),C("comment",""),
                new XElement(Ns+"sPlug",C("id","ACA9C502-A04B-42b5-B2EB-5CEA36D16FCE"),C("name","VOCALOID2 Compatible Style"),C("version","3.0.0.1")),
                Style("pStyle"),new XElement(Ns+"singer",E("t",0),E("bs",voice.Bank),E("pc",voice.Program)));
            // Native VSQ4 controls use S=PBS and P=PIT. Sample by time, preserving
            // the caller's tempo-map conversion without native tempo rounding.
            part.Add(Cc(0,"S",24));
            int noteIndex=0;int priorValue=Int32.MinValue;
            for(int tick=0;tick<=duration;tick+=5) {
                double seconds=(double)tick/TicksPerSecond;
                while(noteIndex+1<request.Notes.Length&&request.Notes[noteIndex+1].StartSeconds<=seconds)noteIndex++;
                double delta=request.Pitch.Sample(seconds)-request.Notes[noteIndex].Tone;
                bool sounding=seconds>=request.Notes[noteIndex].StartSeconds&&seconds<request.Notes[noteIndex].EndSeconds;
                if(sounding&&Math.Abs(delta)>24.001)throw new InvalidDataException("V4 pitch bend exceeds its 24-semitone range.");
                int value=(int)Math.Round(Math.Max(-8192,Math.Min(8191,delta*8192/24)));
                if(value!=priorValue){part.Add(Cc(tick,"P",value));priorValue=value;}
            }
            int priorEnd=-1;
            foreach(var n in request.Notes) {
                int start=(int)Math.Round(n.StartSeconds*TicksPerSecond),end=(int)Math.Round(n.EndSeconds*TicksPerSecond);
                if(end<=start||start<priorEnd)throw new InvalidDataException("V4 notes must remain non-overlapping after 1/960-second quantization.");
                var style=Style("nStyle");style.Add(V("vibLen",0),V("vibType",0));
                part.Add(new XElement(Ns+"note",E("t",start),E("dur",end-start),E("n",n.Tone),E("v",64),C("y",n.Lyric),
                    new XElement(Ns+"p",new XAttribute("lock",1),new XCData(g2p.Phonemize(n.Lyric))),style));priorEnd=end;
            }
            part.Add(E("plane",0));
            XNamespace xsi="http://www.w3.org/2001/XMLSchema-instance";
            var root=new XElement(Ns+"vsq4",new XAttribute(XNamespace.Xmlns+"xsi",xsi),new XAttribute(xsi+"schemaLocation",Ns.NamespaceName+" vsq4.xsd"),
                C("vender","Yamaha corporation"),C("version","4.0.0.3"),
                new XElement(Ns+"vVoiceTable",new XElement(Ns+"vVoice",E("bs",voice.Bank),E("pc",voice.Program),C("id",voice.ComponentId),C("name",voice.Name),Parameters())),
                new XElement(Ns+"mixer",
                    new XElement(Ns+"masterUnit",E("oDev",0),E("rLvl",0),E("vol",0)),
                    new XElement(Ns+"vsUnit",E("tNo",0),E("iGin",0),E("sLvl",-898),E("sEnable",0),E("m",0),E("s",0),E("pan",64),E("vol",0)),
                    new XElement(Ns+"monoUnit",E("iGin",0),E("sLvl",-898),E("sEnable",0),E("m",0),E("s",0),E("pan",64),E("vol",0)),
                    new XElement(Ns+"stUnit",E("iGin",0),E("m",0),E("s",0),E("vol",0))),
                new XElement(Ns+"masterTrack",C("seqName","OpenUtau"),C("comment",""),E("resolution",480),E("preMeasure",1),
                    new XElement(Ns+"timeSig",E("m",0),E("nu",4),E("de",4)),new XElement(Ns+"tempo",E("t",0),E("v",12000))),
                new XElement(Ns+"vsTrack",E("tNo",0),C("name","VOCALOID"),C("comment",""),part),new XElement(Ns+"monoTrack"),new XElement(Ns+"stTrack"));
            using(var writer=XmlWriter.Create(path,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true}))new XDocument(new XDeclaration("1.0","UTF-8","no"),root).Save(writer);
        }
    }
}
