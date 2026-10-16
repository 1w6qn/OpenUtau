param(
    [string]$Config=(Join-Path $PSScriptRoot '../dist/OpenUtau-Vocaloid-V3V5/Singers/vocaloid-592506cf9dae29d0/vocaloid-bridge.json'),
    [string]$OutputWavPath='',
    [ValidateRange(1,32)][int]$EditCount=8
)
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path -Parent $PSScriptRoot
$configPath=[IO.Path]::GetFullPath($Config)
$engineConfig=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if($engineConfig.VoiceId -cne '4:0'){throw "Expected the installed legacy Tianyi V3 voice as catalog ID 4:0, got '$($engineConfig.VoiceId)'."}
if($engineConfig.EngineType -cne 'VOCALOID4'){throw 'V3 voices are currently rendered through the installed VOCALOID4 engine bridge.'}
$component='BETDB8W6KWZPYEB9'
$legacyVoice=Join-Path 'D:\Program Files (x86)\VoiceDB\Luo_Tianyi' $component
if(-not (Test-Path -LiteralPath $legacyVoice -PathType Container)) {
    throw "The installed V3 voice component was not found: $legacyVoice"
}
if([string]::IsNullOrWhiteSpace($OutputWavPath)) {
    $OutputWavPath=Join-Path $bridgeRoot 'test-output/v3-tianyi-ni-hao-a.wav'
}
$wavPath=[IO.Path]::GetFullPath($OutputWavPath)
$requestPath=Join-Path $bridgeRoot 'examples/vocaloid3-render-request.json'
$probe=Join-Path $PSScriptRoot 'native_render_probe.ps1'
$jsonText=& $probe -Config $configPath -EditCount $EditCount -RequestPath $requestPath -OutputWavPath $wavPath
$report=$jsonText | ConvertFrom-Json
if($report.status -cne 'real-vocaloid-render-verified' -or $report.voice_id -cne '4:0') {
    throw 'The native render probe did not report the expected legacy V3 voice.'
}
if($report.peak -lt 0.0001 -or $report.sample_rate -ne 44100) {
    throw 'The V3 backend returned silent or unexpected PCM.'
}
if($report.consecutive_native_renders -lt ($EditCount+1) -or -not $report.edited_faster_than_playback -or -not $report.all_consecutive_faster_than_playback) {
    throw 'The V3 backend did not complete the requested consecutive realtime renders.'
}
if(-not (Test-Path -LiteralPath $wavPath -PathType Leaf) -or (Get-Item -LiteralPath $wavPath).Length -le 44) {
    throw "The real V3 WAV was not created: $wavPath"
}
$header=[byte[]]::new(12)
$stream=[IO.File]::OpenRead($wavPath)
try {$null=$stream.Read($header,0,$header.Length)} finally {$stream.Dispose()}
if([Text.Encoding]::ASCII.GetString($header,0,4) -cne 'RIFF' -or [Text.Encoding]::ASCII.GetString($header,8,4) -cne 'WAVE') {
    throw 'The exported V3 audio is not a valid RIFF/WAVE file.'
}
[ordered]@{
    status='real-vocaloid3-bank-render-verified'
    voice='Tianyi_CHN'
    voice_id='4:0'
    component_id=$component
    source_voicebank=$legacyVoice
    sample_rate=$report.sample_rate
    sample_count=$report.sample_count
    peak=$report.peak
    consecutive_native_renders=$report.consecutive_native_renders
    first_render_seconds=$report.first_render_seconds
    edited_render_seconds=$report.edited_render_seconds
    edited_realtime_factor=$report.edited_realtime_factor
    slowest_consecutive_render_seconds=$report.slowest_consecutive_render_seconds
    all_consecutive_faster_than_playback=$report.all_consecutive_faster_than_playback
    wav_path=$wavPath
} | ConvertTo-Json
