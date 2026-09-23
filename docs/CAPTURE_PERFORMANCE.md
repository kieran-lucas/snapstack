# Capture latency investigation

## Scope and measurement status

This is the first comparative prototype, measured on Windows build 26200, Intel Core 7 245HX, primary 2560 × 1600 / 165 Hz display on Intel Graphics (an RTX 5060 is present but does not drive the primary display). The harness runs 100 samples per contender with five warmups. Values below are one complete run; repeat runs showed the same ordering, with warm DXGI snapshot median between 0.379 and 0.584 ms. All timings use QueryPerformanceCounter.

These are **capture primitive** measurements. The later application-level measurements below cover stages observable through the existing `ms-screenclip` path, but not hotkey-to-overlay or mouse-release-to-pixels latency. The picker interaction cannot be inferred from API call timing.

| 800 × 500 BGRA region, 100 samples | Median total | p95 total | p99 total |
| --- | ---: | ---: | ---: |
| GDI BitBlt into warm DIB | 6.028 ms | 7.736 ms | 10.281 ms |
| DXGI Desktop Duplication, acquire after request | 6.008 ms | 8.227 ms | 12.076 ms |
| DXGI Desktop Duplication, maintain full GPU copy after each frame | 6.004 ms | 6.574 ms | 7.299 ms |
| DXGI crop/readback of already resident full-frame copy | **0.584 ms** | **0.860 ms** | **3.190 ms** |
| Windows.Graphics.Capture, monitor frame pool | 18.099 ms | 20.779 ms | 21.364 ms |

The DXGI warm-snapshot test spends 0.001 ms median issuing `CopySubresourceRegion` and 0.582 ms median mapping the staging texture and copying all rows into a reused BGRA buffer. **The latter includes GPU execution and synchronization**, so 0.001 ms is not the completed GPU crop time. The on-demand DXGI path spends 5.176 ms median waiting for a fresh compositor frame. The WGC contender spends 16.691 ms median waiting for a frame. This benchmark does not measure frame age, HDR quality, rotation, overlay visibility, cross-monitor regions, protected content, PNG encode, clipboard publication, or GPU memory under long-running capture.

## Existing SnapStack path

`RegisterHotKey` → WinUI event → `Launcher.LaunchUriAsync(ms-screenclip:)` → Snipping Tool UI → protocol callback → token redemption → file read → PNG copy into `CaptureSession` → recreate a folder and write **every** capture as a PNG file → construct HTML/RTF/file/bitmap clipboard package → `Clipboard.Flush()` → cleanup folders.

In the 0.1.0.7 baseline, `MainPage.BeginRectangleCaptureAsync` rejected a new capture while `_clipboardPublishInProgress` was true. `ClipboardStackService.PublishAsync` rewrote the entire ordered stack and flushed the clipboard before accepting another screenshot. The baseline measurements below quantify that blocking. `CaptureSession.AddCapture` also copies the PNG byte span into a new array. The existing model keeps PNG only, so region extraction, raw image storage, and encode timing are owned by Snipping Tool and invisible to SnapStack.

## API comparison and provisional decision

| Approach | Relevant behavior | Decision for the next prototype |
| --- | --- | --- |
| `ms-screenclip` | Requires a packaged app for callback; callback returns a token that the app redeems for a file. Offers mature selection UI but adds protocol, activation, and file stages. | Instrument as baseline. |
| Windows.Graphics.Capture | D3D11 frame pool can call back from its own worker thread; monitor targeting is available. Active capture can show a colored border; borderless capture needs declared capability and user consent. | Keep as correctness/fallback candidate; fresh-frame result here is slower. |
| DXGI Desktop Duplication | Per-output GPU texture; supports persistent frame acquisition, dirty/move rectangles, and GPU copy. `DXGI_ERROR_ACCESS_LOST` requires recreation after desktop or mode changes. Rotation requires explicit handling. | Prototype resident per-monitor frame storage and region selection. |
| GDI BitBlt | Simple direct CPU DIB crop with no persistent GPU state. | Retain as possible fallback; measured slower on this output. |
| D3D11 vs D3D12 | Both tested GPU capture APIs deliver D3D11 resources. D3D11 copies a source box directly. Moving these resources through D3D12 would add interop and resource-state work without a demonstrated latency benefit. | Use D3D11 until an A/B benchmark justifies D3D12. |
| Full virtual-desktop texture | Requires stitching all per-output frames, including mixed adapters and monitor transforms, every refresh. | Prototype per-monitor snapshots first; compose only regions crossing monitors. |
| GPU vs CPU cropping | `CopySubresourceRegion` can transfer only the selected region before staging readback. CPU cropping requires a full-frame GPU readback first. | GPU crop for DXGI; compare CPU path once actual region sizes are known. |

