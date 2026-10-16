param(
    [string]$AppDirectory = (Join-Path $PSScriptRoot '../dist/OpenUtau-V5-AutoPitch'),
    [string]$EngineDirectory = 'D:/Program Files/POCALOID5ESV',
    [string]$VoiceDirectory = 'D:/Program Files (x86)/VoiceDB',
    [string]$DataDirectory = '',
    [string]$AcceptanceJson = ''
)
$ErrorActionPreference = 'Stop'
$bridgeRoot = Split-Path -Parent $PSScriptRoot
$app = [IO.Path]::GetFullPath($AppDirectory)
$engine = [IO.Path]::GetFullPath($EngineDirectory)
$voices = [IO.Path]::GetFullPath($VoiceDirectory)
$outRoot = Join-Path $bridgeRoot 'test-output'
$data = if ($DataDirectory) { [IO.Path]::GetFullPath($DataDirectory) } else {
    Join-Path $outRoot ('vocaloid-import-data/' + [Guid]::NewGuid().ToString('N'))
}
$reportPath = if ($AcceptanceJson) { [IO.Path]::GetFullPath($AcceptanceJson) } else {
    Join-Path $outRoot 'vocaloid-import-acceptance.json'
}
if (-not (Test-Path -LiteralPath (Join-Path $app 'OpenUtau.Core.dll') -PathType Leaf)) {
    throw "OpenUtau.Core.dll not found in package: $app"
}
if (-not (Test-Path -LiteralPath (Join-Path $app 'VocaloidBridge/V5/run-native-host.ps1') -PathType Leaf)) {
    throw "V5 bridge host is missing from package: $app"
}
if (-not (Test-Path -LiteralPath $voices -PathType Container)) { throw "Voicebank directory not found: $voices" }
New-Item -ItemType Directory -Force -Path $data,(Join-Path $data 'Cache') | Out-Null
$env:PATH = $app + [IO.Path]::PathSeparator + $env:PATH

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
public static class VocaloidImportAssemblyLoader {
    static string directory;
    public static Assembly Load(string path) {
        directory = path;
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string file = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
        };
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "OpenUtau.Core.dll"));
    }
}
'@
$core = [VocaloidImportAssemblyLoader]::Load($app)
$allFlags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance,FlattenHierarchy'
function Get-ReflectedType([string]$Name) { return $core.GetType($Name, $true) }
function Get-ReflectedMember($Object, [string]$Name) {
    $type = $Object.GetType()
    $property = $type.GetProperty($Name, $allFlags)
    if ($property) { return $property.GetValue($Object) }
    return $type.GetField($Name, $allFlags).GetValue($Object)
}
function Set-ReflectedMember($Object, [string]$Name, $Value) {
    $type = $Object.GetType()
    $property = $type.GetProperty($Name, $allFlags)
    if ($property) { $property.SetValue($Object, $Value); return }
    $type.GetField($Name, $allFlags).SetValue($Object, $Value)
}
function Get-Singleton([string]$Name, [string]$Member = 'Inst') {
    $type = Get-ReflectedType $Name
    $property = $type.GetProperty($Member, $allFlags)
    if ($property) { return $property.GetValue($null) }
    return $type.GetField($Member, $allFlags).GetValue($null)
}

$paths = Get-Singleton 'OpenUtau.Core.PathManager'
$pathType = $paths.GetType()
foreach ($entry in @{ RootPath = $app; DataPath = $data; CachePath = (Join-Path $data 'Cache') }.GetEnumerator()) {
    $pathType.GetProperty($entry.Key, $allFlags).SetValue($paths, $entry.Value)
}
$prefs = Get-Singleton 'OpenUtau.Core.Util.Preferences' 'Default'
Set-ReflectedMember $prefs 'AdditionalSingerPath' (Join-Path $data 'Singers')

