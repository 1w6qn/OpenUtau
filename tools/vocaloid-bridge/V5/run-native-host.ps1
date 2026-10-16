param([Parameter(Mandatory=$true)][string]$Config)
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 8 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'VOCALOID 5 direct host requires 64-bit Windows PowerShell.'
}
$scriptDir = $PSScriptRoot
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll')
$sources = @(Get-ChildItem -LiteralPath $scriptDir -Filter '*.cs' | ForEach-Object FullName)
Add-Type -Path $sources -ReferencedAssemblies $references
[VocaloidBridge.V5.NativeHost]::Run([IO.Path]::GetFullPath($Config))
