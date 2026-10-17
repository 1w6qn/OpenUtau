param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$AppDirectory = 'D:\Program Files\OpenUtau',
    [string]$DataDirectory = ''
)
# Imports a VOCALOID .vsqx with OpenUtau's own reader and saves the resulting
# .ustx, so "what OpenUtau produces from this VSQX" can be measured without
# opening the GUI. Data is written to a scratch directory; user prefs are not
# modified. Mirrors scripts/openutau_bootstrap.ps1.
$ErrorActionPreference = 'Stop'
$appDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
$repoRoot = Split-Path -Parent $PSScriptRoot
$headlessData = if ($DataDirectory) { [IO.Path]::GetFullPath($DataDirectory) } else { Join-Path $repoRoot 'tmp\openutau-vsqx-import' }
New-Item -ItemType Directory -Force -Path $headlessData | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $headlessData 'Cache') | Out-Null
$env:PATH = $appDirectory + [IO.Path]::PathSeparator + $env:PATH
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
public static class OpenUtauVsqxLoader {
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
$core = [OpenUtauVsqxLoader]::Load($appDirectory)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance,FlattenHierarchy'
$pathType = $core.GetType('OpenUtau.Core.PathManager', $true)
$pathManager = $pathType.GetProperty('Inst', $flags).GetValue($null)
foreach ($setting in @{ RootPath = $appDirectory; DataPath = $headlessData; CachePath = (Join-Path $headlessData 'Cache') }.GetEnumerator()) {
    $pathType.GetProperty($setting.Key, $flags).SetValue($pathManager, $setting.Value)
}
$vsqxType = $core.GetType('OpenUtau.Core.Format.VSQx', $true)
$ustxType = $core.GetType('OpenUtau.Core.Format.Ustx', $true)
$source = (Resolve-Path -LiteralPath $Source).Path
$project = $vsqxType.GetMethod('Load', $flags).Invoke($null, @($source))
if ($null -eq $project) { throw 'OpenUtau VSQx.Load returned null' }
$ustxType.GetMethod('Save', $flags).Invoke($null, @([IO.Path]::GetFullPath($Output), $project))
function GetMember($object, $name) {
    $type = $object.GetType()
    $field = $type.GetField($name, $flags)
    if ($field) { return $field.GetValue($object) }
    $property = $type.GetProperty($name, $flags)
    if ($property) { return $property.GetValue($object) }
    throw "Member not found: $name on $($type.FullName)"
}
# Counting project members through PowerShell would force PSObject adaptation of
# OpenUtau types whose members differ only by case (CLS-noncompliant). The saved
# .ustx is the artefact; measure it with scripts/effect_align.py instead.
$saved = [IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath $saved)) { throw "OpenUtau did not write $saved" }
"IMPORTED $saved $((Get-Item -LiteralPath $saved).Length) bytes"
