# Capture API benchmark

Run on an interactive Windows 11 desktop with Visual Studio C++ Build Tools and the Windows SDK:

```powershell
.\benchmarks\CaptureApiBench\build.ps1 -Run
```

The harness creates a 16 × 16 pixel window near the lower-right corner of the primary monitor and changes its color before each sample. It warms each contender for five iterations, then uses QPC to report min, median, p90, p95, p99, and max from 100 samples of an 800 × 500 BGRA region. It uses the same D3D11 device and monitor for DXGI Desktop Duplication and Windows.Graphics.Capture. The GDI contender uses a warm DIB section. The DXGI warm-snapshot measurement reuses a frame copied to a persistent GPU texture, without waiting for a new desktop frame.

`wait for new frame` includes the display/compositor update and frame delivery. `GPU crop / BitBlt issue` records the time to submit a GPU copy, or the complete synchronous GDI BitBlt. `CPU readback / DIB` includes the GPU synchronization caused by mapping a staging texture and copying every row into a reused CPU buffer; it is not pure CPU work. `total` is the elapsed wall time of the complete operation. The warm-snapshot result is a capture-primitive measurement, not hotkey-to-clipboard latency.

The benchmark does not automate the Snipping Tool picker, measure overlay presentation, encode PNG, publish the clipboard, or verify multi-monitor rotation/HDR. Those require separate application-level instrumentation and correctness checks before replacing the production capture path. Results from one machine should not be assumed to hold on another GPU or refresh rate.

For an application-level run, launch an installed SnapStack build with `SNAPSTACK_CAPTURE_BENCHMARK=1`, then run `drive-app.ps1` while the app is idle. The script drives 20 real Snipping Tool selections by default, moves the mouse, replaces the clipboard, and ends the session. It invokes the Capture button through UI Automation, so the `trigger` column is `button`; this run does **not** measure the global hotkey dispatch. It reports mouse-release-to-next-Capture-enabled latency using the harness's `Stopwatch` clock, including Snipping Tool completion, protocol delivery, and up to 20 ms of UI Automation polling. It writes `capture-latency.csv` in the app package's LocalState folder after End. Use `-ContinueSession` to add captures to an active session, or `-ResetSession` to explicitly discard an active session before starting a fresh run.

The interactive native contender runs with `bin\CaptureApiBench.exe --overlay` after `build.ps1`. Run `drive-overlay.ps1` for 20 automated 800 × 500 selections on the primary output. It maintains three owned GPU textures, pins the latest before showing two pre-created layered Win32 overlay windows, and reads back only the selected GPU region after mouse release. The result is a **single-output prototype**, not a SnapStack engine: it does not encode PNG, enter `CaptureSession`, publish clipboard formats, or cover DPI/rotation/HDR/recovery. Its frame-age number is time since the latest GPU copy, not the compositor frame's actual age. The script saves native output under `artifacts/`.
