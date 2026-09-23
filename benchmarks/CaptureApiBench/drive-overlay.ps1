param(
    [switch]$PauseWorker,
    [switch]$VerifyFrozen,
    [switch]$NativeCore,
    [switch]$FlushHide
)

$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SnapStackOverlayBenchmarkInput {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@

$executable = if ($NativeCore) {
    Join-Path $PSHOME 'pwsh.exe'
} else {
    Join-Path $PSScriptRoot 'bin\CaptureApiBench.exe'
}
if (-not (Test-Path -LiteralPath $executable)) {
    throw 'Build the benchmark with build.ps1 first.'
}

$artifactDirectory = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$variant = if ($NativeCore -and $FlushHide) { 'native-core-flush' } elseif ($NativeCore) { 'native-core' } elseif ($VerifyFrozen) { 'verify' } elseif ($PauseWorker) { 'pause' } else { 'continuous' }
$output = Join-Path $artifactDirectory "overlay-benchmark-$variant.txt"
$errors = Join-Path $artifactDirectory "overlay-benchmark-errors-$variant.txt"
$argument = if ($NativeCore) {
    $probe = Join-Path $PSScriptRoot '..\..\src\SnapStack.Capture.Native\probe.ps1'
    "-NoProfile -File `"$probe`" -ExerciseOverlay"
} elseif ($VerifyFrozen) { '--overlay-verify' } elseif ($PauseWorker) { '--overlay-pause' } else { '--overlay' }
$windowClass = if ($NativeCore) { 'SnapStackNativeCaptureInput' } else { 'SnapStackOverlayBenchInput' }
$previousFlush = $env:SNAPSTACK_CAPTURE_FLUSH_HIDE
if ($FlushHide) { $env:SNAPSTACK_CAPTURE_FLUSH_HIDE = '1' }
else { Remove-Item Env:SNAPSTACK_CAPTURE_FLUSH_HIDE -ErrorAction SilentlyContinue }
$process = Start-Process -FilePath $executable -ArgumentList $argument `
    -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
if ($null -eq $previousFlush) { Remove-Item Env:SNAPSTACK_CAPTURE_FLUSH_HIDE -ErrorAction SilentlyContinue }
else { $env:SNAPSTACK_CAPTURE_FLUSH_HIDE = $previousFlush }

try {
    for ($index = 1; $index -le 20; $index++) {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ($process.HasExited) {
                throw "Overlay benchmark exited early at selection $index."
            }
            $overlay = [SnapStackOverlayBenchmarkInput]::FindWindow(
                $windowClass, $null)
            if ($overlay -ne [IntPtr]::Zero -and
                [SnapStackOverlayBenchmarkInput]::IsWindowVisible($overlay)) {
                break
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Overlay did not appear for selection $index."
            }
            Start-Sleep -Milliseconds 10
        } while ($true)

        # Match a real user's reaction interval and let the overlay thread
        # complete its first paint before sending the mouse-down event.
        Start-Sleep -Milliseconds 50
        [SnapStackOverlayBenchmarkInput]::SetCursorPos(600, 400) | Out-Null
        Start-Sleep -Milliseconds 20
        [SnapStackOverlayBenchmarkInput]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        for ($step = 1; $step -le 10; $step++) {
            [SnapStackOverlayBenchmarkInput]::SetCursorPos(
                (600 + $step * 80), (400 + $step * 50)) | Out-Null
            Start-Sleep -Milliseconds 10
        }
        [SnapStackOverlayBenchmarkInput]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)

        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([SnapStackOverlayBenchmarkInput]::IsWindowVisible($overlay)) {
            if ($process.HasExited) { break }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Overlay did not close after selection $index."
            }
            Start-Sleep -Milliseconds 10
        }
        Write-Host "Overlay selection $index / 20"
    }

    if (-not $process.WaitForExit(30000)) {
        throw 'Overlay benchmark did not finish after 20 selections.'
    }
    Get-Content -LiteralPath $output
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $errors
        throw "Overlay benchmark exited with code $($process.ExitCode)."
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
    }
    $process.Dispose()
}
