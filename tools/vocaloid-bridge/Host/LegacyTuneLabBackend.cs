using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VocaloidBridge {

/// <summary>
/// Loads a user-installed TuneLab legacy voice-engine assembly and adapts its
/// interfaces without taking a compile-time dependency on the legacy SDK.
/// </summary>
public sealed class LegacyTuneLabBackend : IDisposable {
    readonly AssemblyLoadContext loadContext;
    readonly object engine;
    readonly Type engineContract;
    readonly Dictionary<string, object> voiceSources = new(StringComparer.Ordinal);
    bool disposed;

    public LegacyTuneLabBackend(string assemblyPath, string engineType, string engineDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineDirectory);
        assemblyPath = Path.GetFullPath(assemblyPath);
        engineDirectory = Path.GetFullPath(engineDirectory);
        if (!File.Exists(assemblyPath)) throw new FileNotFoundException("Legacy voice extension was not found.", assemblyPath);

        // DispatchProxy-generated proxy assemblies are non-collectible and cannot
        // reference interfaces from a collectible AssemblyLoadContext. The host
        // owns process lifetime and terminates this backend process when needed.
        loadContext = new AssemblyLoadContext("VocaloidBridge.Legacy." + Guid.NewGuid().ToString("N"), isCollectible: false);
        loadContext.Resolving += (_, name) => {
            var candidate = Path.Combine(Path.GetDirectoryName(assemblyPath)!, name.Name + ".dll");
            if (!File.Exists(candidate)) candidate = Path.Combine(engineDirectory, name.Name + ".dll");
            return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
        };
        var engineAssemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
        loadContext.ResolvingUnmanagedDll += (_, name) => ResolveNativeLibrary(name, engineAssemblyDirectory, engineDirectory);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var engineContractCandidate = assembly.GetTypes()
            .SelectMany(t => t.GetInterfaces())
            .FirstOrDefault(t => t.FullName == "TuneLab.Extensions.Voices.IVoiceEngine");
        if (engineContractCandidate is null)
            throw new InvalidOperationException("The assembly does not reference the TuneLab legacy IVoiceEngine contract.");
        var implementation = string.IsNullOrWhiteSpace(engineType)
            ? assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface && engineContractCandidate.IsAssignableFrom(t)).ToArray() switch {
                { Length: 1 } matches => matches[0],
                { Length: 0 } => throw new InvalidOperationException("No concrete IVoiceEngine implementation was found in the extension assembly."),
                _ => throw new InvalidOperationException("More than one IVoiceEngine implementation was found; set EngineType explicitly."),
            }
            : assembly.GetType(engineType, throwOnError: true)!;
        engineContract = engineContractCandidate;
        if (!engineContract.IsAssignableFrom(implementation))
            throw new InvalidOperationException($"{engineType} does not implement the legacy IVoiceEngine contract.");
        engine = Activator.CreateInstance(implementation)
            ?? throw new InvalidOperationException($"Could not construct legacy voice engine {engineType}.");
        var init = engineContract.GetMethod("Init")!;
        object?[] initArgs = [engineDirectory, null];
        if (!(bool)init.Invoke(engine, initArgs)!) {
            var reason = initArgs[1]?.ToString();
            DestroyEngine();
            throw new InvalidOperationException("Legacy voice engine initialization failed." + (string.IsNullOrWhiteSpace(reason) ? "" : " " + reason));
        }
    }

    public IReadOnlyList<BridgeVoice> ListVoices() {
        ThrowIfDisposed();
        var mapProperty = engineContract.GetProperty("VoiceInfos")!;
        var map = mapProperty.GetValue(engine)
            ?? throw new InvalidOperationException("Legacy voice engine returned a null VoiceInfos map.");
        var mapContract = mapProperty.PropertyType;
        var countProperty = mapContract.GetProperty("Count") ?? mapContract.GetInterfaces()
            .Select(type => type.GetProperty("Count"))
            .FirstOrDefault(property => property is not null)
            ?? throw new InvalidOperationException("Legacy VoiceInfos map does not expose IReadOnlyCollection.Count.");
        var count = Convert.ToInt32(countProperty.GetValue(map));
        var keyAt = mapContract.GetMethod("KeyAt")!;
        var valueAt = mapContract.GetMethod("ValueAt")!;
        var result = new List<BridgeVoice>(count);
        for (var i = 0; i < count; i++) {
            var id = (string)keyAt.Invoke(map, [i])!;
            var info = valueAt.Invoke(map, [i])!;
            var name = ReadMember(info, "Name")?.ToString() ?? id;
            result.Add(new BridgeVoice { Id = id, Name = name });
        }
        return result;
    }

    public async Task<BridgeAudio> RenderAsync(BridgeRequest request, CancellationToken cancellation) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        cancellation.ThrowIfCancellationRequested();
        request.Validate();
        var source = GetVoiceSource(request.VoiceId);
        var sourceContract = source.GetType().GetInterfaces().Single(i => i.FullName == "TuneLab.Extensions.Voices.IVoiceSource");
        var dataContract = engineContract.Assembly.GetType("TuneLab.Extensions.Voices.ISynthesisData", true)!;
        var data = CreateProxy(dataContract, new SynthesisDataAdapter(request, dataContract.Assembly));
        var task = sourceContract.GetMethod("CreateSynthesisTask")!.Invoke(source, [data])
            ?? throw new InvalidOperationException("Legacy voice source returned a null synthesis task.");
        var taskContract = sourceContract.Assembly.GetType("TuneLab.Extensions.Voices.ISynthesisTask", true)!;
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<object?> onComplete = value => completion.TrySetResult(value);
        Action<object?> onError = value => completion.TrySetException(new InvalidOperationException("Legacy synthesis failed: " + value));
        Action<object?> onProgress = _ => { };
        var eventBindings = new List<(EventInfo Event, Delegate Handler)>();
        try {
            Subscribe(task, taskContract, "Complete", onComplete, eventBindings);
            Subscribe(task, taskContract, "Error", onError, eventBindings);
            Subscribe(task, taskContract, "Progress", onProgress, eventBindings);
            using var registration = cancellation.Register(() => {
                try { taskContract.GetMethod("Stop")!.Invoke(task, null); } catch { /* Stop is a best-effort cancellation signal. */ }
                completion.TrySetCanceled(cancellation);
            });
            cancellation.ThrowIfCancellationRequested();
            taskContract.GetMethod("Start")!.Invoke(task, null);
            var result = await completion.Task.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return ConvertAudio(result!);
        } finally {
            foreach (var (evt, handler) in eventBindings) {
                try { evt.RemoveEventHandler(task, handler); } catch { /* Some third-party tasks throw during teardown. */ }
            }
        }
    }

    BridgeAudio ConvertAudio(object result) {
        var start = Convert.ToDouble(ReadMember(result, "StartTime"), System.Globalization.CultureInfo.InvariantCulture);
        var rate = Convert.ToInt32(ReadMember(result, "SamplingRate"), System.Globalization.CultureInfo.InvariantCulture);
        var samples = ReadMember(result, "AudioData") as float[]
            ?? throw new InvalidOperationException("Legacy synthesis result did not contain float audio samples.");
        if (!double.IsFinite(start) || rate <= 0 || samples.Length == 0 || samples.Any(s => !float.IsFinite(s)))
            throw new InvalidOperationException("Legacy synthesis returned invalid alignment, sample rate, or audio samples.");
        var audio = new BridgeAudio { StartSeconds = start, SampleRate = rate, Samples = samples.ToArray() };
        audio.Validate();
        return audio;
    }

    object GetVoiceSource(string id) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Voice ID is required.", nameof(id));
        if (voiceSources.TryGetValue(id, out var cached)) return cached;
        var voiceMap = engineContract.GetProperty("VoiceInfos")!.GetValue(engine)!;
        var voiceMapContract = engineContract.GetProperty("VoiceInfos")!.PropertyType;
        var containsKey = voiceMapContract.GetMethod("ContainsKey") ?? voiceMapContract.GetInterfaces()
            .SelectMany(type => type.GetMethods())
            .FirstOrDefault(method => method.Name == "ContainsKey" && method.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("Legacy VoiceInfos map does not expose IReadOnlyMap.ContainsKey.");
        if (!ListVoices().Any(voice => string.Equals(voice.Id, id, StringComparison.Ordinal)) ||
            !(bool)containsKey.Invoke(voiceMap, [id])!) {
            throw new InvalidOperationException($"Installed voice ID '{id}' was not found (IDs are matched exactly).");
        }
        var source = engineContract.GetMethod("CreateVoiceSource")!.Invoke(engine, [id])
            ?? throw new InvalidOperationException($"Legacy engine could not create voice source '{id}'.");
        voiceSources.Add(id, source);
        return source;
    }

    static object CreateProxy(Type contract, ContractAdapter adapter) {
        var proxy = DispatchProxy.Create(contract, typeof(LegacyDispatchProxy));
        ((LegacyDispatchProxy)proxy).Adapter = adapter;
        return proxy;
    }

    static void Subscribe(object task, Type contract, string eventName, Action<object?> callback, List<(EventInfo, Delegate)> bindings) {
        var evt = contract.GetEvent(eventName)!;
        var invoke = evt.EventHandlerType!.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var boxed = Expression.Convert(parameters[0], typeof(object));
        var call = Expression.Call(Expression.Constant(callback), typeof(Action<object?>).GetMethod("Invoke")!, boxed);
        var handler = Expression.Lambda(evt.EventHandlerType, call, parameters).Compile();
        evt.AddEventHandler(task, handler);
        bindings.Add((evt, handler));
    }

    static object? ReadMember(object instance, string name) {
        var type = instance.GetType();
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
            ?? type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
    }

    static IntPtr ResolveNativeLibrary(string name, string assemblyDirectory, string engineDirectory) {
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        if (OperatingSystem.IsWindows()) {
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) fileNames.Add(name + ".dll");
        } else if (OperatingSystem.IsLinux()) {
            if (!name.StartsWith("lib", StringComparison.Ordinal)) fileNames.Add("lib" + name);
            foreach (var candidate in fileNames.ToArray()) {
                if (!candidate.EndsWith(".so", StringComparison.OrdinalIgnoreCase)) fileNames.Add(candidate + ".so");
            }
        } else if (OperatingSystem.IsMacOS()) {
            if (!name.StartsWith("lib", StringComparison.Ordinal)) fileNames.Add("lib" + name);
            foreach (var candidate in fileNames.ToArray()) {
                if (!candidate.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)) fileNames.Add(candidate + ".dylib");
            }
        }
        var rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx") + "-" +
            (RuntimeInformation.ProcessArchitecture switch {
                Architecture.X64 => "x64", Architecture.X86 => "x86", Architecture.Arm64 => "arm64", Architecture.Arm => "arm", _ => "unknown",
            });
        foreach (var root in new[] { assemblyDirectory, engineDirectory }.Distinct(StringComparer.OrdinalIgnoreCase)) {
            foreach (var relativeRoot in new[] { "", Path.Combine("runtimes", rid, "native") }) {
                foreach (var fileName in fileNames) {
                    var candidate = Path.Combine(root, relativeRoot, fileName);
                    if (!File.Exists(candidate)) continue;
                    try { return NativeLibrary.Load(candidate); } catch { /* Keep searching the engine's declared layout. */ }
                }
            }
        }
        return IntPtr.Zero;
    }

    void DestroyEngine() {
        try { engineContract.GetMethod("Destroy")?.Invoke(engine, null); } catch { }
    }

    void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(disposed, this); }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        voiceSources.Clear();
        DestroyEngine();
    }

    public abstract class ContractAdapter {
        public abstract object? Invoke(MethodInfo method, object?[]? args);
    }

    public class LegacyDispatchProxy : DispatchProxy {
        public ContractAdapter Adapter { private get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Adapter.Invoke(targetMethod!, args);
    }

    sealed class SynthesisDataAdapter : ContractAdapter {
        readonly BridgeRequest request;
        readonly Assembly sdk;
        readonly Array notes;
        readonly Type noteContract;
        readonly Type automationContract;
        readonly object pitch;
        readonly object partProperties;

        public SynthesisDataAdapter(BridgeRequest request, Assembly sdk) {
            this.request = request;
            this.sdk = sdk;
            var dataContract = sdk.GetType("TuneLab.Extensions.Voices.ISynthesisData", true)!;
            noteContract = sdk.GetType("TuneLab.Extensions.Voices.ISynthesisNote", true)!;
            automationContract = sdk.GetType("TuneLab.Extensions.Voices.IAutomationValueGetter", true)!;
            notes = Array.CreateInstance(noteContract, request.Notes.Length);
            for (var i = 0; i < request.Notes.Length; i++)
                notes.SetValue(CreateProxy(noteContract, new NoteAdapter(request, i, sdk, notes)), i);
            pitch = CreateAutomation(request.Pitch ?? new BridgeCurve(), automationContract);
            partProperties = BuildProperties(request.PartProperties,
                dataContract.GetProperty("PartProperties")!.PropertyType.Assembly);
        }

        public override object? Invoke(MethodInfo method, object?[]? args) => method.Name switch {
            "get_Notes" => notes,
            "get_PartProperties" => partProperties,
            "get_Pitch" => pitch,
            "GetAutomation" => GetAutomation(args!),
            _ => throw new NotSupportedException("Legacy synthesis data member is not supported: " + method.Name),
        };

        object? GetAutomation(object?[] args) {
            var id = (string)args[0]!;
            if (request.Automations is not null && request.Automations.TryGetValue(id, out var curve)) {
                args[1] = CreateAutomation(curve, automationContract);
                return true;
            }
            args[1] = null;
            return false;
        }
    }

    sealed class NoteAdapter : ContractAdapter {
        readonly BridgeRequest request;
        readonly int index;
        readonly Assembly sdk;
        readonly Array notes;
        readonly object properties;
        public NoteAdapter(BridgeRequest request, int index, Assembly sdk, Array notes) {
            this.request = request; this.index = index; this.sdk = sdk; this.notes = notes;
            var values = request.NoteProperties is not null && index < request.NoteProperties.Length ? request.NoteProperties[index] : null;
            properties = BuildProperties(values,
                sdk.GetType("TuneLab.Extensions.Voices.ISynthesisNote", true)!
                    .GetProperty("Properties")!.PropertyType.Assembly);
        }
        public override object? Invoke(MethodInfo method, object?[]? args) => method.Name switch {
            "get_Next" => index + 1 < request.Notes.Length ? notes.GetValue(index + 1) : null,
            "get_Last" => index > 0 ? notes.GetValue(index - 1) : null,
            "get_StartTime" => request.Notes[index].StartSeconds,
            "get_EndTime" => request.Notes[index].EndSeconds,
            "get_Pitch" => request.Notes[index].Tone,
            "get_Lyric" => request.Notes[index].Lyric ?? string.Empty,
            "get_Properties" => properties,
            "get_Phonemes" => Array.CreateInstance(method.ReturnType.GenericTypeArguments[0], 0),
            _ => throw new NotSupportedException("Legacy note member is not supported: " + method.Name),
        };
    }

    sealed class AutomationAdapter(BridgeCurve curve) : ContractAdapter {
        public override object? Invoke(MethodInfo method, object?[]? args) {
            if (method.Name != "GetValue") throw new NotSupportedException("Legacy automation member is not supported: " + method.Name);
            var times = (IReadOnlyList<double>)args![0]!;
            return times.Select(curve.Sample).ToArray();
        }
    }

    static object CreateAutomation(BridgeCurve curve, Type contract) => CreateProxy(contract, new AutomationAdapter(curve));

    static object BuildProperties(Dictionary<string, JsonElement>? values, Assembly sdk) {
        var propertiesType = sdk.GetType("TuneLab.Base.Properties.PropertyObject", false)
            ?? AssemblyLoadContext.GetLoadContext(sdk)?.Assemblies
                .Select(assembly => assembly.GetType("TuneLab.Base.Properties.PropertyObject", false))
                .FirstOrDefault(type => type is not null)
            ?? throw new InvalidOperationException("Could not locate TuneLab.Base.PropertyObject in the loaded legacy SDK.");
        var baseSdk = propertiesType.Assembly;
        var mapType = baseSdk.GetType("TuneLab.Base.Structures.Map`2", true)!.MakeGenericType(typeof(string), baseSdk.GetType("TuneLab.Base.Properties.PropertyValue", true)!);
        var map = Activator.CreateInstance(mapType)!;
        var add = mapType.GetMethod("Add", [typeof(string), mapType.GetGenericArguments()[1]])!;
        var propertyValue = mapType.GetGenericArguments()[1];
        foreach (var (key, element) in values ?? []) {
            object? value = element.ValueKind switch {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when element.TryGetDouble(out var number) => number,
                JsonValueKind.Object => BuildProperties(element.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal), sdk),
                _ => throw new NotSupportedException($"Legacy property '{key}' uses unsupported JSON kind {element.ValueKind}."),
            };
            if (value is null) throw new InvalidDataException($"Legacy property '{key}' cannot be null.");
            var create = propertyValue.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: [value.GetType()], modifiers: null);
            if (create is null) continue;
            add.Invoke(map, [key, create.Invoke(null, [value])]);
        }
        return Activator.CreateInstance(propertiesType, map)!;
    }
}
}
