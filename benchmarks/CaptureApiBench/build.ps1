param(
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) {
    throw 'Visual Studio C++ Build Tools are required.'
}

$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Include'
$cppWinRt = Get-ChildItem -LiteralPath $sdkRoot -Directory |
    Sort-Object -Property Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'cppwinrt' } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'winrt\Windows.Graphics.Capture.h') } |
    Select-Object -First 1
if (-not $cppWinRt) {
    throw 'The Windows SDK C++/WinRT headers are required.'
}

$source = Join-Path $PSScriptRoot 'CaptureApiBench.cpp'
$outputDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$executable = Join-Path $outputDirectory 'CaptureApiBench.exe'
$object = Join-Path $outputDirectory 'CaptureApiBench.obj'

$compile = 'call "{0}" >nul && cl /nologo /EHsc /std:c++20 /O2 /I"{1}" "{2}" /Fo:"{3}" /Fe:"{4}"' -f `
    $vcvars, $cppWinRt, $source, $object, $executable
& cmd.exe /c $compile
if ($LASTEXITCODE -ne 0) {
    throw "Capture API benchmark compilation failed with exit code $LASTEXITCODE."
}

if ($Run) {
    & $executable
    if ($LASTEXITCODE -ne 0) {
        throw "Capture API benchmark failed with exit code $LASTEXITCODE."
    }
}
