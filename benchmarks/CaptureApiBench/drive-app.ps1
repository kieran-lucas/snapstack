param(
    [ValidateRange(1, 200)]
    [int]$Count = 20,
    [ValidateRange(100, 2000)]
    [int]$OverlayWaitMs = 700,
    [switch]$ContinueSession,
    [switch]$ResetSession
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SnapStackBenchmarkMouse {
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@

$app = Get-Process SnapStack -ErrorAction Stop | Select-Object -First 1
$app.Refresh()
$window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
$scope = [System.Windows.Automation.TreeScope]::Descendants

function Find-Control([string]$id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $control = $window.FindFirst($scope, $condition)
    if (-not $control) { throw "Cannot find SnapStack control $id." }
    return $control
}

function Invoke-Control([string]$id) {
    $control = Find-Control $id
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$start = Find-Control 'StartButton'
if (-not $ContinueSession) {
    if (-not $start.Current.IsEnabled) {
        if (-not $ResetSession) {
            throw 'SnapStack has an active session. Use -ContinueSession or -ResetSession explicitly.'
        }
        Invoke-Control 'ClearButton'
    }
    Invoke-Control 'StartButton'
} elseif ($start.Current.IsEnabled) {
    throw 'SnapStack has no active session to continue.'
}
$countText = Find-Control 'CountText'
$feedback = Find-Control 'FeedbackText'
$initialCount = [int]$countText.Current.Name

for ($index = $initialCount + 1; $index -le $initialCount + $Count; $index++) {
    Invoke-Control 'CaptureButton'
    Start-Sleep -Milliseconds $OverlayWaitMs
    [SnapStackBenchmarkMouse]::SetCursorPos(600, 400) | Out-Null
    [SnapStackBenchmarkMouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    for ($step = 1; $step -le 10; $step++) {
        [SnapStackBenchmarkMouse]::SetCursorPos((600 + $step * 80), (400 + $step * 50)) | Out-Null
        Start-Sleep -Milliseconds 10
    }
    [SnapStackBenchmarkMouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($countText.Current.Name -ne [string]$index) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Capture $index did not arrive. Status: $($feedback.Current.Name)"
        }
        Start-Sleep -Milliseconds 20
    }
    if ($index % 10 -eq 0) {
        Write-Host "Captured $index / $($initialCount + $Count)"
    }
}

Invoke-Control 'StopButton'
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while ($feedback.Current.Name -like 'Finalizing*') {
    if ([DateTime]::UtcNow -ge $deadline) {
        throw 'Final clipboard publication did not finish.'
    }
    Start-Sleep -Milliseconds 50
}
Write-Host "Final state: $($feedback.Current.Name)"
