param([string]$Scratch = '')
$ErrorActionPreference = 'Stop'
$bridgeRoot = Split-Path -Parent $PSScriptRoot
if (-not $Scratch) { $Scratch = Join-Path $bridgeRoot 'test-output/core' }
Add-Type -Path @((Join-Path $bridgeRoot 'Core/Contracts.cs'), (Join-Path $bridgeRoot 'Core/BridgeSession.cs'), (Join-Path $PSScriptRoot 'CoreProbe.cs')) -CompilerOptions '/nullable:enable'
$bridgeCount = [CoreProbe]::Run($bridgeRoot, [IO.Path]::GetFullPath($Scratch), (Get-Process -Id $PID).Path)
Write-Output "Core/transport checks passed: $bridgeCount (synthetic audio only; no real VOCALOID voice tested)."