The persistent concept needs a second benchmark with an actual background frame thread and a pre-created selection overlay. The frame shown to the user must be frozen **before** the overlay appears; otherwise the snapshot may include the selection UI. At selection confirmation, that frozen frame must survive until the region has been copied, even if the live frame loop advances. A one-frame shared texture without ownership is unsafe.

## Proposed next instrumentation

Record QPC ticks at T0 hotkey dispatch, T1 overlay presentation, T2 selection confirmation, T3 framebuffer access, T4 crop/readback complete, T5 session insertion, T6 clipboard publication, and T7 next capture accepted. Track T2→T7 separately from T2→T6; clipboard work must not gate the next selection. Report median, p90, p95, p99, min and max over 20 completed selections per application-level run, plus allocations, GC and GPU memory. The current prototype is not enough to claim those targets.

The current app now has opt-in timing for the stages it owns. Launch a packaged build with `SNAPSTACK_CAPTURE_BENCHMARK=1`, make a session of screenshots, then end or clear the session. It writes `capture-latency.csv` in the app's `ApplicationData.Current.LocalFolder`. Each row records hotkey dispatch, protocol launch, activation, token redemption, file read, session storage, clipboard publication, and when another capture is accepted. Rejected hotkeys are separate rows. Filter to `outcome=captured` before computing latency percentiles. T1 through T4 and the exact mouse-release timestamp cannot be observed through the Snipping Tool protocol; `protocol_to_next_ready_ms` is a lower bound on T2→T7, not an equivalent measurement. The custom capture candidate must expose all T0–T7 timestamps before an end-to-end claim is made.

### First application-level baseline (14 captures)

The local packaged 0.1.0.7 build recorded 14 completed captures. This is a diagnostic sample, **not** a controlled 20-capture run. Median / p95 / p99 are 105.40 / 188.61 / 188.61 ms for clipboard publication and 121.08 / 207.09 / 207.09 ms from protocol activation to next-capture readiness. File-read-to-session insertion is 0.06 ms median. The 1.9–6.9 s hotkey-to-protocol times include the person's selection interaction and must not be described as app processing time. The app also rejected one capture hotkey while selection was in progress. In the current code, the clipboard stage directly gates the next screenshot.

### Background clipboard preparation (20 captures)

The user reduced the requested run size to 20 captures per comparison. The packaged 0.1.0.9 candidate was driven through the Capture button using UI Automation and 20 real Snipping Tool selections. It retained all clipboard formats (RTF, HTML, file list, bitmap) and ended with the ordered 20-image session. The trigger is `button`, so the numbers do not establish physical-hotkey latency.

| Stage | Before, n=14 median / p95 / p99 | After, n=20 median / p95 / p99 |
| --- | --- | --- |
| Protocol activation → next capture ready | 121.08 / 207.09 / 207.09 ms | **11.79 / 14.30 / 48.79 ms** |
| File read → session stored | 0.06 / 1.39 / 1.39 ms | 0.03 / 0.10 / 0.76 ms |
| Session stored → next capture ready | Not recorded | **0.57 / 0.69 / 2.01 ms** |
| Session enqueue → clipboard ready | 105.40 / 188.61 / 188.61 ms | 177.06 / 279.32 / 280.94 ms, background |

Clipboard publication is still slower as the stack grows because every publication rewrites all PNG files and rebuilds the entire RTF string. The current change removes that work from the next-capture gate and moves file/package preparation off the UI thread. The final WinRT clipboard publication remains on the UI thread as required by its documented focus/threading behavior. A later optimization should cache unchanged PNG files and avoid repeated full-stack disk writes, then remeasure rather than assuming the improvement.

### Mouse-release-to-next-capture baseline (20 captures)

