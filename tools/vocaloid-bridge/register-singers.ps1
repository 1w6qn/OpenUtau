param(
    [Parameter(Mandatory=$true)][string]$Config,
    [Parameter(Mandatory=$true)][string]$SingerDirectory,
    [string]$VoiceId = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -Path @((Join-Path $PSScriptRoot 'Core/Contracts.cs'), (Join-Path $PSScriptRoot 'Core/BridgeSession.cs')) -CompilerOptions '/nullable:enable'
$bridgeConfigPath = [IO.Path]::GetFullPath($Config)
$bridgeConfig = [VocaloidBridge.BridgeConfig]::Load($bridgeConfigPath)
$bridgeConfiguredLanguage = $bridgeConfig.LyricLanguage
$bridgeHostScript = $bridgeConfig.HostScript
$bridgeRoot = [IO.Path]::GetFullPath($SingerDirectory)
$bridgeSession = [VocaloidBridge.BridgeSession]::Get($bridgeConfigPath)
try {
    $bridgeVoices = $bridgeSession.ListVoicesAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($VoiceId) {
        $bridgeVoices = @($bridgeVoices | Where-Object Id -CEQ $VoiceId)
        if ($bridgeVoices.Count -ne 1) { throw 'The requested exact voice ID is not installed in this engine.' }
    }
    if ($bridgeVoices.Count -eq 0) { throw 'The engine reported no installed voices.' }
    $bridgePlans = @()
    foreach ($bridgeVoice in $bridgeVoices) {
        $bridgeDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($bridgeVoice.Id))).Substring(0,16).ToLowerInvariant()
        $bridgeDestination = Join-Path $bridgeRoot ('vocaloid-' + $bridgeDigest)
        if (Test-Path -LiteralPath $bridgeDestination) { throw "Singer directory already exists: $bridgeDestination. No singers were written." }
        $bridgePlans += @{ path=$bridgeDestination; voice=$bridgeVoice }
    }
    foreach ($bridgePlan in $bridgePlans) {
        $bridgeConfig.VoiceId = $bridgePlan.voice.Id
        $bridgeConfig.HostScript = [IO.Path]::GetRelativePath($bridgePlan.path, $bridgeHostScript)
        $bridgeConfig.LyricLanguage = if ($bridgePlan.voice.Language) { $bridgePlan.voice.Language } else { $bridgeConfiguredLanguage }
        $bridgeName = $bridgePlan.voice.Name.Replace("`r",' ').Replace("`n",' ')
        if (-not $bridgePlan.voice.Ready) { $bridgeName += ' [待 V5 授权]' }
        $bridgeQuotedName = [Text.Json.JsonSerializer]::Serialize([object][string]$bridgeName, [string], [Text.Json.JsonSerializerOptions]::new())
        # JSON quoted strings are valid YAML scalars, including names with ':' or '#'.
        $bridgeYaml = "name: $bridgeQuotedName`nsinger_type: vocaloid`ntext_file_encoding: utf-8`ndefault_phonemizer: OpenUtau.Core.DefaultPhonemizer`n"
        [IO.Directory]::CreateDirectory($bridgePlan.path) | Out-Null
        [IO.File]::WriteAllText((Join-Path $bridgePlan.path 'character.txt'), "name=$bridgeName`nauthor=VOCALOID local engine bridge`n")
        [IO.File]::WriteAllText((Join-Path $bridgePlan.path 'character.yaml'), $bridgeYaml)
        [IO.File]::WriteAllText((Join-Path $bridgePlan.path 'vocaloid-voice.json'), [Text.Json.JsonSerializer]::Serialize([object]$bridgePlan.voice, [VocaloidBridge.BridgeVoice], [Text.Json.JsonSerializerOptions]@{ WriteIndented=$true }))
        [IO.File]::WriteAllText((Join-Path $bridgePlan.path 'vocaloid-bridge.json'), [Text.Json.JsonSerializer]::Serialize([object]$bridgeConfig, [VocaloidBridge.BridgeConfig], [Text.Json.JsonSerializerOptions]@{ WriteIndented=$true }))
        [ordered]@{ directory=$bridgePlan.path; id=$bridgePlan.voice.Id; name=$bridgeName; generation=$bridgePlan.voice.Generation; ready=$bridgePlan.voice.Ready } | ConvertTo-Json -Compress
    }
} finally { $bridgeSession.Dispose() }
