[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Config,
    [Parameter(Mandatory=$true)][string]$Request
)
$ErrorActionPreference = 'Stop'
$scriptPath = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $scriptPath
if ([IntPtr]::Size -eq 8) {
    $wow = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $wow)) { throw '32-bit Windows PowerShell was not found under SysWOW64.' }
    & $wow -NoProfile -ExecutionPolicy Bypass -File $scriptPath -Config $Config -Request $Request
    exit $LASTEXITCODE
}
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkDir 'csc.exe'
$outputDir = Join-Path ([IO.Path]::GetTempPath()) 'openutau-vocaloid-stream-server'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$exe = Join-Path $outputDir 'StreamServer.exe'
$sources = @('stream/StreamServer.cs', 'FrameStream.cs', 'Contracts.cs', 'NativeApi.cs', 'NativeSynthesis.cs',
             'V4ScoreWriter.cs', 'VoiceCatalog.cs', 'NativeFrontend.cs', 'NativeChineseG2p.cs') |
    ForEach-Object { Join-Path $scriptDir $_ }
$compileArgs = @('/nologo', '/target:exe', '/platform:x86', '/langversion:5', '/optimize+',
                 '/main:VocaloidBridge.V4.StreamServer', ('/out:' + $exe),
                 '/reference:System.Web.Extensions.dll', '/reference:System.Runtime.Serialization.dll') + $sources
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw ('C# compilation failed with exit code ' + $LASTEXITCODE) }
& $exe $([IO.Path]::GetFullPath($Config)) $([IO.Path]::GetFullPath($Request))
exit $LASTEXITCODE