A second 20-selection run of the same packaged 0.1.0.9 candidate measured from the harness's injected mouse release until UI Automation observed the next Capture button enabled. Median / p95 / p99 were **773.96 / 793.94 / 812.22 ms** (min 746.40, max 812.22 ms). This includes up to 20 ms of polling uncertainty. In that same run, protocol activation → next-ready was 11.38 / 12.48 / 12.59 ms and session insertion → next-ready was 0.56 / 0.69 / 0.98 ms. The approximately 0.76 s gap before protocol activation is therefore the largest measured delay in this interaction, but this harness cannot separate Snipping Tool's capture/animation, URI callback dispatch, and any input-injection effects. A custom overlay/capture contender must beat this release-to-ready baseline with the same region, display, and 20-selection method before replacing the shipping path.

### Interactive DXGI/Win32 contender, single output (20 selections)

The first custom contender has a persistent `AcquireNextFrame` worker, three application-owned GPU textures, pinned generation ownership, a pre-created layered Win32 overlay, and GPU region copy to staging readback. The same 800 × 500 automated selection geometry was used on the primary Intel output. Across two separate 20-selection runs, native-QPC trigger → overlay `DwmFlush` median ranged **13.23–14.45 ms**, p95 **27.82–28.01 ms**; mouse release → selected BGRA pixels median ranged **4.88–5.50 ms**, p95 **12.18–12.34 ms**. All 40 selections completed. The copied-frame age at trigger had median 5.10–5.24 ms, but this is only time since the worker copy, not true desktop content age. The p95 readback cost rose when the live worker continued copying frames during selection, so a paused-worker variant is worth measuring.

This is an experimental native benchmark, **not** the full application path. It has not yet proven PNG/session/clipboard readiness, all-monitor geometry, HDR color, recovery, cancellation correctness, or long-run resource stability. Its release-to-pixels figure must not be presented as release-to-next-capture-ready. The production default remains Snipping Tool until those gates pass.

### Pause worker while a selection owns a frame

The pinned-texture design was A/B tested with the live DXGI worker either continuing to copy desktop frames or waiting until the selection releases its pin. Two 20-selection runs were made for each mode using the same 800 × 500 geometry. Mouse-release → BGRA-pixels median / p95: continuous **7.66 / 17.15 ms** and **5.85 / 24.80 ms**; paused **6.36 / 9.97 ms** and **5.60 / 10.09 ms**. Overlay-presentation medians stayed near 13 ms in both modes. The median advantage is not robust across runs, but the tail improvement is consistent, so the candidate engine should pause the frame worker during selection. The next-capture path must ensure a fresh non-overlay frame after resuming, particularly under rapid repeated captures.

### Frozen-frame pixel check

A separate 20-selection run placed a green 16 × 16 Win32 marker inside the selected region before frame pinning, changed the live marker to magenta after the overlay appeared, and checked a pixel in each BGRA readback. **20/20** retained the pre-overlay green pixel. This validates ownership against a changing desktop pixel on the tested single-output SDR setup; it does not establish whole-image correctness or exclude all possible overlay artifacts. The marker mode flushes the compositor during selection and is not used for latency comparisons.

### Native core integration boundary

`src/SnapStack.Capture.Native` builds a native DLL with a versioned C ABI. Its display probe conservatively accepts only one attached, unrotated SDR output with working DXGI duplication; this development machine reports one 2560 × 1600 output, identity rotation, SDR color space, and status 0. Multiple outputs, rotation, HDR, and unavailable duplication retain the Snipping Tool path. The DLL is packaged in x64 MSIX; the managed app selects it automatically when eligible, with `SNAPSTACK_CAPTURE_ENGINE=snipping` to force the old path. Native initialization or capture failures fall back to Snipping Tool. This boundary keeps COM pointers and GPU ownership inside native code rather than exposing them across managed interop.

The DLL now also owns a persistent duplication worker, three GPU frame textures, a frozen-slot pin, and a reusable staging texture. Its `Create`/`Freeze`/`Crop`/`Cancel`/`Destroy` ABI returns caller-owned BGRA memory rather than allocating a PNG on the critical path. A 20-crop local ABI exercise on an 800 × 500 region measured crop/readback median **0.48 ms**, p95 **1.63 ms**, p99 **11.52 ms**; this is not an interactive capture result. The engine pauses worker copies while a frame is pinned. It is still experimental and not yet packaged or wired into the WinUI selection flow.

