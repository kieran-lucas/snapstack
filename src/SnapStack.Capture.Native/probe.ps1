param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64',
    [switch]$ExerciseCapture,
    [switch]$ExerciseOverlay,
    [switch]$ExerciseEncoding
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

[StructLayout(LayoutKind.Sequential)]
public struct SnapCoreSelection {
    public uint AbiVersion;
    public int Status;
    public int X;
    public int Y;
    public uint Width;
    public uint Height;
    public long OverlaySubmittedAt;
    public long ReleasedAt;
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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int SnapCoreBeginSelectionFunction(IntPtr handle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int SnapCoreWaitSelectionFunction(IntPtr handle, uint timeoutMs,
    ref SnapCoreSelection selection);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int SnapCoreEncodePngFunction(IntPtr pixels, uint width, uint height,
    uint stride, UIntPtr pixelsBytes, out IntPtr png, out UIntPtr pngBytes);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void SnapCoreFreeBufferFunction(IntPtr buffer);
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

    if ($ExerciseCapture -or $ExerciseOverlay -or $ExerciseEncoding) {
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
        $byteCount = 900 * 600 * 4
        $pixels = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($byteCount)
        try {
            $releaseToPixels = [System.Collections.Generic.List[double]]::new()
            $alphaSamples = [System.Collections.Generic.List[int]]::new()
            if ($ExerciseOverlay) {
                $begin = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
                    [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_BeginSelection'),
                    [SnapCoreBeginSelectionFunction])
                $waitSelection = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
                    [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_WaitSelection'),
                    [SnapCoreWaitSelectionFunction])
                $showMs = [System.Collections.Generic.List[double]]::new()
                $widths = [System.Collections.Generic.List[int]]::new()
                $heights = [System.Collections.Generic.List[int]]::new()
            }
            for ($index = 0; $index -lt 20; $index++) {
                $submittedAt = [long]0
                $pixelsAt = [long]0
                if ($ExerciseOverlay) {
                    $startedAt = [System.Diagnostics.Stopwatch]::GetTimestamp()
                    $status = $begin.Invoke($engine)
                    if ($status -ne 0) { throw "Begin selection $index failed: $status" }
                    $selection = [SnapCoreSelection]::new()
                    $selection.AbiVersion = 1
                    $status = $waitSelection.Invoke($engine, 15000, [ref]$selection)
                    if ($status -ne 0 -or $selection.Status -ne 0) {
                        throw "Selection $index failed: wait=$status result=$($selection.Status)"
                    }
                    if ($selection.Width -lt 780 -or $selection.Width -gt 820 -or
                        $selection.Height -lt 480 -or $selection.Height -gt 520) {
                        throw "Selection $index was $($selection.Width)x$($selection.Height), outside the 800x500 test tolerance."
                    }
                    $widths.Add([int]$selection.Width)
                    $heights.Add([int]$selection.Height)
                    $showMs.Add((($selection.OverlaySubmittedAt - $startedAt) * 1000.0 /
                        [System.Diagnostics.Stopwatch]::Frequency))
                    $startedAt = $selection.ReleasedAt
                    $x = $selection.X
                    $y = $selection.Y
                    $width = $selection.Width
                    $height = $selection.Height
                } else {
                    $pinnedAt = [long]0
                    $copiedAt = [long]0
                    $status = $freeze.Invoke($engine, [ref]$pinnedAt, [ref]$copiedAt)
                    if ($status -ne 0) { throw "Freeze $index failed: $status" }
                    $startedAt = [System.Diagnostics.Stopwatch]::GetTimestamp()
                    $x = 600
                    $y = 400
                    $width = 800
                    $height = 500
                }
                $status = $crop.Invoke($engine, $x, $y, $width, $height,
                    $pixels, [UIntPtr]::new([uint64]$byteCount),
                    [ref]$submittedAt, [ref]$pixelsAt)
                if ($status -ne 0) { throw "Crop $index failed: $status" }
                $releaseToPixels.Add(
                    (($pixelsAt - $startedAt) * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency))
                $alphaSamples.Add([System.Runtime.InteropServices.Marshal]::ReadByte($pixels, 3))
                if ($ExerciseOverlay) { Start-Sleep -Milliseconds 250 }
            }
            $releaseToPixels.Sort()
            if ($ExerciseOverlay) {
                $showMs.Sort()
                Write-Host ("Native core begin -> overlay submit: n=20 median={0:N2} ms p95={1:N2} ms p99={2:N2} ms" -f
                    $showMs[9], $showMs[18], $showMs[19])
                $widths.Sort()
                $heights.Sort()
                Write-Host "Selection dimensions: width $($widths[0])–$($widths[19]), height $($heights[0])–$($heights[19])"
            }
            Write-Host ("Native core release -> pixels: n=20 median={0:N2} ms p95={1:N2} ms p99={2:N2} ms" -f
                $releaseToPixels[9], $releaseToPixels[18], $releaseToPixels[19])
            $alphaSamples.Sort()
            Write-Host "Sampled BGRA alpha range: $($alphaSamples[0])–$($alphaSamples[19])"
            Write-Host "First BGRA byte: $([System.Runtime.InteropServices.Marshal]::ReadByte($pixels))"

            if ($ExerciseEncoding) {
                $encode = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
                    [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_EncodePng'),
                    [SnapCoreEncodePngFunction])
                $freeBuffer = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
                    [System.Runtime.InteropServices.NativeLibrary]::GetExport($handle, 'SnapCore_FreeBuffer'),
                    [SnapCoreFreeBufferFunction])
                $encodeMs = [System.Collections.Generic.List[double]]::new()
                for ($index = 0; $index -lt 20; $index++) {
                    $png = [IntPtr]::Zero
                    $pngLength = [UIntPtr]::Zero
                    $startedAt = [System.Diagnostics.Stopwatch]::GetTimestamp()
                    $status = $encode.Invoke($pixels, $width, $height, $width * 4,
                        [UIntPtr]::new([uint64]($width * $height * 4)),
                        [ref]$png, [ref]$pngLength)
                    $endedAt = [System.Diagnostics.Stopwatch]::GetTimestamp()
                    if ($status -ne 0 -or $png -eq [IntPtr]::Zero) {
                        throw "WIC PNG encode $index failed: $status"
                    }
                    try {
                        $encodeMs.Add((($endedAt - $startedAt) * 1000.0 /
                            [System.Diagnostics.Stopwatch]::Frequency))
                        if ($index -eq 0) {
                            $pngBytes = [byte[]]::new([int]$pngLength.ToUInt64())
                            [System.Runtime.InteropServices.Marshal]::Copy(
                                $png, $pngBytes, 0, $pngBytes.Length)
                            $signature = [Convert]::ToHexString($pngBytes, 0, 8)
                            if ($signature -ne '89504E470D0A1A0A') {
                                throw "Encoder returned invalid PNG signature: $signature"
                            }
                            Add-Type -AssemblyName System.Drawing
                            $stream = [System.IO.MemoryStream]::new($pngBytes)
                            try {
                                $bitmap = [System.Drawing.Bitmap]::FromStream($stream)
                                try {
                                    if ($bitmap.Width -ne $width -or $bitmap.Height -ne $height) {
                                        throw "Decoded PNG has wrong dimensions: $($bitmap.Width)x$($bitmap.Height)."
                                    }
                                    $pixel = $bitmap.GetPixel(0, 0)
                                    $rawBlue = [System.Runtime.InteropServices.Marshal]::ReadByte($pixels, 0)
                                    $rawGreen = [System.Runtime.InteropServices.Marshal]::ReadByte($pixels, 1)
                                    $rawRed = [System.Runtime.InteropServices.Marshal]::ReadByte($pixels, 2)
                                    if ($pixel.B -ne $rawBlue -or $pixel.G -ne $rawGreen -or
                                        $pixel.R -ne $rawRed -or $pixel.A -ne 255) {
                                        throw 'Decoded PNG pixel does not match raw BGRA.'
                                    }
                                }
                                finally { $bitmap.Dispose() }
                            }
                            finally { $stream.Dispose() }
                            Write-Host "Decoded PNG dimensions and first BGRA pixel verified; bytes=$($pngBytes.Length)"
                        }
                    }
                    finally { $freeBuffer.Invoke($png) }
                }
                $encodeMs.Sort()
                Write-Host ("WIC PNG encode: n=20 median={0:N2} ms p95={1:N2} ms p99={2:N2} ms" -f
                    $encodeMs[9], $encodeMs[18], $encodeMs[19])
            }
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
