param(
    [ValidateSet('V3','V4','V5')][string]$Generation='V5',
    [string]$AppDirectory='',
    [string]$SingerPath='',
    [string]$DataDirectory=''
)
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path -Parent $PSScriptRoot
if (-not $AppDirectory) {
    $AppDirectory=Join-Path $bridgeRoot $(if($Generation -eq 'V5'){'dist/OpenUtau-V5-AutoPitch'}else{'dist/OpenUtau-Vocaloid-AutoPitch'})
}
$app=[IO.Path]::GetFullPath($AppDirectory)
$outputRoot=Join-Path $bridgeRoot 'test-output'
$label=$Generation.ToLowerInvariant()
$data=if($DataDirectory){[IO.Path]::GetFullPath($DataDirectory)}else{Join-Path $outputRoot ('pitch-'+$label+'-data/'+[Guid]::NewGuid().ToString('N'))}
& (Join-Path $PSScriptRoot 'headless/openutau_headless.ps1') -AppDirectory $app -DataDirectory $data `
    -SingerPath $(if($SingerPath){[IO.Path]::GetFullPath($SingerPath)}else{Join-Path $app 'Singers'}) `
    -ProjectPath (Join-Path $bridgeRoot "examples/vocaloid$($label.Substring(1))-demo.ustx") `
    -OutputPath (Join-Path $outputRoot "autopitch-$label-demo.wav") -Render

$refs=@((Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)+(Join-Path $app 'OpenUtau.Core.dll'))
Add-Type -Path (Join-Path $PSScriptRoot 'VocaloidPitchPipelineProbe.cs') -ReferencedAssemblies $refs -CompilerOptions '/nullable:enable'
$count=[VocaloidPitchPipelineProbe]::Run($Generation,(Join-Path $outputRoot "autopitch-$label-acceptance.json"))
Write-Output "PASS: $count actual $Generation phrases automatically analyzed and cached."
