param(
    [string]$Config = (Join-Path $PSScriptRoot '../vocaloid5.example.json'),
    [string]$OutputDirectory = '',
    [ValidateRange(1,32)][int]$EditCount = 2
)
$ErrorActionPreference = 'Stop'
$bridgeRoot = Split-Path -Parent $PSScriptRoot
$configPath = [IO.Path]::GetFullPath($Config)
$outputRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $bridgeRoot ('test-output/v5-legacy-acceptance/' + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

Add-Type -Path @((Join-Path $bridgeRoot 'Core/Contracts.cs'), (Join-Path $bridgeRoot 'Core/BridgeSession.cs')) `
    -CompilerOptions '/nullable:enable'
$settings = [VocaloidBridge.BridgeConfig]::Load($configPath)
if ($settings.EngineType -cne 'VOCALOID5') { throw 'This test requires EngineType=VOCALOID5; V4 fallback is forbidden.' }
if (-not (Test-Path -LiteralPath $settings.EngineAssembly -PathType Leaf)) { throw "V5 engine assembly missing: $($settings.EngineAssembly)" }

# Query the installed V5 engine's own catalog. Never synthesize a guessed ID or route to V4.
$catalogSession = [VocaloidBridge.BridgeSession]::Get($configPath)
try {
    $catalog = @($catalogSession.ListVoicesAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult())
} finally { $catalogSession.Dispose() }
$legacyVoices = @($catalog | Where-Object { $_.Generation -eq 3 -or $_.Generation -eq 4 } | Sort-Object Generation,Id)
$generation3 = @($legacyVoices | Where-Object Generation -EQ 3).Count
$generation4 = @($legacyVoices | Where-Object Generation -EQ 4).Count
if ($generation3 -eq 0 -or $generation4 -eq 0) {
    throw "V5 catalog must enumerate both Generation 3 and 4 banks; found G3=$generation3, G4=$generation4."
}

$requestPath = Join-Path $bridgeRoot 'request.vocaloid5.json'
if (-not (Test-Path -LiteralPath $requestPath -PathType Leaf)) { throw "V5 acceptance request is missing: $requestPath" }
$probeScript = Join-Path $PSScriptRoot 'native_render_probe.ps1'
$pwshPath = Join-Path $PSHOME 'pwsh.exe'
$results = [Collections.Generic.List[object]]::new()
foreach ($voice in $legacyVoices) {
    $voiceStem = ('g{0}-{1}' -f $voice.Generation, ([string]$voice.Id -replace '[^A-Za-z0-9._-]', '_'))
    $voiceDir = Join-Path $outputRoot $voiceStem
    New-Item -ItemType Directory -Force -Path $voiceDir | Out-Null
    $voiceConfig = Join-Path $voiceDir 'vocaloid5.json'
    $voiceSettings = [VocaloidBridge.BridgeConfig]::new()
    foreach ($property in $settings.GetType().GetProperties([Reflection.BindingFlags]'Public,Instance')) {
        if ($property.CanRead -and $property.CanWrite) { $property.SetValue($voiceSettings, $property.GetValue($settings)) }
    }
    $voiceSettings.VoiceId = [string]$voice.Id
    # Use the configured VOCALOID 5 engine and host, with absolute paths from the source config.
    $voiceSettings.HostScript = $settings.HostScript
    $voiceSettings.EngineAssembly = $settings.EngineAssembly
    $voiceSettings.EngineDirectory = $settings.EngineDirectory
    [IO.File]::WriteAllText($voiceConfig, [Text.Json.JsonSerializer]::Serialize($voiceSettings,
        [Text.Json.JsonSerializerOptions]@{ WriteIndented = $true }))

    $wavPath = Join-Path $voiceDir 'render.wav'
    $stdoutPath = Join-Path $voiceDir 'probe.stdout.txt'
    $stderrPath = Join-Path $voiceDir 'probe.stderr.txt'
    $probeError = ''
    $probe = $null
    try {
        $start = [Diagnostics.ProcessStartInfo]::new($pwshPath)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$probeScript,
                '-Config',$voiceConfig,'-RequestPath',$requestPath,'-OutputWavPath',$wavPath,'-EditCount',[string]$EditCount)) {
            $start.ArgumentList.Add([string]$argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Could not start the isolated native render probe.' }
        $readOut = $process.StandardOutput.ReadToEndAsync()
        $readErr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $readOut.GetAwaiter().GetResult()
        $stderr = $readErr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($stdoutPath, $stdout)
        [IO.File]::WriteAllText($stderrPath, $stderr)
        if ($process.ExitCode -ne 0) {
            $probeError = "native_render_probe exited $($process.ExitCode): $($stderr.Trim()) $($stdout.Trim())".Trim()
        } else {
            try { $probe = $stdout | ConvertFrom-Json -ErrorAction Stop }
            catch { $probeError = "Probe did not return valid JSON: $($_.Exception.Message)" }
        }
        $process.Dispose()
    } catch {
        $probeError = $_.Exception.Message
    }

    $ok = $false
    if (-not $probeError -and $probe) {
        $ok = $probe.status -ceq 'real-vocaloid-render-verified' -and
            $probe.voice_id -ceq $voice.Id -and $probe.sample_rate -eq 44100 -and
            $probe.sample_count -gt 0 -and $probe.peak -ge 0.0001 -and
            $probe.consecutive_native_renders -eq ($EditCount + 1)
        if (-not $ok) { $probeError = 'Probe completed without meeting exact-voice, non-silent PCM, sample-rate, or consecutive-edit assertions.' }
    }
    $results.Add([ordered]@{
        generation = $voice.Generation
        voice_id = $voice.Id
        name = $voice.Name
        ready = $voice.Ready
        engine = 'VOCALOID5'
        status = $(if ($ok) { 'passed' } else { 'failed' })
        sample_rate = $(if ($probe) { $probe.sample_rate } else { $null })
        sample_count = $(if ($probe) { $probe.sample_count } else { $null })
        peak = $(if ($probe) { $probe.peak } else { $null })
        phrase_seconds = $(if ($probe) { $probe.phrase_seconds } else { $null })
        edited_render_seconds = $(if ($probe) { $probe.edited_render_seconds } else { $null })
        slowest_consecutive_render_seconds = $(if ($probe) { $probe.slowest_consecutive_render_seconds } else { $null })
        consecutive_native_renders = $(if ($probe) { $probe.consecutive_native_renders } else { 0 })
        output_wav = $wavPath
        error = $probeError
    })
}

$failed = @($results | Where-Object status -NE 'passed').Count
$report = [ordered]@{
    status = $(if ($failed -eq 0) { 'v5-legacy-voice-render-verified' } else { 'v5-legacy-voice-render-failed' })
    engine = 'VOCALOID5'
    config = $configPath
    output_directory = $outputRoot
    catalog_legacy_count = $legacyVoices.Count
    generation3_count = $generation3
    generation4_count = $generation4
    edit_count = $EditCount
    failed_count = $failed
    results = @($results.ToArray())
    tested_utc = [DateTime]::UtcNow.ToString('o')
}
$reportPath = Join-Path $outputRoot 'acceptance.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 10))
Write-Output ($report | ConvertTo-Json -Depth 10)
if ($failed -gt 0) { exit 1 }
