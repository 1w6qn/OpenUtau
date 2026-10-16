[CmdletBinding()]
param(
    [string]$VocaloidRoot = 'D:\Program Files (x86)\Vocaloid4P',
    [switch]$CompileOnly
)

$ErrorActionPreference = 'Stop'
$scriptPath = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$scriptDir = Split-Path -Parent $scriptPath

# Use native x86 PowerShell so child tools and DLL dependency resolution are
# consistently 32-bit even when launched from a 64-bit console.
if ([IntPtr]::Size -eq 8) {
    $wowPowerShell = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $wowPowerShell)) {
        throw '32-bit Windows PowerShell was not found under SysWOW64.'
    }
    $forwardArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath, '-VocaloidRoot', $VocaloidRoot)
    if ($CompileOnly) { $forwardArgs += '-CompileOnly' }
    & $wowPowerShell @forwardArgs
    exit $LASTEXITCODE
}

$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkDir 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw " .NET Framework C# compiler not found: $compiler"
}
$outputDir = Join-Path ([IO.Path]::GetTempPath()) 'openutau-vocaloid-v4-host'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$exe = Join-Path $outputDir 'V4DiagnosticHost.exe'
$api = Join-Path $scriptDir 'NativeApi.cs'
& $compiler /nologo /target:exe /platform:x86 /langversion:5 /optimize+ /out:$exe /reference:System.Runtime.Serialization.dll $api
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
if ($CompileOnly) {
    Write-Output ('{"ok":true,"compiledHost":"' + $exe.Replace('\', '\\') + '","processBits":' + ([IntPtr]::Size * 8) + '}')
    exit 0
}
& $exe $VocaloidRoot
exit $LASTEXITCODE
