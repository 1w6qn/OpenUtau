param(
    [ValidateSet('doctor','voices','render','import')][string]$Operation = 'doctor',
    [Parameter(Mandatory=$true)][string]$Config,
    [string]$Request = '',
    [string]$Output = '',
    [string]$VoiceDirectory = ''
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8) {
    throw 'Use PowerShell 7.4+ (pwsh), not Windows PowerShell 5.'
}
Add-Type -Path @((Join-Path $PSScriptRoot 'Core/Contracts.cs'), (Join-Path $PSScriptRoot 'Core/BridgeSession.cs')) -CompilerOptions '/nullable:enable'
$bridgeSession = $null
try {
    $bridgePath = [IO.Path]::GetFullPath($Config)
    $bridgeConfig = [VocaloidBridge.BridgeConfig]::Load($bridgePath)
    $bridgeSession = [VocaloidBridge.BridgeSession]::Get($bridgePath)
    $bridgeWatch = [Diagnostics.Stopwatch]::StartNew()
    $bridgeVoices = $bridgeSession.ListVoicesAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($Operation -eq 'import') {
        if (-not $VoiceDirectory) { throw 'import requires -VoiceDirectory <installed-voicebank-folder>.' }
        $bridgeImported=$bridgeSession.ImportVoicesAsync([IO.Path]::GetFullPath($VoiceDirectory),[Threading.CancellationToken]::None).GetAwaiter().GetResult()
        @{status='imported';voices=$bridgeImported;ready_count=@($bridgeImported | Where-Object Ready).Count;real_voice_render_verified=$false} | ConvertTo-Json -Depth 6
    } elseif ($Operation -in @('doctor','voices')) {
        [ordered]@{
            status = 'backend-ready'
            engine_assembly = $bridgeConfig.EngineAssembly
            engine_type = $bridgeConfig.EngineType
            voices = $bridgeVoices
            selected_voice_found = [bool](@($bridgeVoices | Where-Object Id -CEQ $bridgeConfig.VoiceId).Count)
            real_voice_render_verified = $false
            note = 'Voice enumeration passed. Render a real request to verify synthesis; OpenUtau source integration is separately required.'
        } | ConvertTo-Json -Depth 6
    } else {
        if (-not $Request -or -not $Output) { throw 'render requires -Request request.json and -Output new-file.wav.' }
        $bridgeOutput = [IO.Path]::GetFullPath($Output)
        if (Test-Path -LiteralPath $bridgeOutput) { throw 'Output already exists; choose a new output filename.' }
        $bridgeRequest = [Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText([IO.Path]::GetFullPath($Request)), [VocaloidBridge.BridgeRequest])
        if (-not $bridgeRequest.VoiceId) { $bridgeRequest.VoiceId = $bridgeConfig.VoiceId }
        $bridgeAudio = $bridgeSession.RenderAsync($bridgeRequest, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $bridgeWatch.Stop()
        # IEEE float, mono RIFF/WAVE. Keep the full engine output; StartSeconds is reported below.
        $bridgeFile = [IO.File]::Open($bridgeOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        $bridgeWriter = [IO.BinaryWriter]::new($bridgeFile)
        try {
            $bridgeSize = [int]($bridgeAudio.Samples.Length * 4)
            $bridgeWriter.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
            $bridgeWriter.Write([int](36 + $bridgeSize))
            $bridgeWriter.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
            $bridgeWriter.Write([int]16)
            $bridgeWriter.Write([short]3)
            $bridgeWriter.Write([short]1)
            $bridgeWriter.Write([int]$bridgeAudio.SampleRate)
            $bridgeWriter.Write([int]($bridgeAudio.SampleRate * 4))
            $bridgeWriter.Write([short]4)
            $bridgeWriter.Write([short]32)
            $bridgeWriter.Write([Text.Encoding]::ASCII.GetBytes('data'))
            $bridgeWriter.Write($bridgeSize)
            $bridgePcm = [byte[]]::new($bridgeSize)
            [Buffer]::BlockCopy($bridgeAudio.Samples, 0, $bridgePcm, 0, $bridgeSize)
            $bridgeWriter.Write($bridgePcm)
        } finally { $bridgeWriter.Dispose() }
        [ordered]@{
            status='rendered'; output=$bridgeOutput; voice_id=$bridgeRequest.VoiceId
            start_seconds=$bridgeAudio.StartSeconds; sample_rate=$bridgeAudio.SampleRate
            audio_seconds=($bridgeAudio.Samples.Length / $bridgeAudio.SampleRate)
            elapsed_seconds=$bridgeWatch.Elapsed.TotalSeconds
            phrase_seconds=($bridgeRequest.Notes[-1].EndSeconds - $bridgeRequest.Notes[0].StartSeconds)
            realtime_factor=($bridgeWatch.Elapsed.TotalSeconds / ($bridgeRequest.Notes[-1].EndSeconds - $bridgeRequest.Notes[0].StartSeconds))
        } | ConvertTo-Json
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.GetBaseException().Message)
    exit 2
} finally {
    if ($null -ne $bridgeSession) { $bridgeSession.Dispose() }
}
