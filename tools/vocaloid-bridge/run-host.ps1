param([Parameter(Mandatory=$true)][string]$Config)
$ErrorActionPreference = 'Stop'
# PowerShell 7.4+ supplies Roslyn and .NET 8+. No SDK/NuGet is needed for the host.
if ($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 8) {
    throw 'The bridge host requires PowerShell 7.4 or later (.NET 8+).'
}
$bridgeSources = @(
    (Join-Path $PSScriptRoot 'Core/Contracts.cs'),
    (Join-Path $PSScriptRoot 'Core/BridgeSession.cs'),
    (Join-Path $PSScriptRoot 'Host/LegacyTuneLabBackend.cs'),
    (Join-Path $PSScriptRoot 'Host/EngineHost.cs')
)
Add-Type -Path $bridgeSources -CompilerOptions '/nullable:enable' -ErrorAction Stop
[VocaloidBridge.EngineHost]::Run([IO.Path]::GetFullPath($Config))
