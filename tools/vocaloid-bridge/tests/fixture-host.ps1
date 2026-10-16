param([string]$Config)
$ErrorActionPreference = 'Stop'
$bridgeRoot = Split-Path -Parent $PSScriptRoot
Add-Type -Path @((Join-Path $bridgeRoot 'Core/Contracts.cs'), (Join-Path $bridgeRoot 'Core/BridgeSession.cs'), (Join-Path $PSScriptRoot 'FixtureHost.cs')) -CompilerOptions '/nullable:enable'
[FixtureHost]::Run()
