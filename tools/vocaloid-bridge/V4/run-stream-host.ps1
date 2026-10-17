[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Config,
    [Parameter(Mandatory=$true)][string]$Request,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$StartSeconds = '0',
    [string]$Seconds = '1',
    [switch]$Pace
)
$ErrorActionPreference = 'Stop'
$scriptPath = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $scriptPath

# The VOCALOID 4 ABI is x86 only; re-dispatch through 32-bit Windows PowerShell.
if ([IntPtr]::Size -eq 8) {
    $wow = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $wow)) { throw '32-bit Windows PowerShell was not found under SysWOW64.' }
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath,
                 '-Config', $Config, '-Request', $Request, '-Output', $Output,
                 '-StartSeconds', $StartSeconds, '-Seconds', $Seconds)
    if ($Pace) { $forward += '-Pace' }
    & $wow @forward
    exit $LASTEXITCODE
}

$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkDir 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw (' .NET Framework C# compiler not found: ' + $compiler) }
$outputDir = Join-Path ([IO.Path]::GetTempPath()) 'openutau-vocaloid-stream-host'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$exe = Join-Path $outputDir 'StreamHost.exe'
$sources = @('stream/StreamHost.cs', 'Contracts.cs', 'NativeApi.cs', 'NativeSynthesis.cs',
             'V4ScoreWriter.cs', 'VoiceCatalog.cs', 'NativeFrontend.cs', 'NativeChineseG2p.cs') |
    ForEach-Object { Join-Path $scriptDir $_ }
$compileArgs = @('/nologo', '/target:exe', '/platform:x86', '/langversion:5', '/optimize+',
                 '/main:VocaloidBridge.V4.StreamHost', ('/out:' + $exe),
                 '/reference:System.Web.Extensions.dll', '/reference:System.Runtime.Serialization.dll') + $sources
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw ('C# compilation failed with exit code ' + $LASTEXITCODE) }

$target = [IO.Path]::GetFullPath($Output)
$runArgs = @($([IO.Path]::GetFullPath($Config)), $([IO.Path]::GetFullPath($Request)),
             $StartSeconds, $Seconds, $target)
if ($Pace) { $runArgs += 'pace' }
& $exe @runArgs
exit $LASTEXITCODE
