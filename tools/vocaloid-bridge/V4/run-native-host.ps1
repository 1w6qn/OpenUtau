param([Parameter(Mandatory=$true)][string]$Config)
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 4 -or $PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Use SysWOW64\WindowsPowerShell\v1.0\powershell.exe for the direct V4 host.'
}
$v4References = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Xml.Linq.dll')
Add-Type -Path @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object FullName) -ReferencedAssemblies $v4References
[VocaloidBridge.V4.NativeHost]::Run([IO.Path]::GetFullPath($Config))