$importer = Get-ReflectedType 'OpenUtau.Core.Vocaloid.VocaloidVoicebankImporter'
$importMethod = $importer.GetMethod('ImportAsync', $allFlags)
if (-not $importMethod) { throw 'The package does not expose VocaloidVoicebankImporter.ImportAsync.' }
$invalidPathError = ''
try {
    $invalidArgs = [object[]]@([string]$engine, [string](Join-Path $data 'missing-voice-directory'), [Threading.CancellationToken]::None)
    $invalidTask = $importMethod.Invoke($null, $invalidArgs)
    $invalidTask.GetAwaiter().GetResult() | Out-Null
    throw 'ImportAsync unexpectedly accepted a missing voicebank directory.'
} catch [Reflection.TargetInvocationException] {
    $inner = $_.Exception.InnerException
    if ($inner -isnot [IO.DirectoryNotFoundException] -or $inner.Message -notmatch 'Voicebank directory was not found') { throw }
    $invalidPathError = $inner.Message
} catch [IO.DirectoryNotFoundException] {
    if ($_.Exception.Message -notmatch 'Voicebank directory was not found') { throw }
    $invalidPathError = $_.Exception.Message
}

$importArgs = [object[]]@([string]$engine, [string]$voices, [Threading.CancellationToken]::None)
$task = $importMethod.Invoke($null, $importArgs)
$result = $task.GetAwaiter().GetResult()
$imported = @($result.Voices)
if ($imported.Count -ne 6) { throw "Expected 6 imported voicebanks; got $($imported.Count)." }
$ready = @($imported | Where-Object Ready).Count
$pending = @($imported | Where-Object { -not $_.Ready }).Count
if ($ready -ne 3 -or $pending -ne 3) { throw "Expected 3 ready and 3 authorization-pending voices; got $ready/$pending." }
$generations = @($imported | ForEach-Object Generation | Sort-Object -Unique)
if (($generations -join ',') -ne '3,4,5') { throw "Unexpected voicebank generations: $($generations -join ',')." }

$singerRoot = [IO.Path]::GetFullPath($result.SingerDirectory)
$directoriesBefore = @(Get-ChildItem -LiteralPath $singerRoot -Directory | Where-Object Name -Like 'vocaloid-*')
if ($directoriesBefore.Count -ne 6) { throw "Expected six generated singer directories; found $($directoriesBefore.Count)." }
$customConfigPath = Join-Path $directoriesBefore[0].FullName 'vocaloid-bridge.json'
$customConfig = Get-Content -LiteralPath $customConfigPath -Raw | ConvertFrom-Json
$customConfig.HeadMs = 321
$customConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $customConfigPath -Encoding utf8
$characterPath = Join-Path $directoriesBefore[0].FullName 'character.txt'
Set-Content -LiteralPath $characterPath -Value @('name=User Custom Singer Name','author=user') -Encoding utf8
$repeatTask = $importMethod.Invoke($null, $importArgs)
$repeatResult = $repeatTask.GetAwaiter().GetResult()
$directoriesAfter = @(Get-ChildItem -LiteralPath $singerRoot -Directory | Where-Object Name -Like 'vocaloid-*')
if ($directoriesAfter.Count -ne 6) { throw "Repeat import created duplicate singer folders: $($directoriesAfter.Count)." }
$preservedConfig = Get-Content -LiteralPath $customConfigPath -Raw | ConvertFrom-Json
if ([double]$preservedConfig.HeadMs -ne 321) { throw "Repeat import did not preserve the user's HeadMs setting." }
if (-not ((Get-Content -LiteralPath $characterPath -Raw) -match 'name=User Custom Singer Name')) {
    throw "Repeat import did not preserve the user's custom singer name."
}

