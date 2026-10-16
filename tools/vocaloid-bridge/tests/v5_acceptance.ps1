param(
    [string]$Config=(Join-Path $PSScriptRoot '../vocaloid5.example.json'),
    [string]$OutputWavPath=(Join-Path $PSScriptRoot '../test-output/v5-ning-ni-hao-a.wav'),
    [ValidateRange(1,32)][int]$EditCount=8
)
$ErrorActionPreference='Stop'
$configPath=[IO.Path]::GetFullPath($Config)
$settings=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if($settings.EngineType -cne 'VOCALOID5' -or -not $settings.VoiceId.StartsWith('v5:')) {
    throw 'This acceptance test requires a direct VOCALOID5 configuration and exact v5: voice ID.'
}
$reportText=& (Join-Path $PSScriptRoot 'native_render_probe.ps1') `
    -Config $configPath -EditCount $EditCount -OutputWavPath $OutputWavPath -RequestPath (Join-Path $PSScriptRoot '../request.vocaloid5.json')
$report=$reportText | ConvertFrom-Json
if($report.status -cne 'real-vocaloid-render-verified' -or $report.voice_id -cne $settings.VoiceId -or
   $report.peak -lt 0.0001 -or $report.consecutive_native_renders -ne ($EditCount+1)) {
    throw 'V5 failed real-audio, exact-voice, or consecutive-render validation.'
}
if(-not $report.all_consecutive_faster_than_playback) {
    throw "V5 synthesis exceeded the phrase playback duration on this machine: $($report.slowest_consecutive_render_seconds)s."
}
$report | Add-Member -NotePropertyName backend -NotePropertyValue 'VOCALOID5 x64 VSM'
$report | ConvertTo-Json -Depth 8
