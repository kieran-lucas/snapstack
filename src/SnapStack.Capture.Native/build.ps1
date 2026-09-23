param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) {
    throw 'Visual Studio C++ Build Tools are required.'
}

$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvarsall.bat'
$target = if ($Architecture -eq 'ARM64') { 'x64_arm64' } else { 'x64' }
$runtime = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$outputDirectory = Join-Path $PSScriptRoot "bin\$runtime"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$source = Join-Path $PSScriptRoot 'CaptureCore.cpp'
$dll = Join-Path $outputDirectory 'SnapStackCaptureNative.dll'
$object = Join-Path $outputDirectory 'CaptureCore.obj'

$compile = 'call "{0}" {1} >nul && cl /nologo /LD /EHsc /std:c++20 /O2 /W4 /WX /MT "{2}" /Fo:"{3}" /Fe:"{4}"' -f `
    $vcvars, $target, $source, $object, $dll
& cmd.exe /c $compile
if ($LASTEXITCODE -ne 0) {
    throw "Native capture core compilation failed with exit code $LASTEXITCODE."
}
Write-Host "Built $dll"
