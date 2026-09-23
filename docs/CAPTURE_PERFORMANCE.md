# Capture latency investigation

## Scope and measurement status

This is the first comparative prototype, measured on Windows build 26200, Intel Core 7 245HX, primary 2560 × 1600 / 165 Hz display on Intel Graphics (an RTX 5060 is present but does not drive the primary display). The harness runs 100 samples per contender with five warmups. Values below are one complete run; repeat runs showed the same ordering, with warm DXGI snapshot median between 0.379 and 0.584 ms. All timings use QueryPerformanceCounter.

These are **capture primitive** measurements. The current SnapStack `ms-screenclip` path has no end-to-end instrumentation yet, so there are no honest before/after hotkey or clipboard figures. The picker interaction cannot be inferred from API call timing. Application-level T0–T7 tracing and a manual or automated interactive run are still required.

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

`MainPage.BeginRectangleCaptureAsync` rejects a new capture while `_clipboardPublishInProgress` is true. `ClipboardStackService.PublishAsync` rewrites the entire ordered stack and synchronously flushes the clipboard. This is a code-confirmed source of next-capture blocking, but its duration has not been measured yet. `CaptureSession.AddCapture` also copies the PNG byte span into a new array. The existing model keeps PNG only, so region extraction, raw image storage, and encode timing are owned by Snipping Tool and invisible to SnapStack.

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

Record QPC ticks at T0 hotkey dispatch, T1 overlay presentation, T2 selection confirmation, T3 framebuffer access, T4 crop/readback complete, T5 session insertion, T6 clipboard publication, and T7 next capture accepted. Track T2→T7 separately from T2→T6; clipboard work must not gate the next selection. Report median, p90, p95, p99, min and max over at least 100 completed selections, plus allocations, GC and GPU memory. The current prototype is not enough to claim those targets.

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
