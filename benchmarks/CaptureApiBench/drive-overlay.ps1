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

$executable = Join-Path $PSScriptRoot 'bin\CaptureApiBench.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw 'Build the benchmark with build.ps1 first.'
}

$artifactDirectory = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$output = Join-Path $artifactDirectory 'overlay-benchmark.txt'
$errors = Join-Path $artifactDirectory 'overlay-benchmark-errors.txt'
$process = Start-Process -FilePath $executable -ArgumentList '--overlay' `
    -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors

try {
    for ($index = 1; $index -le 20; $index++) {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            if ($process.HasExited) {
                throw "Overlay benchmark exited early at selection $index."
            }
            $overlay = [SnapStackOverlayBenchmarkInput]::FindWindow(
                'SnapStackOverlayBenchInput', $null)
            if ($overlay -ne [IntPtr]::Zero -and
                [SnapStackOverlayBenchmarkInput]::IsWindowVisible($overlay)) {
                break
            }
            if ([DateTime]::UtcNow -ge $deadline) {
                throw "Overlay did not appear for selection $index."
            }
            Start-Sleep -Milliseconds 10
        } while ($true)

        [SnapStackOverlayBenchmarkInput]::SetCursorPos(600, 400) | Out-Null
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
