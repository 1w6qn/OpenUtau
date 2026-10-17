using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
        public string VoiceId { get; set; }
        public string EngineDirectory { get; set; }
        public double TailMs { get; set; }
        public double TimeoutSeconds { get; set; }
    }
}
