param(
    [string]$AppDirectory=(Join-Path $PSScriptRoot '../dist/OpenUtau-V5-AutoPitch'),
    [string]$SingerPath='',
    [string]$DataDirectory='',
    [string]$OutputPath=(Join-Path $PSScriptRoot '../test-output/openutau-v5-demo.wav')
)
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path -Parent $PSScriptRoot
$app=[IO.Path]::GetFullPath($AppDirectory)
$singers=if($SingerPath){[IO.Path]::GetFullPath($SingerPath)}else{Join-Path $app 'Singers'}
$outputRoot=Join-Path $bridgeRoot 'test-output'
$wav=[IO.Path]::GetFullPath($OutputPath)
$data=if($DataDirectory){[IO.Path]::GetFullPath($DataDirectory)}else{Join-Path $outputRoot ('openutau-v5-data/'+[Guid]::NewGuid().ToString('N'))}
foreach($id in @('vocaloid-87a9101ae1158b84','vocaloid-1ff1d78aa4a2f769','vocaloid-83afe545637566ab')) {
    if(-not (Test-Path -LiteralPath (Join-Path $singers "$id/vocaloid-bridge.json") -PathType Leaf)) {
        throw "Missing V5 proxy singer $id; register the three installed banks first."
    }
}
$oldStems=$env:SVS_STEM_DIR
$oldDump=$env:SVS_DUMP_PHRASES
$oldPythonEncoding=$env:PYTHONIOENCODING
try {
    $env:PYTHONIOENCODING='utf-8'
    $env:SVS_STEM_DIR=Join-Path $outputRoot 'openutau-v5-stems'
    $env:SVS_DUMP_PHRASES=Join-Path $outputRoot 'openutau-v5-phrases.json'
    & (Join-Path $PSScriptRoot 'headless/openutau_headless.ps1') `
        -AppDirectory $app -DataDirectory $data -SingerPath $singers `
        -ProjectPath (Join-Path $bridgeRoot 'examples/vocaloid5-demo.ustx') -OutputPath $wav -Render
    if(-not (Test-Path -LiteralPath $wav -PathType Leaf)){throw 'OpenUtau did not create the V5 demo WAV.'}
    & python (Join-Path $PSScriptRoot 'analyze_v5_openutau_render.py') $wav (Join-Path $outputRoot 'openutau-v5-acceptance.json')
    if($LASTEXITCODE -ne 0){throw "Real V5 audio validation failed (exit $LASTEXITCODE)."}
} finally {
    $env:SVS_STEM_DIR=$oldStems
    $env:SVS_DUMP_PHRASES=$oldDump
    $env:PYTHONIOENCODING=$oldPythonEncoding
}
