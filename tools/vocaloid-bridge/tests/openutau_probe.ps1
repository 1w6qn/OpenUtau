param(
    [string]$OpenUtauRoot = 'D:\Program Files\OpenUtau',
    [string]$FixtureRoot = ''
)
$ErrorActionPreference = 'Stop'
$bridgeRoot = Split-Path -Parent $PSScriptRoot
$userProvidedFixtureRoot = -not [string]::IsNullOrWhiteSpace($FixtureRoot)
$FixtureRoot = if ($userProvidedFixtureRoot) { [IO.Path]::GetFullPath($FixtureRoot) } else { '' }
$OpenUtauRoot = [IO.Path]::GetFullPath($OpenUtauRoot)
$bridgeTestOutput = Join-Path $bridgeRoot 'test-output'
$testConfig = Join-Path $bridgeTestOutput 'core/test-config.json'
if (-not $userProvidedFixtureRoot -and -not (Test-Path -LiteralPath $testConfig)) {
    throw "Missing $testConfig. Run tests/run.ps1 first to create the synthetic-host config, then rerun this probe."
}
if (-not $userProvidedFixtureRoot) {
    $FixtureRoot = Join-Path $bridgeTestOutput ('openutau-singers/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $FixtureRoot | Out-Null
    & (Join-Path $PSHOME 'pwsh.exe') -NoLogo -NoProfile -File (Join-Path $bridgeRoot 'register-singers.ps1') `
        -Config $testConfig -SingerDirectory $FixtureRoot -VoiceId 'fixture' | Out-Null
}
$fixture = Get-ChildItem -LiteralPath $FixtureRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'character.yaml') } | Select-Object -First 1
if (-not $fixture) { throw "No generated Vocaloid registration fixture found under $FixtureRoot" }

# Build all bridge and OpenUtau integration sources against the installed, matching Core API.
$refs = @(
    (Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName) +
    @((Join-Path $OpenUtauRoot 'OpenUtau.Core.dll'),
      (Join-Path $OpenUtauRoot 'NAudio.Core.dll'),
      (Join-Path $OpenUtauRoot 'NAudio.dll'),
      (Join-Path $OpenUtauRoot 'YamlDotNet.dll'),
      (Join-Path $OpenUtauRoot 'Serilog.dll'))
)
$sources = @(
    (Get-ChildItem -LiteralPath (Join-Path $bridgeRoot 'Core') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName) +
    (Get-ChildItem -LiteralPath (Join-Path $bridgeRoot 'OpenUtau') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName) +
    (Join-Path $PSScriptRoot 'OpenUtauRenderProbe.cs')
)
Add-Type -Path $sources -ReferencedAssemblies $refs -CompilerOptions @('/nullable:enable', '/nowarn:1701')
[OpenUtauAssemblyResolver]::Attach($OpenUtauRoot)
[void][System.Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath((Join-Path $OpenUtauRoot 'OpenUtau.Core.dll'))

# The installed Core is deliberately unpatched. Apply the same mutable name-map entries that
# integrate.py adds in a source build, then exercise its real character.yaml/character.txt reader.
$vocaloidType = [Enum]::ToObject([OpenUtau.Core.Ustx.USingerType], 7)
[OpenUtau.Core.Ustx.SingerTypeUtils]::SingerTypeFromName['vocaloid'] = $vocaloidType
[OpenUtau.Core.Ustx.SingerTypeUtils]::SingerTypeNames[$vocaloidType] = 'vocaloid'
$loader = [OpenUtau.Classic.VoicebankLoader]::new($FixtureRoot)
$banks = @($loader.SearchAll())
$bank = $banks | Where-Object { [int]$_.SingerType -eq 7 } | Select-Object -First 1
if (-not $bank) { throw 'VoicebankLoader did not recognize singer_type: vocaloid as value 0x7.' }
$singer = [OpenUtau.Core.Vocaloid.VocaloidSinger]::new($bank)
$singer.EnsureLoaded()
if (-not $singer.Loaded) { throw "VocaloidSinger failed to load: $($singer.Errors -join '; ')" }
$oto = $null
if (-not $singer.TryGetOto('a', [ref]$oto) -or $null -eq $oto) { throw 'VocaloidSinger.TryGetOto did not return a dummy oto.' }
if ([int]$singer.SingerType -ne 7) { throw "SingerType was $([int]$singer.SingerType), expected 7." }
if ($singer.DefaultPhonemizer -ne 'OpenUtau.Core.DefaultPhonemizer') { throw "Unexpected default phonemizer: $($singer.DefaultPhonemizer)" }
Write-Output "PASS: compiled all bridge/Core integration sources against $OpenUtauRoot\OpenUtau.Core.dll"
Write-Output "PASS: VoicebankLoader recognized $($bank.Name); SingerType=7, Loaded=$($singer.Loaded), dummy oto and DefaultPhonemizer verified"
$renderChecks = [OpenUtauRenderProbe]::Run($singer, (Join-Path $fixture.FullName 'vocaloid-bridge.json'))
Write-Output "OpenUtau renderer checks passed: $renderChecks (synthetic PCM fixture, no VOCALOID voice tested)."
