#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <d3d11.h>
#include <dwmapi.h>
#include <wincodec.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <thread>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

// Fixed-width, versioned C ABI. A future engine handle must not expose COM
// pointers or C++ object ownership to the WinUI process.
extern "C" {

struct SnapCoreDisplayProbe {
    unsigned abiVersion;
    unsigned activeOutputCount;
    long left;
    long top;
    long right;
    long bottom;
    unsigned rotation;
    unsigned colorSpace;
    long status;
};

struct SnapCoreSelection {
    unsigned abiVersion;
    long status;
    int x;
    int y;
    unsigned width;
    unsigned height;
    long long overlaySubmittedAt;
    long long releasedAt;
};

__declspec(dllexport) unsigned __cdecl SnapCore_GetAbiVersion() noexcept {
    return 1;
}

__declspec(dllexport) long __cdecl SnapCore_ProbeDisplay(
    SnapCoreDisplayProbe* probe) noexcept;

}

namespace {

class WarmEngine {
public:
    explicit WarmEngine(const SnapCoreDisplayProbe& probe)
        : bounds_{probe.left, probe.top, probe.right, probe.bottom} {
        wchar_t flushFlag[2]{};
        flushHide_ = GetEnvironmentVariableW(
            L"SNAPSTACK_CAPTURE_FLUSH_HIDE", flushFlag, 2) > 0;
        ComPtr<IDXGIFactory1> factory;
        auto hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(hr)) throw hr;

        ComPtr<IDXGIAdapter1> selectedAdapter;
        ComPtr<IDXGIOutput1> selectedOutput;
        for (UINT adapterIndex = 0; !selectedOutput; ++adapterIndex) {
            ComPtr<IDXGIAdapter1> adapter;
            hr = factory->EnumAdapters1(adapterIndex, &adapter);
            if (FAILED(hr)) throw hr;
            for (UINT outputIndex = 0; ; ++outputIndex) {
                ComPtr<IDXGIOutput> output;
                hr = adapter->EnumOutputs(outputIndex, &output);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                if (FAILED(hr)) throw hr;
                DXGI_OUTPUT_DESC desc{};
                hr = output->GetDesc(&desc);
                if (FAILED(hr)) throw hr;
                if (!desc.AttachedToDesktop) continue;
                selectedAdapter = adapter;
                hr = output.As(&selectedOutput);
                if (FAILED(hr)) throw hr;
                break;
            }
        }

        D3D_FEATURE_LEVEL level{};
        hr = D3D11CreateDevice(selectedAdapter.Get(), D3D_DRIVER_TYPE_UNKNOWN,
            nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
            D3D11_SDK_VERSION, &device_, &level, &context_);
        if (FAILED(hr)) throw hr;
        hr = selectedOutput->DuplicateOutput(device_.Get(), &duplication_);
        if (FAILED(hr)) throw hr;

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = static_cast<UINT>(bounds_.right - bounds_.left);
        desc.Height = static_cast<UINT>(bounds_.bottom - bounds_.top);
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        for (auto& slot : slots_) {
            hr = device_->CreateTexture2D(&desc, nullptr, &slot.texture);
            if (FAILED(hr)) throw hr;
        }
        worker_ = std::thread([this] { Run(); });
    }

    ~WarmEngine() {
        if (inputWindow_) PostMessageW(inputWindow_, WM_CLOSE, 0, 0);
        if (overlayThread_.joinable()) overlayThread_.join();
        if (selectionPen_) DeleteObject(selectionPen_);
        if (dimBrush_) DeleteObject(dimBrush_);
        if (selectionEvent_) CloseHandle(selectionEvent_);
        if (overlayReadyEvent_) CloseHandle(overlayReadyEvent_);
        stop_ = true;
        changed_.notify_all();
        if (worker_.joinable()) worker_.join();
    }

