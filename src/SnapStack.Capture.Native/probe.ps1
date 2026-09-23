param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct SnapCoreDisplayProbe {
    public uint AbiVersion;
    public uint ActiveOutputCount;
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public uint Rotation;
    public uint ColorSpace;
    public int Status;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate uint SnapCoreVersionFunction();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int SnapCoreProbeFunction(ref SnapCoreDisplayProbe probe);
'@

$runtime = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$dll = Join-Path $PSScriptRoot "bin\$runtime\SnapStackCaptureNative.dll"
$handle = [System.Runtime.InteropServices.NativeLibrary]::Load($dll)
try {
    $versionPointer = [System.Runtime.InteropServices.NativeLibrary]::GetExport(
        $handle, 'SnapCore_GetAbiVersion')
    $probePointer = [System.Runtime.InteropServices.NativeLibrary]::GetExport(
        $handle, 'SnapCore_ProbeDisplay')
    $version = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $versionPointer, [SnapCoreVersionFunction])
    $probeFunction = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $probePointer, [SnapCoreProbeFunction])
    $probe = [SnapCoreDisplayProbe]::new()
    $probe.AbiVersion = $version.Invoke()
    $result = $probeFunction.Invoke([ref]$probe)
    Write-Host "ABI version: $($probe.AbiVersion)"
    Write-Host "Active outputs: $($probe.ActiveOutputCount)"
    Write-Host "Desktop bounds: $($probe.Left),$($probe.Top) to $($probe.Right),$($probe.Bottom)"
    Write-Host "Rotation: $($probe.Rotation); color space: $($probe.ColorSpace); status: $result"
    if ($result -ne $probe.Status) {
        throw 'The native probe return value disagrees with its output structure.'
    }
}
finally {
    [System.Runtime.InteropServices.NativeLibrary]::Free($handle)
}