The native core now has a pre-created two-window selection overlay on a dedicated message thread, plus `BeginSelection`/`WaitSelection` APIs. Its first show implementation called `DwmFlush` on the input thread; an injected drag could begin while that thread was blocked and lose the true mouse-down coordinate. The core now records overlay **submission** without blocking input; physical presentation needs an independent ETW/visual measurement.

An A/B 20-selection run on the same single SDR output and nominal 800 × 500 region measured mouse release → BGRA pixels at **24.36 / 34.85 / 44.25 ms** median / p95 / p99 when flushing the compositor after hiding the overlay, versus **6.20 / 14.71 / 28.92 ms** without the flush. All 20/20 selections in each run completed, and their actual dimensions were exactly 800 × 500. The no-flush path is retained for the candidate because a synchronous compositor wait needlessly gates readback; rapid-repeat contamination and freshness still need targeted validation. Begin → overlay submission was 6.48 / 8.88 / 12.05 ms with hide flush and 7.25 / 9.06 / 21.17 ms without, which does **not** establish hotkey → visually presented overlay.

The session model now accepts a capture backed by a pending PNG task. Adding such a capture stores its order and dimensions immediately; clipboard preparation awaits the task on its existing background worker, and the compatibility paste path also awaits it. The current Snipping Tool path still supplies completed PNG bytes, so this refactor alone has no measured end-to-end speedup. It creates the safe handoff needed to let native BGRA pixels enter the session before encoding.

The native core now offers WIC PNG encoding for an existing BGRA buffer, with the encoder output copied once into a caller-owned managed array and freed through the DLL's ABI. A 20-encode local test of an 800 × 500 desktop crop measured median **3.64 ms**, p95 **4.68 ms**, p99 **15.67 ms**. The decoded PNG had the expected 800 × 500 dimensions, the same first BGR pixel as the raw buffer, and opaque alpha; sampled raw alpha was 255 on all 20 crops. This is an encoder primitive test on mostly static content, not a guarantee for complex screenshots. Encoding remains designated background work and must not gate capture readiness.

### Integrated native app candidate (single SDR output)

An installed x64 MSIX used the packaged native DLL, the Win32 overlay, a dedicated selection thread, pooled BGRA readback, deferred WIC PNG encoding, and the existing session/clipboard pipeline. With 20 automated 800 × 500 selections and 200 ms of scripted overlay dwell, all 20 captures reached the final 20-file clipboard. The injected mouse release → UI Automation observing the next Capture button enabled measured **31.13 / 39.80 / 47.93 ms** median / p95 / p99; a second run measured **31.00 / 36.86 / 48.06 ms**. This compares to **773.96 / 793.94 / 812.22 ms** on the Snipping Tool path. The first run's internal QPC trace measured release → session **8.10 / 12.91 / 21.52 ms** and release → next-ready **8.90 / 14.14 / 24.41 ms**; UI Automation observation includes polling and rendering delay. Hotkey → physically visible overlay has not been measured; trigger → overlay-show submission was **6.85 / 8.37 / 18.67 ms** in that run, using the Capture button rather than the hotkey. A 100-ms overlay-dwell stress run completed 20/20 but its first selection had incorrect dimensions (793 × 434); all 20 dimensions were correct in the subsequent 200-ms run. This early-input race remains under investigation and should not be hidden by the latency result.

## References

- [Microsoft: Snipping Tool protocol and callback requirements](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-snipping-tool)
- [Microsoft: Windows.Graphics.Capture frame-pool worker thread](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded)
- [Microsoft: monitor capture interop](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor)
- [Microsoft: capture border permission](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired)
- [Microsoft: Desktop Duplication behavior and rotation](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)
- [Microsoft: frame acquisition error handling](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe)
- [Microsoft: D3D11 GPU box copy](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-copysubresourceregion)
- [Microsoft: desktop duplication sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/DXGIDesktopDuplication)
- [Microsoft: clipboard delayed rendering and its tradeoffs](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-operations)
- [Microsoft: WM_HOTKEY queue priority](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-keyboard-input)