    HRESULT InitializeOverlay() noexcept {
        overlayReadyEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        selectionEvent_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!overlayReadyEvent_ || !selectionEvent_)
            return HRESULT_FROM_WIN32(GetLastError());
        try {
            overlayThread_ = std::thread([this] { RunOverlay(); });
        } catch (...) {
            return E_FAIL;
        }
        if (WaitForSingleObject(overlayReadyEvent_, 3000) != WAIT_OBJECT_0)
            return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        return overlayStatus_;
    }

    HRESULT BeginSelection() noexcept {
        if (selectionActive_.exchange(true)) return HRESULT_FROM_WIN32(ERROR_BUSY);
        const auto frozen = Freeze(nullptr, nullptr);
        if (FAILED(frozen)) {
            selectionActive_ = false;
            return frozen;
        }
        ResetEvent(selectionEvent_);
        if (!inputWindow_ || !PostMessageW(inputWindow_, WM_APP_START, 0, 0)) {
            Cancel();
            selectionActive_ = false;
            return HRESULT_FROM_WIN32(GetLastError());
        }
        return S_OK;
    }

    HRESULT WaitSelection(unsigned timeoutMs, SnapCoreSelection* output) noexcept {
        if (!output || output->abiVersion != 1 || !selectionEvent_) return E_INVALIDARG;
        const auto wait = WaitForSingleObject(selectionEvent_, timeoutMs);
        if (wait == WAIT_TIMEOUT) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        if (wait != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(GetLastError());
        std::lock_guard lock(mutex_);
        *output = selection_;
        return S_OK;
    }

    void CancelSelection() noexcept {
        if (selectionActive_ && inputWindow_) {
            PostMessageW(inputWindow_, WM_APP_CANCEL, 0, 0);
        }
    }

    HRESULT Freeze(long long* pinnedAt, long long* frameCopiedAt) noexcept {
        std::unique_lock lock(mutex_);
        if (pinned_ >= 0) return HRESULT_FROM_WIN32(ERROR_BUSY);
        changed_.wait_for(lock, std::chrono::seconds(1),
            [this] { return latest_ >= 0 || FAILED(failure_) || stop_; });
        if (FAILED(failure_)) return failure_;
        if (latest_ < 0) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        pinned_ = latest_;
        paused_ = true;
        if (pinnedAt) *pinnedAt = Counter();
        if (frameCopiedAt) *frameCopiedAt = slots_[pinned_].copiedAt;
        return S_OK;
    }

    HRESULT Crop(int x, int y, unsigned width, unsigned height,
        unsigned char* destination, size_t destinationBytes,
        long long* submittedAt, long long* pixelsAt) noexcept {
        if (!destination || width == 0 || height == 0 ||
            static_cast<size_t>(width) * height > SIZE_MAX / 4 ||
            destinationBytes < static_cast<size_t>(width) * height * 4) {
            return E_INVALIDARG;
        }
        if (x < bounds_.left || y < bounds_.top ||
            static_cast<long long>(x) + width > bounds_.right ||
            static_cast<long long>(y) + height > bounds_.bottom) {
            return E_INVALIDARG;
        }

        HRESULT result = S_OK;
        {
            std::lock_guard lock(mutex_);
            if (pinned_ < 0) return E_UNEXPECTED;
            if (!staging_ || stagingWidth_ != width || stagingHeight_ != height) {
                staging_.Reset();
                D3D11_TEXTURE2D_DESC desc{};
                desc.Width = width;
                desc.Height = height;
                desc.MipLevels = 1;
                desc.ArraySize = 1;
                desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.SampleDesc.Count = 1;
                desc.Usage = D3D11_USAGE_STAGING;
                desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                result = device_->CreateTexture2D(&desc, nullptr, &staging_);
                if (SUCCEEDED(result)) {
                    stagingWidth_ = width;
                    stagingHeight_ = height;
                }
            }
            if (SUCCEEDED(result)) {
                const auto localX = static_cast<UINT>(x - bounds_.left);
                const auto localY = static_cast<UINT>(y - bounds_.top);
                const D3D11_BOX box{localX, localY, 0,
                    localX + width, localY + height, 1};
                context_->CopySubresourceRegion(staging_.Get(), 0, 0, 0, 0,
                    slots_[pinned_].texture.Get(), 0, &box);
                if (submittedAt) *submittedAt = Counter();
                D3D11_MAPPED_SUBRESOURCE mapped{};
                result = context_->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped);
                if (SUCCEEDED(result)) {
                    const auto source = static_cast<const unsigned char*>(mapped.pData);
                    for (unsigned row = 0; row < height; ++row) {
                        std::memcpy(destination + static_cast<size_t>(row) * width * 4,
                            source + static_cast<size_t>(row) * mapped.RowPitch,
                            static_cast<size_t>(width) * 4);
                    }
                    context_->Unmap(staging_.Get(), 0);
                    if (pixelsAt) *pixelsAt = Counter();
                }
            }
            pinned_ = -1;
            paused_ = false;
        }
        selectionActive_ = false;
        changed_.notify_one();
        return result;
    }

    void Cancel() noexcept {
        {
            std::lock_guard lock(mutex_);
            pinned_ = -1;
            paused_ = false;
        }
        selectionActive_ = false;
        changed_.notify_one();
    }

