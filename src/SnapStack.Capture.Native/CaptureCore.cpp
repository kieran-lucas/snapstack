#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <thread>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

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
        stop_ = true;
        changed_.notify_all();
        if (worker_.joinable()) worker_.join();
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
        changed_.notify_one();
        return result;
    }

    void Cancel() noexcept {
        {
            std::lock_guard lock(mutex_);
            pinned_ = -1;
            paused_ = false;
        }
        changed_.notify_one();
    }

private:
    struct Slot {
        ComPtr<ID3D11Texture2D> texture;
        long long copiedAt = 0;
    };

    static long long Counter() noexcept {
        LARGE_INTEGER value{};
        QueryPerformanceCounter(&value);
        return value.QuadPart;
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
    std::mutex mutex_;
    std::condition_variable changed_;
    std::atomic<bool> stop_{false};
    HRESULT failure_ = S_OK;
    int latest_ = -1;
    int pinned_ = -1;
    bool paused_ = false;
};

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