$bridgeVoiceType = $core.GetType('VocaloidBridge.BridgeVoice', $true)
$bridgeNoteType = $core.GetType('VocaloidBridge.BridgeNote', $true)
$bridgeCurveType = $core.GetType('VocaloidBridge.BridgeCurve', $true)
$bridgeRequestType = $core.GetType('VocaloidBridge.BridgeRequest', $true)
$bridgeSessionType = $core.GetType('VocaloidBridge.BridgeSession', $true)
$pendingRejections = @()
foreach ($voice in $imported | Where-Object { -not $_.Ready }) {
    $idHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]$voice.Id))).Substring(0,16).ToLowerInvariant()
    $voiceDirectoryPath = Join-Path $singerRoot ('vocaloid-' + $idHash)
    $metadataPath = Join-Path $voiceDirectoryPath 'vocaloid-voice.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw "Could not locate generated singer directory for pending voice $($voice.Id)." }
    $metadataVoice = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadataVoice.Id -cne $voice.Id -or $metadataVoice.Ready) { throw "Generated voice metadata does not match pending voice $($voice.Id)." }
    $configPath = Join-Path $voiceDirectoryPath 'vocaloid-bridge.json'
    $request = [Activator]::CreateInstance($bridgeRequestType)
    $request.VoiceId = [string]$voice.Id
    $note = [Activator]::CreateInstance($bridgeNoteType)
    $note.StartSeconds = 0.0; $note.EndSeconds = 0.2; $note.Tone = 60; $note.Lyric = 'a'
    $notes = [Array]::CreateInstance($bridgeNoteType, 1); $notes.SetValue($note, 0)
    $request.Notes = $notes
    $pitch = [Activator]::CreateInstance($bridgeCurveType)
    $pitch.TimesSeconds = [double[]]@(0.0); $pitch.Values = [double[]]@(60.0)
    $request.Pitch = $pitch
    $session = $null
    try {
        $session = $bridgeSessionType.GetMethod('Get', $allFlags).Invoke($null, [object[]]@([string]$configPath))
        $renderMethod = $bridgeSessionType.GetMethod('RenderAsync', $allFlags)
        $renderTask = $renderMethod.Invoke($session, [object[]]@($request, [Threading.CancellationToken]::None))
        $renderTask.GetAwaiter().GetResult() | Out-Null
        throw "Unauthorized voice $($voice.Id) returned rendered audio instead of refusing synthesis."
    } catch [InvalidOperationException] {
        $errorText = $_.Exception.Message
        if ($errorText -notmatch 'no V5 synthesis license' -or $errorText -notmatch 'NumLicenses=0') { throw }
        $pendingRejections += [ordered]@{ id=$voice.Id; error=$errorText }
    } finally {
        if ($session) { $session.Dispose() }
    }
}
if ($pendingRejections.Count -ne $pending) { throw "Expected $pending unauthorized render rejections, got $($pendingRejections.Count)." }

# Exercise OpenUtau's actual singer discovery against the isolated generated data path.
$doc = Get-Singleton 'OpenUtau.Core.DocManager'
$docType = $doc.GetType()
$singerManager = Get-Singleton 'OpenUtau.Core.SingerManager'
$initializeSingers = $singerManager.GetType().GetMethods($allFlags) | Where-Object {
    $_.Name -eq 'Initialize' -and $_.GetParameters().Length -eq 0
} | Select-Object -First 1
if ($initializeSingers) { $initializeSingers.Invoke($singerManager, @()) }
$search = $singerManager.GetType().GetMethod('SearchAllSingers', $allFlags)
if ($search) { $search.Invoke($singerManager, @()) }
$singerMap = Get-ReflectedMember $singerManager 'Singers'
$discovered = @($singerMap.Values | Where-Object { $_.GetType().FullName -eq 'OpenUtau.Core.Vocaloid.VocaloidSinger' })
if ($discovered.Count -ne 6) { throw "SingerManager discovered $($discovered.Count) VocaloidSinger entries, expected 6." }

$report = [ordered]@{
    status = 'vocaloid-voicebank-import-verified'
    package = $app
    engine_directory = $engine
    voice_directory = $voices
    data_directory = $data
    singer_directory = $singerRoot
    imported_count = $imported.Count
    ready_count = $ready
    authorization_pending_count = $pending
    generations = $generations
    singer_manager_discovered = $discovered.Count
    repeat_import_directory_count = $directoriesAfter.Count
    preserved_head_ms = [double]$preservedConfig.HeadMs
    preserved_custom_name = $true
    pending_render_rejections = $pendingRejections
    invalid_path_error = $invalidPathError
    voices = @($imported | ForEach-Object { [ordered]@{ id=$_.Id; name=$_.Name; generation=$_.Generation; ready=$_.Ready } })
    tested_utc = [DateTime]::UtcNow.ToString('o')
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output ($report | ConvertTo-Json -Depth 8)