private:
    static constexpr UINT WM_APP_START = WM_APP + 31;
    static constexpr UINT WM_APP_CANCEL = WM_APP + 32;

    struct Slot {
        ComPtr<ID3D11Texture2D> texture;
        long long copiedAt = 0;
    };

    static long long Counter() noexcept {
        LARGE_INTEGER value{};
        QueryPerformanceCounter(&value);
        return value.QuadPart;
    }

    static LRESULT CALLBACK InputProc(
        HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
        if (message == WM_NCCREATE) {
            const auto created = reinterpret_cast<CREATESTRUCTW*>(lparam);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA,
                reinterpret_cast<LONG_PTR>(created->lpCreateParams));
        }
        auto* engine = reinterpret_cast<WarmEngine*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (!engine) return DefWindowProcW(hwnd, message, wparam, lparam);
        switch (message) {
        case WM_APP_START:
            engine->ShowOverlay();
            return 0;
        case WM_APP_CANCEL:
            engine->FinishSelection(1);
            return 0;
        case WM_LBUTTONDOWN:
            engine->dragStart_ = {GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
            engine->dragEnd_ = engine->dragStart_;
            engine->dragging_ = true;
            SetCapture(hwnd);
            return 0;
        case WM_MOUSEMOVE:
            if (engine->dragging_) {
                engine->dragEnd_ = {GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
                InvalidateRect(engine->borderWindow_, nullptr, FALSE);
            }
            return 0;
        case WM_LBUTTONUP:
            if (engine->dragging_) {
                engine->releasedAt_ = Counter();
                engine->dragEnd_ = {GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
                engine->FinishSelection(0);
            }
            return 0;
        case WM_KEYDOWN:
            if (wparam == VK_ESCAPE) {
                engine->FinishSelection(1);
                return 0;
            }
            break;
        case WM_DISPLAYCHANGE:
        case WM_CANCELMODE:
            if (engine->selectionActive_) engine->FinishSelection(1);
            return 0;
        case WM_CLOSE:
            if (engine->selectionActive_) engine->FinishSelection(1);
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    static LRESULT CALLBACK BorderProc(
        HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) noexcept {
        if (message == WM_NCCREATE) {
            const auto created = reinterpret_cast<CREATESTRUCTW*>(lparam);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA,
                reinterpret_cast<LONG_PTR>(created->lpCreateParams));
        }
        if (message == WM_PAINT) {
            PAINTSTRUCT ps{};
            const auto dc = BeginPaint(hwnd, &ps);
            auto* engine = reinterpret_cast<WarmEngine*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
            if (engine && engine->dimBrush_) {
                FillRect(dc, &ps.rcPaint, engine->dimBrush_);
            }
            if (engine && engine->dragging_) {
                const RECT selected{
                    std::min(engine->dragStart_.x, engine->dragEnd_.x),
                    std::min(engine->dragStart_.y, engine->dragEnd_.y),
                    std::max(engine->dragStart_.x, engine->dragEnd_.x),
                    std::max(engine->dragStart_.y, engine->dragEnd_.y)};
                // Black is the layer's color key. The selected pixels are
                // fully transparent while everything around them is dimmed.
                FillRect(dc, &selected, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
                const auto previousPen = SelectObject(dc, engine->selectionPen_);
                const auto previousBrush = SelectObject(dc, GetStockObject(HOLLOW_BRUSH));
                Rectangle(dc,
                    selected.left, selected.top, selected.right, selected.bottom);
                SelectObject(dc, previousBrush);
                SelectObject(dc, previousPen);
            }
            EndPaint(hwnd, &ps);
            return 0;
        }
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    void RunOverlay() noexcept {
        const auto instance = GetModuleHandleW(nullptr);
        WNDCLASSW inputClass{};
        inputClass.lpfnWndProc = InputProc;
        inputClass.hInstance = instance;
        inputClass.lpszClassName = L"SnapStackNativeCaptureInput";
        inputClass.hCursor = LoadCursor(nullptr, IDC_CROSS);
        RegisterClassW(&inputClass);
        WNDCLASSW borderClass = inputClass;
        borderClass.lpfnWndProc = BorderProc;
        borderClass.lpszClassName = L"SnapStackNativeCaptureBorder";
        RegisterClassW(&borderClass);
        const auto width = bounds_.right - bounds_.left;
        const auto height = bounds_.bottom - bounds_.top;
        inputWindow_ = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            inputClass.lpszClassName, L"", WS_POPUP,
            bounds_.left, bounds_.top, width, height,
            nullptr, nullptr, instance, this);
        borderWindow_ = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOPMOST |
                WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
            borderClass.lpszClassName, L"", WS_POPUP,
            bounds_.left, bounds_.top, width, height,
            nullptr, nullptr, instance, this);
        if (inputWindow_ && borderWindow_ &&
            (dimBrush_ = CreateSolidBrush(RGB(4, 12, 24))) &&
            (selectionPen_ = CreatePen(PS_SOLID, 3, RGB(64, 139, 255))) &&
            SetLayeredWindowAttributes(inputWindow_, 0, 1, LWA_ALPHA) &&
            SetLayeredWindowAttributes(borderWindow_, RGB(0, 0, 0), 150,
                LWA_COLORKEY | LWA_ALPHA)) {
            // Exclusion is a secondary safeguard; the pinned frame predates
            // both overlay windows becoming visible. The visual-check switch
            // is for the benchmark harness to photograph the overlay only.
            wchar_t visualCheck[2]{};
            const auto visualCheckLength = GetEnvironmentVariableW(
                L"SNAPSTACK_CAPTURE_VISUAL_CHECK", visualCheck, 2);
            if (visualCheckLength != 1 || visualCheck[0] != L'1') {
                SetWindowDisplayAffinity(inputWindow_, WDA_EXCLUDEFROMCAPTURE);
                SetWindowDisplayAffinity(borderWindow_, WDA_EXCLUDEFROMCAPTURE);
            }
            overlayStatus_ = S_OK;
        } else {
            overlayStatus_ = HRESULT_FROM_WIN32(GetLastError());
        }
        SetEvent(overlayReadyEvent_);
        if (SUCCEEDED(overlayStatus_)) {
            MSG message{};
            while (GetMessageW(&message, nullptr, 0, 0) > 0) {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        if (borderWindow_) DestroyWindow(borderWindow_);
        if (inputWindow_) DestroyWindow(inputWindow_);
        borderWindow_ = inputWindow_ = nullptr;
    }

    void ShowOverlay() noexcept {
        dragging_ = false;
        releasedAt_ = 0;
        InvalidateRect(borderWindow_, nullptr, FALSE);
        previousForeground_ = GetForegroundWindow();
        ShowWindow(inputWindow_, SW_SHOW);
        ShowWindow(borderWindow_, SW_SHOWNOACTIVATE);
        SetForegroundWindow(inputWindow_);
        SetFocus(inputWindow_);
        UpdateWindow(inputWindow_);
        UpdateWindow(borderWindow_);
        // Do not block this input thread on DwmFlush: a fast drag could be
        // coalesced before WM_LBUTTONDOWN is dispatched. This timestamp is a
        // show/paint submission, not proof of physical presentation.
        overlaySubmittedAt_ = Counter();
    }

    void FinishSelection(long status) noexcept {
        if (!selectionActive_) return;
        const auto left = std::clamp(std::min(dragStart_.x, dragEnd_.x),
            0L, bounds_.right - bounds_.left);
        const auto top = std::clamp(std::min(dragStart_.y, dragEnd_.y),
            0L, bounds_.bottom - bounds_.top);
        const auto right = std::clamp(std::max(dragStart_.x, dragEnd_.x),
            0L, bounds_.right - bounds_.left);
        const auto bottom = std::clamp(std::max(dragStart_.y, dragEnd_.y),
            0L, bounds_.bottom - bounds_.top);
        dragging_ = false;
        if (GetCapture() == inputWindow_) ReleaseCapture();
        ShowWindow(borderWindow_, SW_HIDE);
        ShowWindow(inputWindow_, SW_HIDE);
        if (flushHide_) DwmFlush();
        if (previousForeground_ && IsWindow(previousForeground_))
            SetForegroundWindow(previousForeground_);
        if (right <= left || bottom <= top) status = 1;
        {
            std::lock_guard lock(mutex_);
            selection_ = {1, status,
                static_cast<int>(bounds_.left + left),
                static_cast<int>(bounds_.top + top),
                static_cast<unsigned>(right - left),
                static_cast<unsigned>(bottom - top),
                overlaySubmittedAt_, releasedAt_};
        }
        if (status != 0) Cancel();
        SetEvent(selectionEvent_);
    }

    void Run() noexcept {
        while (!stop_) {
            {
                std::unique_lock lock(mutex_);
                changed_.wait(lock, [this] { return stop_ || !paused_; });
            }
            if (stop_) break;
            DXGI_OUTDUPL_FRAME_INFO info{};
            ComPtr<IDXGIResource> resource;
            const auto hr = duplication_->AcquireNextFrame(16, &info, &resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue;
            if (FAILED(hr)) {
                Fail(hr);
                break;
            }
            ComPtr<ID3D11Texture2D> source;
            const auto sourceHr = resource.As(&source);
            if (FAILED(sourceHr)) {
                duplication_->ReleaseFrame();
                Fail(sourceHr);
                break;
            }
            {
                std::lock_guard lock(mutex_);
                if (!paused_) {
                    int next = (latest_ + 1) % 3;
                    if (next == pinned_) next = (next + 1) % 3;
                    context_->CopyResource(slots_[next].texture.Get(), source.Get());
                    context_->Flush();
                    slots_[next].copiedAt = Counter();
                    latest_ = next;
                }
            }
            changed_.notify_all();
            const auto releaseHr = duplication_->ReleaseFrame();
            if (FAILED(releaseHr)) {
                Fail(releaseHr);
                break;
            }
        }
    }

    void Fail(HRESULT error) noexcept {
        {
            std::lock_guard lock(mutex_);
            failure_ = error;
        }
        changed_.notify_all();
    }

    RECT bounds_{};
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<IDXGIOutputDuplication> duplication_;
    Slot slots_[3];
    ComPtr<ID3D11Texture2D> staging_;
    unsigned stagingWidth_ = 0;
    unsigned stagingHeight_ = 0;
    std::thread worker_;
    std::thread overlayThread_;
    std::mutex mutex_;
    std::condition_variable changed_;
    std::atomic<bool> stop_{false};
    std::atomic<bool> selectionActive_{false};
    HRESULT failure_ = S_OK;
    int latest_ = -1;
    int pinned_ = -1;
    bool paused_ = false;
    HANDLE overlayReadyEvent_ = nullptr;
    HANDLE selectionEvent_ = nullptr;
    HWND inputWindow_ = nullptr;
    HWND borderWindow_ = nullptr;
    HWND previousForeground_ = nullptr;
    HBRUSH dimBrush_ = nullptr;
    HPEN selectionPen_ = nullptr;
    HRESULT overlayStatus_ = E_FAIL;
    SnapCoreSelection selection_{1, 1, 0, 0, 0, 0, 0, 0};
    POINT dragStart_{};
    POINT dragEnd_{};
    bool dragging_ = false;
    bool flushHide_ = false;
    long long overlaySubmittedAt_ = 0;
    long long releasedAt_ = 0;
};

struct ComApartment {
    HRESULT status = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ~ComApartment() {
        if (SUCCEEDED(status)) CoUninitialize();
    }
};

HRESULT EncodePngCore(const unsigned char* pixels, unsigned width,
    unsigned height, unsigned stride, size_t pixelsBytes,
    unsigned char** output, size_t* outputBytes) noexcept {
    if (!pixels || !output || !outputBytes || width == 0 || height == 0 ||
        width > UINT_MAX / 4 || stride < width * 4 ||
        static_cast<size_t>(stride) * height > pixelsBytes ||
        static_cast<size_t>(stride) * height > UINT_MAX) return E_INVALIDARG;
    *output = nullptr;
    *outputBytes = 0;

    ComApartment apartment;
    if (FAILED(apartment.status) && apartment.status != RPC_E_CHANGED_MODE)
        return apartment.status;

    ComPtr<IWICImagingFactory> factory;
    auto hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr,
        CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return hr;
    ComPtr<IStream> stream;
    hr = CreateStreamOnHGlobal(nullptr, TRUE, &stream);
    if (FAILED(hr)) return hr;
    ComPtr<IWICBitmapEncoder> encoder;
    hr = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
    if (FAILED(hr)) return hr;
    hr = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
    if (FAILED(hr)) return hr;
    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> options;
    hr = encoder->CreateNewFrame(&frame, &options);
    if (FAILED(hr)) return hr;
    hr = frame->Initialize(options.Get());
    if (FAILED(hr)) return hr;
    hr = frame->SetSize(width, height);
    if (FAILED(hr)) return hr;
    WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
    hr = frame->SetPixelFormat(&format);
    if (FAILED(hr)) return hr;
    if (!IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA))
        return WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT;
    hr = frame->WritePixels(height, stride,
        static_cast<UINT>(static_cast<size_t>(stride) * height),
        const_cast<BYTE*>(pixels));
    if (FAILED(hr)) return hr;
    hr = frame->Commit();
    if (FAILED(hr)) return hr;
    hr = encoder->Commit();
    if (FAILED(hr)) return hr;

    STATSTG info{};
    hr = stream->Stat(&info, STATFLAG_NONAME);
    if (FAILED(hr)) return hr;
    if (info.cbSize.QuadPart == 0 || info.cbSize.QuadPart > SIZE_MAX)
        return E_FAIL;
    HGLOBAL storage = nullptr;
    hr = GetHGlobalFromStream(stream.Get(), &storage);
    if (FAILED(hr)) return hr;
    const auto source = GlobalLock(storage);
    if (!source) return HRESULT_FROM_WIN32(GetLastError());
    const auto size = static_cast<size_t>(info.cbSize.QuadPart);
    auto destination = static_cast<unsigned char*>(CoTaskMemAlloc(size));
    if (!destination) {
        GlobalUnlock(storage);
        return E_OUTOFMEMORY;
    }
    std::memcpy(destination, source, size);
    GlobalUnlock(storage);
    *output = destination;
    *outputBytes = size;
    return S_OK;
}

}

extern "C" {

__declspec(dllexport) void* __cdecl SnapCore_Create(long* status) noexcept {
    if (status) *status = E_INVALIDARG;
    if (!status) return nullptr;
    SnapCoreDisplayProbe probe{};
    probe.abiVersion = 1;
    const auto result = SnapCore_ProbeDisplay(&probe);
    if (result != 0) {
        *status = result;
        return nullptr;
    }
    try {
        auto* engine = new WarmEngine(probe);
        const auto overlayStatus = engine->InitializeOverlay();
        if (FAILED(overlayStatus)) {
            *status = overlayStatus;
            delete engine;
            return nullptr;
        }
        *status = S_OK;
        return engine;
    } catch (HRESULT error) {
        *status = error;
    } catch (...) {
        *status = E_FAIL;
    }
    return nullptr;
}

__declspec(dllexport) long __cdecl SnapCore_Freeze(
    void* handle, long long* pinnedAt, long long* frameCopiedAt) noexcept {
    return handle ? static_cast<WarmEngine*>(handle)->Freeze(pinnedAt, frameCopiedAt) : E_INVALIDARG;
}

__declspec(dllexport) long __cdecl SnapCore_BeginSelection(void* handle) noexcept {
    return handle ? static_cast<WarmEngine*>(handle)->BeginSelection() : E_INVALIDARG;
}

__declspec(dllexport) long __cdecl SnapCore_WaitSelection(
    void* handle, unsigned timeoutMs, SnapCoreSelection* result) noexcept {
    return handle ? static_cast<WarmEngine*>(handle)->WaitSelection(timeoutMs, result) : E_INVALIDARG;
}

__declspec(dllexport) void __cdecl SnapCore_CancelSelection(void* handle) noexcept {
    if (handle) static_cast<WarmEngine*>(handle)->CancelSelection();
}

__declspec(dllexport) long __cdecl SnapCore_Crop(
    void* handle, int x, int y, unsigned width, unsigned height,
    unsigned char* destination, size_t destinationBytes,
    long long* submittedAt, long long* pixelsAt) noexcept {
    return handle ? static_cast<WarmEngine*>(handle)->Crop(x, y, width, height,
        destination, destinationBytes, submittedAt, pixelsAt) : E_INVALIDARG;
}

__declspec(dllexport) void __cdecl SnapCore_Cancel(void* handle) noexcept {
    if (handle) static_cast<WarmEngine*>(handle)->Cancel();
}

__declspec(dllexport) void __cdecl SnapCore_Destroy(void* handle) noexcept {
    delete static_cast<WarmEngine*>(handle);
}

__declspec(dllexport) long __cdecl SnapCore_EncodePng(
    const unsigned char* pixels, unsigned width, unsigned height,
    unsigned stride, size_t pixelsBytes,
    unsigned char** output, size_t* outputBytes) noexcept {
    return EncodePngCore(pixels, width, height, stride,
        pixelsBytes, output, outputBytes);
}

__declspec(dllexport) void __cdecl SnapCore_FreeBuffer(void* buffer) noexcept {
    CoTaskMemFree(buffer);
}

}

extern "C" {

// status = 0: eligible single-output SDR fast path; 1: unsupported display
// topology/color; negative HRESULT: DXGI/D3D failure. Unsupported cases are
// expected to keep using the known-good Snipping Tool engine.
__declspec(dllexport) long __cdecl SnapCore_ProbeDisplay(
    SnapCoreDisplayProbe* probe) noexcept {
    if (!probe || probe->abiVersion != 1) return E_INVALIDARG;
    probe->activeOutputCount = 0;
    probe->left = probe->top = probe->right = probe->bottom = 0;
    probe->rotation = probe->colorSpace = 0;
    probe->status = 1;

    ComPtr<IDXGIFactory1> factory;
    auto hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return probe->status = hr;

    ComPtr<IDXGIAdapter1> selectedAdapter;
    ComPtr<IDXGIOutput1> selectedOutput;
    DXGI_OUTPUT_DESC selectedDesc{};
    for (UINT adapterIndex = 0; ; ++adapterIndex) {
        ComPtr<IDXGIAdapter1> adapter;
        hr = factory->EnumAdapters1(adapterIndex, &adapter);
        if (hr == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(hr)) return probe->status = hr;
        for (UINT outputIndex = 0; ; ++outputIndex) {
            ComPtr<IDXGIOutput> output;
            hr = adapter->EnumOutputs(outputIndex, &output);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(hr)) return probe->status = hr;
            DXGI_OUTPUT_DESC desc{};
            hr = output->GetDesc(&desc);
            if (FAILED(hr)) return probe->status = hr;
            if (!desc.AttachedToDesktop) continue;
            ++probe->activeOutputCount;
            if (probe->activeOutputCount == 1) {
                selectedAdapter = adapter;
                selectedDesc = desc;
                hr = output.As(&selectedOutput);
                if (FAILED(hr)) return probe->status = hr;
            }
        }
    }

    if (probe->activeOutputCount != 1 || !selectedOutput) return 1;
    probe->left = selectedDesc.DesktopCoordinates.left;
    probe->top = selectedDesc.DesktopCoordinates.top;
    probe->right = selectedDesc.DesktopCoordinates.right;
    probe->bottom = selectedDesc.DesktopCoordinates.bottom;
    probe->rotation = selectedDesc.Rotation;
    if (selectedDesc.Rotation != DXGI_MODE_ROTATION_IDENTITY) return 1;

    ComPtr<IDXGIOutput6> output6;
    hr = selectedOutput.As(&output6);
    if (FAILED(hr)) return probe->status = hr;
    DXGI_OUTPUT_DESC1 desc1{};
    hr = output6->GetDesc1(&desc1);
    if (FAILED(hr)) return probe->status = hr;
    probe->colorSpace = desc1.ColorSpace;
    if (desc1.ColorSpace != DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709) return 1;

    ComPtr<ID3D11Device> device;
    D3D_FEATURE_LEVEL level{};
    hr = D3D11CreateDevice(selectedAdapter.Get(), D3D_DRIVER_TYPE_UNKNOWN,
        nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
        D3D11_SDK_VERSION, &device, &level, nullptr);
    if (FAILED(hr)) return probe->status = hr;
    ComPtr<IDXGIOutputDuplication> duplication;
    hr = selectedOutput->DuplicateOutput(device.Get(), &duplication);
    if (FAILED(hr)) return probe->status = hr;

    return probe->status = 0;
}

}
