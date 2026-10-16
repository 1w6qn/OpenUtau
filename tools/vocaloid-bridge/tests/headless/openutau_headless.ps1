param(
    [Parameter(Mandatory = $true)][string]$AppDirectory,
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [string]$SingerPath = '',
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [string]$OutputPath = '',
    [switch]$Render
)

$ErrorActionPreference = 'Stop'
$app = [IO.Path]::GetFullPath($AppDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
$project = [IO.Path]::GetFullPath($ProjectPath)
$singers = if ($SingerPath) { [IO.Path]::GetFullPath($SingerPath) } else { Join-Path $app 'Singers' }
if (-not (Test-Path -LiteralPath (Join-Path $app 'OpenUtau.Core.dll') -PathType Leaf)) {
    throw "OpenUtau.Core.dll not found in app directory: $app"
}
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "USTX project not found: $project" }
if ($Render -and -not $OutputPath) { throw 'OutputPath is required when -Render is specified.' }
New-Item -ItemType Directory -Force -Path $data,(Join-Path $data 'Cache') | Out-Null
$oldPath = $env:PATH
$env:PATH = $app + [IO.Path]::PathSeparator + $env:PATH

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

public static class VocaloidBridgeHeadlessAssemblyLoader {
    static string directory;
    public static Assembly Load(string path) {
        directory = path;
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string dll = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(dll) ? context.LoadFromAssemblyPath(dll) : null;
        };
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "OpenUtau.Core.dll"));
    }
}
'@
$core = [VocaloidBridgeHeadlessAssemblyLoader]::Load($app)
$sources = Join-Path $PSScriptRoot 'OpenUtauHeadless.cs'
Add-Type -Path $sources
try {
    [OpenUtauBridgeHeadless]::Run($core, $app, $data, $singers, $project, $OutputPath, $Render.IsPresent)
} finally {
    $env:PATH = $oldPath
}
