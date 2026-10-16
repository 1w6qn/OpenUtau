param(
    [string]$ProjectPath=(Join-Path $PSScriptRoot '../examples/vocaloid3-demo.ustx'),
    [string]$OutputPath=(Join-Path (Split-Path -Parent $PSScriptRoot) 'test-output/openutau-v3-demo.wav'),
    [string]$AppDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist/OpenUtau-Vocaloid-V3V5'),
    [string]$DataDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'test-output/openutau-v3-data'),
    [string]$SingerPath=''
)
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path -Parent $PSScriptRoot
$rootOutput=[IO.Path]::GetFullPath((Join-Path $bridgeRoot 'test-output'))
$project=[IO.Path]::GetFullPath($ProjectPath)
$wav=[IO.Path]::GetFullPath($OutputPath)
$app=[IO.Path]::GetFullPath($AppDirectory)
$data=[IO.Path]::GetFullPath($DataDirectory)
$singers=if($SingerPath){[IO.Path]::GetFullPath($SingerPath)}else{Join-Path $app 'Singers'}
foreach($path in @($wav,$data)) {
    if(-not $path.StartsWith($rootOutput+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Output/data directory must stay inside bridge test-output: $path"
    }
}
if(-not (Test-Path -LiteralPath $project -PathType Leaf)){throw "USTX example not found: $project"}
if(-not (Test-Path -LiteralPath $app -PathType Container)){throw "OpenUtau app directory not found: $app"}
if(-not (Test-Path -LiteralPath (Join-Path $singers 'vocaloid-592506cf9dae29d0/character.yaml') -PathType Leaf)) {
    throw 'The stable Tianyi_CHN proxy singer is missing from the packaged OpenUtau dist.'
}
$stemDirectory=Join-Path $rootOutput 'openutau-v3-stems'
$phraseDump=Join-Path $rootOutput 'openutau-v3-phrases.json'
$acceptance=Join-Path $rootOutput 'openutau-v3-acceptance.json'
$bootstrap=Join-Path $PSScriptRoot 'headless/openutau_headless.ps1'
$analyzer=Join-Path $PSScriptRoot 'analyze_v3_openutau_render.py'
$oldStem=$env:SVS_STEM_DIR
$oldDump=$env:SVS_DUMP_PHRASES
$oldPythonEncoding=$env:PYTHONIOENCODING
try {
    $env:PYTHONIOENCODING='utf-8'
    $env:SVS_STEM_DIR=$stemDirectory
    $env:SVS_DUMP_PHRASES=$phraseDump
    & $bootstrap -ProjectPath $project -OutputPath $wav -Render -AppDirectory $app -DataDirectory $data -SingerPath $singers
    if(-not (Test-Path -LiteralPath $wav -PathType Leaf)){throw 'OpenUtau headless pipeline did not create the output WAV.'}
    if(-not (Test-Path -LiteralPath $phraseDump -PathType Leaf)){throw 'OpenUtau headless pipeline did not dump prepared phrase data.'}
    $pythonOutput=& python $analyzer $wav $phraseDump $acceptance
    if($LASTEXITCODE -ne 0){throw "V3 WAV analysis failed with exit code $LASTEXITCODE."}
    $result=$pythonOutput | ConvertFrom-Json
    if($result.sample_rate -ne 44100 -or $result.peak -lt 0.0001){throw 'OpenUtau returned silent or unexpected-rate output.'}
    if($result.first_audible_seconds -lt 0.8 -or $result.first_audible_seconds -gt 1.4) {
        throw "Unexpected first audible onset $($result.first_audible_seconds)s; expected near 1.0s."
    }
    if($result.duration_seconds -lt 3.0 -or $result.duration_seconds -gt 6.0) {
        throw "Unexpected project render duration $($result.duration_seconds)s."
    }
    $pitchChecks=@($result.expected_notes | Where-Object {$null -eq $_.measured_midi -or [Math]::Abs($_.pitch_error_semitones) -gt 0.5})
    if($pitchChecks.Count -gt 0){throw 'At least one sustained note pitch could not be measured within half a semitone of the USTX target.'}
    Write-Output $pythonOutput
} finally {
    $env:SVS_STEM_DIR=$oldStem
    $env:SVS_DUMP_PHRASES=$oldDump
    $env:PYTHONIOENCODING=$oldPythonEncoding
}
