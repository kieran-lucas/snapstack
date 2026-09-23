param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64',
    [switch]$ExerciseCapture
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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate IntPtr SnapCoreCreateFunction(out int status);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int SnapCoreFreezeFunction(IntPtr handle, out long pinnedAt, out long copiedAt);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int SnapCoreCropFunction(IntPtr handle, int x, int y, uint width, uint height,
    IntPtr destination, UIntPtr destinationBytes, out long submittedAt, out long pixelsAt);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void SnapCoreCancelFunction(IntPtr handle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void SnapCoreDestroyFunction(IntPtr handle);
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

    if ($ExerciseCapture) {
        if ($result -ne 0) { throw 'This display is not eligible for the native capture exercise.' }
        $create = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
            [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_Create'),
            [SnapCoreCreateFunction])
        $freeze = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
            [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_Freeze'),
            [SnapCoreFreezeFunction])
        $crop = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
            [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_Crop'),
            [SnapCoreCropFunction])
        $cancel = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
            [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_Cancel'),
            [SnapCoreCancelFunction])
        $destroy = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
            [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_Destroy'),
            [SnapCoreDestroyFunction])
        $status = 0
        $engine = $create.Invoke([ref]$status)
        if ($engine -eq [IntPtr]::Zero -or $status -ne 0) {
            throw "Native engine creation failed: $status"
        }
        $byteCount = 800 * 500 * 4
        $pixels = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($byteCount)
        try {
            $releaseToPixels = [System.Collections.Generic.List[double]]::new()
            for ($index = 0; $index -lt 20; $index++) {
                $pinnedAt = [long]0
                $copiedAt = [long]0
                $submittedAt = [long]0
                $pixelsAt = [long]0
                $status = $freeze.Invoke($engine, [ref]$pinnedAt, [ref]$copiedAt)
                if ($status -ne 0) { throw "Freeze $index failed: $status" }
                $startedAt = [System.Diagnostics.Stopwatch]::GetTimestamp()
                $status = $crop.Invoke($engine, 600, 400, 800, 500,
                    $pixels, [UIntPtr]::new([uint64]$byteCount),
                    [ref]$submittedAt, [ref]$pixelsAt)
                if ($status -ne 0) { throw "Crop $index failed: $status" }
                $releaseToPixels.Add(
                    (($pixelsAt - $startedAt) * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency))
            }
            $releaseToPixels.Sort()
            Write-Host ("Native core crop/readback: n=20 median={0:N2} ms p95={1:N2} ms p99={2:N2} ms" -f
                $releaseToPixels[9], $releaseToPixels[18], $releaseToPixels[19])
            Write-Host "First BGRA byte: $([System.Runtime.InteropServices.Marshal]::ReadByte($pixels))"
        }
        finally {
            $cancel.Invoke($engine)
            [System.Runtime.InteropServices.Marshal]::FreeHGlobal($pixels)
            $destroy.Invoke($engine)
        }
    }
}
finally {
    [System.Runtime.InteropServices.NativeLibrary]::Free($handle)
}
