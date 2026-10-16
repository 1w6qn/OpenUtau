param(
    [string]$OpenUtauRoot=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist/OpenUtau-Vocaloid-AutoPitch'),
    [string]$V3Wav=(Join-Path (Split-Path -Parent $PSScriptRoot) 'test-output/openutau-v3-demo.wav'),
    [string]$V5Wav=(Join-Path (Split-Path -Parent $PSScriptRoot) 'test-output/openutau-v5-demo.wav')
)
$ErrorActionPreference = 'Stop'
$bridgeRoot = Split-Path -Parent $PSScriptRoot
$OpenUtauRoot = [IO.Path]::GetFullPath($OpenUtauRoot)
foreach ($file in @('OpenUtau.Core.dll', 'worldline.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OpenUtauRoot $file) -PathType Leaf)) {
        throw "Missing OpenUtau dependency $file under $OpenUtauRoot"
    }
}

$resolver = @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
public static class OpenUtauPitchAssemblyResolver {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool SetDllDirectory(string path);
    static string directory;
    public static Assembly Load(string path) {
        directory = Path.GetFullPath(path);
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        if (!SetDllDirectory(directory)) throw new InvalidOperationException("SetDllDirectory failed: " + Marshal.GetLastWin32Error());
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            string dll = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(dll) ? context.LoadFromAssemblyPath(dll) : null;
        };
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "OpenUtau.Core.dll"));
    }
}
'@
Add-Type -TypeDefinition $resolver -Language CSharp
$references = @(
    (Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName) +
    @((Join-Path $OpenUtauRoot 'OpenUtau.Core.dll'),
      (Join-Path $OpenUtauRoot 'NAudio.Core.dll'),
      (Join-Path $OpenUtauRoot 'NAudio.dll'),
      (Join-Path $OpenUtauRoot 'YamlDotNet.dll'),
      (Join-Path $OpenUtauRoot 'Serilog.dll'))
)
$sources = @(
    (Join-Path $bridgeRoot 'OpenUtau/VocaloidPitchAnalyzer.cs'),
    (Join-Path $PSScriptRoot 'VocaloidPitchAnalyzerProbe.cs')
)
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions @('/nullable:enable','/nowarn:0436,1701')
[void][OpenUtauPitchAssemblyResolver]::Load($OpenUtauRoot)
[OpenUtau.Core.Vocaloid.Tests.VocaloidPitchAnalyzerProbe]::Run([IO.Path]::GetFullPath($V3Wav), [IO.Path]::GetFullPath($V5Wav))
