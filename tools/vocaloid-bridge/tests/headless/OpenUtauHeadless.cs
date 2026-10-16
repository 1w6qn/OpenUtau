using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

// Small isolated launcher for integration tests. It loads the project's USTX,
// waits for phonemization, renders through OpenUtau's real phrase renderer, and
// writes float WAVs without importing application preferences or user data.
public static class OpenUtauBridgeHeadless {
    static Assembly core;
    static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
    static Type T(string name) { return core.GetType(name, true); }
    static object Singleton(string name, string member = "Inst") {
        Type type = T(name);
        var property = type.GetProperty(member, All);
        return property != null ? property.GetValue(null) : type.GetField(member, All).GetValue(null);
    }
    static object Read(object obj, string member) {
        Type type = obj.GetType();
        var property = type.GetProperty(member, All);
        return property != null ? property.GetValue(obj) : type.GetField(member, All).GetValue(obj);
    }
    static void Set(object obj, string member, object value) {
        Type type = obj.GetType();
        var property = type.GetProperty(member, All);
        if (property != null) property.SetValue(obj, value); else type.GetField(member, All).SetValue(obj, value);
    }

    sealed class PumpScheduler : TaskScheduler {
        readonly ConcurrentQueue<Task> queue = new ConcurrentQueue<Task>();
        readonly int owner = Environment.CurrentManagedThreadId;
        protected override IEnumerable<Task> GetScheduledTasks() { return queue.ToArray(); }
        protected override void QueueTask(Task task) { queue.Enqueue(task); }
        protected override bool TryExecuteTaskInline(Task task, bool previouslyQueued) {
            return owner == Environment.CurrentManagedThreadId && TryExecuteTask(task);
        }
        public void Post(Action action) { Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, this); }
        public void Pump() {
            while (queue.TryDequeue(out Task task)) TryExecuteTask(task);
            Thread.Sleep(5);
        }
    }

    static void WriteFloatWav(string path, float[] samples) {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        using (var stream = new BinaryWriter(File.Create(path))) {
            int bytes = checked(samples.Length * sizeof(float));
            stream.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); stream.Write(bytes + 36);
            stream.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); stream.Write(16);
            stream.Write((short)3); stream.Write((short)1); stream.Write(44100); stream.Write(44100 * 4);
            stream.Write((short)4); stream.Write((short)32);
            stream.Write(System.Text.Encoding.ASCII.GetBytes("data")); stream.Write(bytes);
            foreach (float sample in samples) stream.Write(sample);
        }
    }

    public static void Run(Assembly assembly, string appPath, string dataPath, string singerPath,
            string projectPath, string outputPath, bool render) {
        core = assembly;
        var flags = All;
        object paths = Singleton("OpenUtau.Core.PathManager");
        Type pathType = paths.GetType();
        foreach (var setting in new Dictionary<string, string> {
            { "RootPath", appPath }, { "DataPath", dataPath }, { "CachePath", Path.Combine(dataPath, "Cache") }
        }) {
            pathType.GetProperty(setting.Key, flags).SetValue(paths, setting.Value);
        }
        Directory.CreateDirectory(Path.Combine(dataPath, "Cache"));

        var scheduler = new PumpScheduler();
        object prefs = Singleton("OpenUtau.Core.Util.Preferences", "Default");
        Set(prefs, "AdditionalSingerPath", singerPath);
        Set(prefs, "PreRender", false);

        object doc = Singleton("OpenUtau.Core.DocManager");
        Set(doc, "PostOnUIThread", new Action<Action>(action => scheduler.Post(action)));
        Set(doc, "RequestFrame", new Action<Action>(action => scheduler.Post(action)));
        T("OpenUtau.Core.DocManager").GetMethod("Initialize", flags).Invoke(doc, new object[] { Thread.CurrentThread, scheduler });
        object singers = Singleton("OpenUtau.Core.SingerManager");
        singers.GetType().GetMethod("Initialize", flags).Invoke(singers, null);

        object project = T("OpenUtau.Core.Format.Ustx").GetMethod("Load", flags).Invoke(null, new object[] { projectPath });
        doc.GetType().GetMethod("TakeProjectForTest", flags).Invoke(doc, new object[] { project });
        project.GetType().GetMethod("ValidateFull", flags).Invoke(project, null);
        var parts = new List<object>();
        foreach (object part in (IEnumerable)Read(project, "parts")) {
            if (part.GetType().Name == "UVoicePart") parts.Add(part);
        }

        var started = DateTime.UtcNow;
        while (true) {
            scheduler.Pump();
            bool ready = true;
            foreach (object part in parts) {
                ready &= (bool)Read(part, "PhonemesUpToDate") && ((IList)Read(part, "renderPhrases")).Count > 0;
            }
            if (ready) break;
            if ((DateTime.UtcNow - started).TotalSeconds > 180) throw new TimeoutException("Timed out preparing OpenUtau phrases.");
        }

        int phoneCount = 0, phraseCount = 0;
        foreach (object part in parts) {
            phoneCount += ((IList)Read(part, "phonemes")).Count;
            phraseCount += ((IList)Read(part, "renderPhrases")).Count;
        }
        Console.WriteLine("READY parts=" + parts.Count + " phonemes=" + phoneCount + " phrases=" + phraseCount);

        string phraseDump = Environment.GetEnvironmentVariable("SVS_DUMP_PHRASES");
        if (!string.IsNullOrWhiteSpace(phraseDump)) {
            var rows = new List<object>();
            int partNumber = 0;
            foreach (object part in parts) {
                var notes = new List<object>();
                foreach (object note in (IEnumerable)Read(part, "notes")) notes.Add(note);
                foreach (object phrase in (IEnumerable)Read(part, "renderPhrases")) {
                    var phones = new List<object>();
                    foreach (object phone in (IEnumerable)Read(phrase, "phones")) phones.Add(new {
                        phoneme = Read(phone, "phoneme"), start_ms = Read(phone, "positionMs"),
                        end_ms = Read(phone, "endMs"), relative_ticks = Read(phone, "position")
                    });
                    rows.Add(new { part = partNumber, track_no = Read(part, "trackNo"), part_position = Read(part, "position"),
                        position = Read(phrase, "position"), leading = Read(phrase, "leading"), end = Read(phrase, "end"),
                        baseline = Read(phrase, "pitchesBeforeDeviation"), pitches = Read(phrase, "pitches"), phones = phones });
                }
                partNumber++;
            }
            string fullDumpPath = Path.GetFullPath(phraseDump);
            Directory.CreateDirectory(Path.GetDirectoryName(fullDumpPath));
            File.WriteAllText(fullDumpPath, JsonSerializer.Serialize(rows));
        }

        if (!render) return;
        var progress = Activator.CreateInstance(T("OpenUtau.Core.Render.Progress"), new object[] { phoneCount });
        var cancellation = new CancellationTokenSource();
        var rendered = new List<Tuple<int, float[]>>();
        var stems = new SortedDictionary<int, List<Tuple<int, float[]>>>();
        int completed = 0;
        foreach (object part in parts) foreach (object phrase in (IEnumerable)Read(part, "renderPhrases")) {
            object renderer = Read(phrase, "renderer");
            Task task = (Task)renderer.GetType().GetMethod("Render", flags).Invoke(renderer,
                new object[] { phrase, progress, Read(part, "trackNo"), cancellation, false, null });
            while (!task.IsCompleted) scheduler.Pump();
            task.GetAwaiter().GetResult();
            object result = task.GetType().GetProperty("Result").GetValue(task);
            float[] samples = (float[])Read(result, "samples");
            if (samples == null || samples.Length == 0) throw new Exception("Renderer returned no samples.");
            double offsetMs = (double)Read(result, "positionMs") - (double)Read(result, "leadingMs");
            int position = (int)Math.Round(offsetMs * 44.1);
            rendered.Add(Tuple.Create(position, samples));
            int trackNumber = (int)Read(part, "trackNo");
            if (!stems.ContainsKey(trackNumber)) stems[trackNumber] = new List<Tuple<int, float[]>>();
            stems[trackNumber].Add(Tuple.Create(position, samples));
            Console.WriteLine("RENDER " + (++completed) + "/" + phraseCount + " at=" + offsetMs.ToString("F1") + "ms samples=" + samples.Length);
        }

        int length = 0;
        foreach (var item in rendered) length = Math.Max(length, item.Item1 + item.Item2.Length);
        float[] full = new float[length];
        foreach (var item in rendered) AddAt(full, item.Item1, item.Item2);
        WriteFloatWav(outputPath, full);

        string stemDirectory = Environment.GetEnvironmentVariable("SVS_STEM_DIR");
        if (!string.IsNullOrWhiteSpace(stemDirectory)) {
            Directory.CreateDirectory(stemDirectory);
            foreach (var track in stems) {
                float[] stem = new float[length];
                foreach (var item in track.Value) AddAt(stem, item.Item1, item.Item2);
                WriteFloatWav(Path.Combine(stemDirectory, "track-" + track.Key.ToString("D2") + ".wav"), stem);
            }
        }
        Console.WriteLine("SAVED " + Path.GetFullPath(outputPath) + " duration=" + (length / 44100.0).ToString("F2"));
    }

    static void AddAt(float[] destination, int offset, float[] samples) {
        int sourceStart = Math.Max(0, -offset);
        int destinationStart = Math.Max(0, offset);
        int count = Math.Min(samples.Length - sourceStart, destination.Length - destinationStart);
        for (int i = 0; i < count; i++) destination[destinationStart + i] += samples[sourceStart + i];
    }
}
