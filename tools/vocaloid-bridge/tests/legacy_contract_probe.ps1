param([string]$SdkSourceRoot = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('vocaloid-legacy-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $fixturePath = Join-Path $scratch 'LegacyVoiceFixture.dll'
    $basePath = Join-Path $scratch 'TuneLab.Base.dll'
    $voicesPath = Join-Path $scratch 'TuneLab.Extensions.Voices.dll'
    if ($SdkSourceRoot) {
        $SdkSourceRoot = (Resolve-Path -LiteralPath $SdkSourceRoot).Path
        $powershellDir = Split-Path -Parent (Get-Process -Id $PID).Path
        $roslyn = @((Join-Path $powershellDir 'Microsoft.CodeAnalysis.dll'), (Join-Path $powershellDir 'Microsoft.CodeAnalysis.CSharp.dll'))
        foreach ($path in $roslyn) { $null = [Reflection.Assembly]::LoadFrom($path) }
        function Compile-LegacySdkSource([string]$AssemblyName, [string[]]$SourceFiles, [string]$OutputPath, [string[]]$References) {
            $syntaxTreeType = [Type]::GetType('Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis')
            $syntaxListType = [Type]::GetType('System.Collections.Generic.List`1').MakeGenericType($syntaxTreeType)
            $syntaxTrees = [Activator]::CreateInstance($syntaxListType)
            $treeAdd = $syntaxListType.GetMethod('Add')
            $syntaxClass = [Type]::GetType('Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree, Microsoft.CodeAnalysis.CSharp')
            $parseOptionsType = [Type]::GetType('Microsoft.CodeAnalysis.CSharp.CSharpParseOptions, Microsoft.CodeAnalysis.CSharp')
            $languageVersionType = [Type]::GetType('Microsoft.CodeAnalysis.CSharp.LanguageVersion, Microsoft.CodeAnalysis.CSharp')
            $parseOptions = $parseOptionsType.GetProperty('Default').GetValue($null).WithLanguageVersion([Enum]::Parse($languageVersionType, 'CSharp12'))
            $parseMethod = $syntaxClass.GetMethods([Reflection.BindingFlags]'Public,Static') | Where-Object { $_.Name -eq 'ParseText' -and $_.GetParameters().Count -eq 5 -and $_.GetParameters()[0].ParameterType -eq [string] } | Select-Object -First 1
            foreach ($source in $SourceFiles) {
                $tree = $parseMethod.Invoke($null, [object[]]@([IO.File]::ReadAllText($source), $parseOptions, $source, $null, [Threading.CancellationToken]::None))
                $treeAdd.Invoke($syntaxTrees, [object[]]@($tree))
            }
            $metadataType = [Type]::GetType('Microsoft.CodeAnalysis.MetadataReference, Microsoft.CodeAnalysis')
            $metadataListType = [Type]::GetType('System.Collections.Generic.List`1').MakeGenericType($metadataType)
            $metadata = [Activator]::CreateInstance($metadataListType)
            $metadataAdd = $metadataListType.GetMethod('Add')
            $metadataPropertiesType = [Type]::GetType('Microsoft.CodeAnalysis.MetadataReferenceProperties, Microsoft.CodeAnalysis')
            $createReference = $metadataType.GetMethod('CreateFromFile')
            $referencePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($path in ([string][AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator)) { $null = $referencePaths.Add($path) }
            foreach ($path in $References) { $null = $referencePaths.Add($path) }
            foreach ($path in $referencePaths) {
                $reference = $createReference.Invoke($null, [object[]]@($path, [Activator]::CreateInstance($metadataPropertiesType), $null))
                $metadataAdd.Invoke($metadata, [object[]]@($reference))
            }
            $outputKindType = [Type]::GetType('Microsoft.CodeAnalysis.OutputKind, Microsoft.CodeAnalysis')
            $compilationOptionsType = [Type]::GetType('Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions, Microsoft.CodeAnalysis.CSharp')
            $optionsConstructor = $compilationOptionsType.GetConstructors() | Where-Object { $_.GetParameters().Count -eq 27 } | Select-Object -First 1
            $optionArgs = [object[]]::new(27)
            $optionArgs[0] = [Enum]::Parse($outputKindType, 'DynamicallyLinkedLibrary')
            for ($i = 1; $i -lt $optionArgs.Length; $i++) { $optionArgs[$i] = [Type]::Missing }
            $options = $optionsConstructor.Invoke($optionArgs)
            $nullableOptionsType = [Type]::GetType('Microsoft.CodeAnalysis.NullableContextOptions, Microsoft.CodeAnalysis')
            $options = $options.GetType().GetMethod('WithNullableContextOptions').Invoke($options, [object[]]@([Enum]::Parse($nullableOptionsType, 'Enable')))
            $compilationType = [Type]::GetType('Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp')
            $compilationCreate = $compilationType.GetMethods([Reflection.BindingFlags]'Public,Static') | Where-Object { $_.Name -eq 'Create' -and $_.GetParameters().Count -eq 4 } | Select-Object -First 1
            $compilation = $compilationCreate.Invoke($null, [object[]]@($AssemblyName, $syntaxTrees, $metadata, $options))
            $stream = [IO.File]::Create($OutputPath)
            try {
                $emit = $compilation.Emit($stream)
                if (-not $emit.Success) {
                    $errors = @($emit.Diagnostics | Where-Object Severity -eq 'Error' | ForEach-Object ToString)
                    throw "Roslyn compile failed for ${AssemblyName}: $($errors -join "`n")"
                }
            } finally { $stream.Dispose() }
        }
        foreach ($spec in @(@{ Name = 'TuneLab.Base'; Folder = 'TuneLab.Base'; Output = $basePath; References = @() },
                            @{ Name = 'TuneLab.Extensions.Voices'; Folder = 'TuneLab.Extensions.Voices'; Output = $voicesPath; References = @($basePath) })) {
            $sourceFolder = Join-Path $SdkSourceRoot $spec.Folder
            if (-not (Test-Path -LiteralPath $sourceFolder -PathType Container)) { throw "SDK source folder not found: $sourceFolder" }
            $compileDir = Join-Path $scratch ($spec.Name + '-sources')
            New-Item -ItemType Directory -Path $compileDir | Out-Null
            $globalUsingsPath = Join-Path $compileDir 'GlobalUsings.cs'
            [IO.File]::WriteAllText($globalUsingsPath, @'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
#nullable enable
'@)
            $versionPath = Join-Path $compileDir 'AssemblyInfo.cs'
            [IO.File]::WriteAllText($versionPath, '[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]')
            $sdkFiles = @(Get-ChildItem -LiteralPath $sourceFolder -Filter '*.cs' -File -Recurse | Select-Object -ExpandProperty FullName)
            $compileFiles = @($globalUsingsPath, $versionPath) + $sdkFiles
            Compile-LegacySdkSource $spec.Name $compileFiles $spec.Output ([string[]]$spec.References)
            $identity = [Reflection.AssemblyName]::GetAssemblyName($spec.Output)
            if ($identity.Name -ne $spec.Name -or $identity.Version.ToString() -ne '1.0.0.0') { throw "Unexpected legacy SDK assembly identity: $identity" }
        }
    }
    $fixture = @'
#nullable enable
#pragma warning disable
using System;
using System.Collections.Generic;
using TuneLab.Base.Structures;
using TuneLab.Base.Properties;
namespace TuneLab.Base.Structures {
 public interface IReadOnlyKeyWithValue<out K,out V> { K Key {get;} V Value {get;} }
 public interface IReadOnlyMap<K,out V> : IEnumerable<IReadOnlyKeyWithValue<K,V>>, IReadOnlyCollection<IReadOnlyKeyWithValue<K,V>> where K:notnull { V this[K key]{get;} IEnumerable<K> Keys{get;} IEnumerable<V> Values{get;} bool ContainsKey(K key); V GetValue(K key,out bool success); }
 public interface IReadOnlyOrderedMap<K,out V> : IReadOnlyMap<K,V>, IReadOnlyList<IReadOnlyKeyWithValue<K,V>> where K:notnull { K KeyAt(int i); V ValueAt(int i); }
 public sealed class Entry<K,V>(K key,V value) : IReadOnlyKeyWithValue<K,V> { public K Key=>key; public V Value=>value; }
 public sealed class Map<K,V> : IReadOnlyOrderedMap<K,V> where K:notnull { readonly List<Entry<K,V>> items=new(); public void Add(K k,V v)=>items.Add(new(k,v)); public int Count=>items.Count; public K KeyAt(int i)=>items[i].Key; public V ValueAt(int i)=>items[i].Value; public V this[K k]=>items.Find(x=>EqualityComparer<K>.Default.Equals(x.Key,k))!.Value; public IEnumerable<K> Keys=>items.ConvertAll(x=>x.Key); public IEnumerable<V> Values=>items.ConvertAll(x=>x.Value); public bool ContainsKey(K k)=>items.Exists(x=>EqualityComparer<K>.Default.Equals(x.Key,k)); public V GetValue(K k,out bool ok){ok=ContainsKey(k);return ok?this[k]:default!;} public IEnumerator<IReadOnlyKeyWithValue<K,V>> GetEnumerator()=>items.GetEnumerator(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator(); IReadOnlyKeyWithValue<K,V> IReadOnlyList<IReadOnlyKeyWithValue<K,V>>.this[int i]=>items[i]; }
 public struct Point(double x,double y) { public double X=x; public double Y=y; }
}
namespace TuneLab.Base.Properties {
 public readonly struct PropertyValue { readonly object value; PropertyValue(object v){value=v;} public static PropertyValue Create(bool v)=>new(v); public static PropertyValue Create(double v)=>new(v); public static PropertyValue Create(string v)=>new(v); }
 public sealed class PropertyObject { public PropertyObject(object map){} }
 public interface IPropertyConfig { }
}
namespace TuneLab.Extensions.Voices {
 public sealed class AutomationConfig { }
 public struct SynthesisSegment<T> where T:ISynthesisNote { public PropertyObject PartProperties; public IReadOnlyCollection<T> Notes; }
 public struct VoiceSourceInfo { public string Name{get;set;} public string Description{get;set;} }
 public interface IVoiceEngine { IReadOnlyOrderedMap<string,VoiceSourceInfo> VoiceInfos{get;} bool Init(string path,out string? error); void Destroy(); IVoiceSource CreateVoiceSource(string id); }
 public interface IVoiceSource { string Name{get;} string DefaultLyric{get;} IReadOnlyOrderedMap<string,AutomationConfig> AutomationConfigs{get;} IReadOnlyOrderedMap<string,IPropertyConfig> PartProperties{get;} IReadOnlyOrderedMap<string,IPropertyConfig> NoteProperties{get;} IReadOnlyList<SynthesisSegment<T>> Segment<T>(SynthesisSegment<T> segment) where T:ISynthesisNote; ISynthesisTask CreateSynthesisTask(ISynthesisData data); }
 public interface ISynthesisTask { event Action<SynthesisResult>? Complete; event Action<double>? Progress; event Action<string>? Error; void Start(); void Suspend(); void Resume(); void Stop(); void SetDirty(string dirtyType); }
 public interface ISynthesisData { IEnumerable<ISynthesisNote> Notes{get;} PropertyObject PartProperties{get;} bool GetAutomation(string id,out IAutomationValueGetter? automation); IAutomationValueGetter Pitch{get;} }
 public interface ISynthesisNote { ISynthesisNote? Next{get;} ISynthesisNote? Last{get;} double StartTime{get;} double EndTime{get;} int Pitch{get;} string Lyric{get;} PropertyObject Properties{get;} IReadOnlyList<SynthesizedPhoneme> Phonemes{get;} }
 public interface IAutomationValueGetter { double[] GetValue(IReadOnlyList<double> times); }
 public struct SynthesizedPhoneme { public string Symbol; public double StartTime; public double EndTime; }
 public sealed class SynthesisResult { public readonly double StartTime; public readonly int SamplingRate; public readonly float[] AudioData; public SynthesisResult(double start,int rate,float[] audio){StartTime=start;SamplingRate=rate;AudioData=audio;} }
 public sealed class VoiceEngine : IVoiceEngine { public IReadOnlyOrderedMap<string,VoiceSourceInfo> VoiceInfos{get;} = Build(); static Map<string,VoiceSourceInfo> Build(){var m=new Map<string,VoiceSourceInfo>();m.Add("voice-a",new VoiceSourceInfo{Name="Fixture Voice"});return m;} public bool Init(string path,out string? error){error=null;return System.IO.Path.IsPathFullyQualified(path);} public void Destroy(){} public IVoiceSource CreateVoiceSource(string id)=>id=="voice-a"?new Source():throw new Exception("unknown voice"); }
 public sealed class Source : IVoiceSource { public string Name=>"Fixture Voice"; public string DefaultLyric=>"a"; public IReadOnlyOrderedMap<string,AutomationConfig> AutomationConfigs=>new Map<string,AutomationConfig>(); public IReadOnlyOrderedMap<string,IPropertyConfig> PartProperties=>new Map<string,IPropertyConfig>(); public IReadOnlyOrderedMap<string,IPropertyConfig> NoteProperties=>new Map<string,IPropertyConfig>(); public IReadOnlyList<SynthesisSegment<T>> Segment<T>(SynthesisSegment<T> segment) where T:ISynthesisNote=>Array.Empty<SynthesisSegment<T>>(); public ISynthesisTask CreateSynthesisTask(ISynthesisData data)=>new TaskImpl(data); }
 public sealed class TaskImpl(ISynthesisData data) : ISynthesisTask { public event Action<SynthesisResult>? Complete; public event Action<double>? Progress; public event Action<string>? Error; public void Start(){ var notes=new List<ISynthesisNote>(data.Notes); if(notes.Count!=2 || notes[0].Next?.Lyric!="i" || notes[1].Last?.Lyric!="a" || Math.Abs(data.Pitch.GetValue(new double[]{0.25})[0]-60.5)>0.0001 || !data.GetAutomation("gain",out var gain) || Math.Abs(gain!.GetValue(new double[]{0.5})[0]-0.5)>0.0001) { Error?.Invoke("contract probe did not receive expected data"); return; } Complete?.Invoke(new SynthesisResult(0.125,48000,new float[]{0.1f,-0.1f})); } public void Suspend(){} public void Resume(){} public void Stop(){} public void SetDirty(string dirtyType){} }
}
'@
    if ($SdkSourceRoot) {
        $fixture = @'
#nullable enable
using System;
using System.Collections.Generic;
using TuneLab.Base.Properties;
using TuneLab.Base.Structures;
using TuneLab.Extensions.Voices;
namespace LegacyFixture;
public sealed class VoiceEngine : IVoiceEngine {
    readonly OrderedMap<string, VoiceSourceInfo> voices = new();
    public VoiceEngine() => voices.Add("voice-a", new VoiceSourceInfo { Name = "Fixture Voice", Description = "ABI probe" });
    IReadOnlyOrderedMap<string, VoiceSourceInfo> IVoiceEngine.VoiceInfos => voices;
    bool IVoiceEngine.Init(string enginePath, out string? error) { error = null; return System.IO.Path.IsPathFullyQualified(enginePath); }
    void IVoiceEngine.Destroy() { }
    IVoiceSource IVoiceEngine.CreateVoiceSource(string id) => id == "voice-a" ? new Source() : throw new InvalidOperationException("unknown voice");
}
sealed class Source : IVoiceSource {
    public string Name => "Fixture Voice";
    public string DefaultLyric => "a";
    public IReadOnlyOrderedMap<string, AutomationConfig> AutomationConfigs { get; } = new OrderedMap<string, AutomationConfig>();
    public IReadOnlyOrderedMap<string, IPropertyConfig> PartProperties { get; } = new OrderedMap<string, IPropertyConfig>();
    public IReadOnlyOrderedMap<string, IPropertyConfig> NoteProperties { get; } = new OrderedMap<string, IPropertyConfig>();
    public IReadOnlyList<SynthesisSegment<T>> Segment<T>(SynthesisSegment<T> segment) where T : ISynthesisNote => Array.Empty<SynthesisSegment<T>>();
    public ISynthesisTask CreateSynthesisTask(ISynthesisData data) => new SynthesisTask(data);
}
sealed class SynthesisTask(ISynthesisData data) : ISynthesisTask {
    public event Action<SynthesisResult>? Complete;
    public event Action<double>? Progress;
    public event Action<string>? Error;
    public void Start() {
        var notes = new List<ISynthesisNote>(data.Notes);
        if (notes.Count != 2 || !ReferenceEquals(notes[0].Next, notes[1]) || !ReferenceEquals(notes[1].Last, notes[0]) ||
            Math.Abs(data.Pitch.GetValue(new double[] { 0.25 })[0] - 60.5) > 0.0001 ||
            !data.GetAutomation("gain", out var gain) || Math.Abs(gain!.GetValue(new double[] { 0.5 })[0] - 0.5) > 0.0001) {
            Error?.Invoke("contract probe did not receive expected data"); return;
        }
        Complete?.Invoke(new SynthesisResult(0.125, 48000, new float[] { 0.1f, -0.1f }));
    }
    public void Suspend() { }
    public void Resume() { }
    public void Stop() { }
    public void SetDirty(string dirtyType) { }
}
'@
        $fixtureSource = Join-Path $scratch 'LegacyVoiceFixture.cs'
        [IO.File]::WriteAllText($fixtureSource, $fixture)
        Compile-LegacySdkSource 'LegacyVoiceFixture' @($fixtureSource) $fixturePath @($basePath, $voicesPath)
    } else {
        Add-Type -TypeDefinition $fixture -Language CSharp -OutputAssembly $fixturePath -OutputType Library
    }
    Add-Type -Path @((Join-Path $repo 'plugins\vocaloid-bridge\Core\Contracts.cs'),
        (Join-Path $repo 'plugins\vocaloid-bridge\Host\LegacyTuneLabBackend.cs')) -CompilerOptions @('/langversion:12.0', '/nullable:enable')
    $engineType = if ($SdkSourceRoot) { '' } else { 'TuneLab.Extensions.Voices.VoiceEngine' }
    $backend = [VocaloidBridge.LegacyTuneLabBackend]::new($fixturePath, $engineType, 'fixture-engine')
    try {
        $voices = $backend.ListVoices()
        if ($voices.Count -ne 1 -or $voices[0].Id -ne 'voice-a' -or $voices[0].Name -ne 'Fixture Voice') { throw 'VoiceInfos adapter failed.' }
        $request = [VocaloidBridge.BridgeRequest]::new()
        $request.VoiceId = 'voice-a'
        $request.Notes = @(
            [VocaloidBridge.BridgeNote]@{ StartSeconds = 0; EndSeconds = 0.5; Tone = 60; Lyric = 'a' },
            [VocaloidBridge.BridgeNote]@{ StartSeconds = 0.5; EndSeconds = 1; Tone = 62; Lyric = 'i' }
        )
        $request.Pitch = [VocaloidBridge.BridgeCurve]@{ TimesSeconds = @(0, 1); Values = @(60, 62) }
        $request.Automations['gain'] = [VocaloidBridge.BridgeCurve]@{ TimesSeconds = @(0, 1); Values = @(0, 1) }
        $request.VoiceId = 'VOICE-A'
        $unknownVoiceRejected = $false
        try { $null = $backend.RenderAsync($request, [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        catch { $unknownVoiceRejected = $true }
        if (-not $unknownVoiceRejected) { throw 'Case-mismatched voice ID was silently accepted.' }
        $request.VoiceId = 'voice-a'
        $request.PartProperties['unsupported-array'] = [System.Text.Json.JsonDocument]::Parse('[1,2]').RootElement.Clone()
        $unsupportedPropertyRejected = $false
        try { $null = $backend.RenderAsync($request, [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        catch { $unsupportedPropertyRejected = $true }
        if (-not $unsupportedPropertyRejected) { throw 'Unsupported legacy property value was silently ignored.' }
        $request.PartProperties.Remove('unsupported-array') | Out-Null
        try { $audio = $backend.RenderAsync($request, [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        catch { Write-Error $_.Exception.ToString(); throw }
        $audio.Validate()
        if ($audio.StartSeconds -ne 0.125 -or $audio.SampleRate -ne 48000 -or $audio.Samples.Length -ne 2) { throw 'Synthesis result mapping failed.' }
        'PASS: legacy IVoiceEngine, ordered VoiceInfos, automatic engine discovery, exact case-sensitive voice ID, explicit property errors, note reference identity, pitch/automation getters, completion event, sample rate and alignment.'
    } finally { $backend.Dispose() }
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $scratchLeaf = Split-Path -Leaf $resolvedScratch
    if ($resolvedScratch.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        $scratchLeaf -match '^vocaloid-legacy-probe-[0-9a-f]{32}$' -and (Test-Path -LiteralPath $resolvedScratch -PathType Container)) {
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
