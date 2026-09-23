#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <numeric>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {

constexpr unsigned kIterations = 100;
constexpr unsigned kCropWidth = 800;
constexpr unsigned kCropHeight = 500;

struct Sample {
    double waitMs = 0;
    double copyMs = 0;
    double readMs = 0;
    double totalMs = 0;
};

double milliseconds(LARGE_INTEGER start, LARGE_INTEGER end) {
    static LARGE_INTEGER frequency = [] {
        LARGE_INTEGER result{};
        QueryPerformanceFrequency(&result);
        return result;
    }();
    return (end.QuadPart - start.QuadPart) * 1000.0 / frequency.QuadPart;
}

LARGE_INTEGER stamp() {
    LARGE_INTEGER result{};
    QueryPerformanceCounter(&result);
    return result;
}

void check(HRESULT hr, const char* action) {
    if (FAILED(hr)) {
        char message[256];
        sprintf_s(message, "%s failed: 0x%08X", action, static_cast<unsigned>(hr));
        throw std::runtime_error(message);
    }
}

void print(const char* name, const std::vector<double>& values) {
    if (values.empty()) {
        printf("%-24s no samples\n", name);
        return;
    }
    auto sorted = values;
    std::sort(sorted.begin(), sorted.end());
    auto percentile = [&](double p) {
        return sorted[static_cast<size_t>(std::ceil(p * sorted.size())) - 1];
    };
    printf("%-24s n=%3zu min=%7.3f median=%7.3f p90=%7.3f p95=%7.3f p99=%7.3f max=%7.3f ms\n",
        name, sorted.size(), sorted.front(), percentile(.5), percentile(.9),
        percentile(.95), percentile(.99), sorted.back());
}

void report(const char* engine, const std::vector<Sample>& samples) {
    std::vector<double> wait, copy, read, total;
    for (auto& s : samples) {
        wait.push_back(s.waitMs);
        copy.push_back(s.copyMs);
        read.push_back(s.readMs);
        total.push_back(s.totalMs);
    }
    printf("\n%s\n", engine);
    print("wait for new frame", wait);
    print("GPU crop / BitBlt issue", copy);
    print("CPU readback / DIB", read);
    print("total", total);
}

std::atomic<unsigned> paintCount{0};

LRESULT CALLBACK stimulusProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_PAINT) {
        PAINTSTRUCT ps{};
        auto dc = BeginPaint(hwnd, &ps);
        auto color = paintCount.fetch_add(1) % 2 ? RGB(0, 0, 0) : RGB(255, 255, 255);
        auto brush = CreateSolidBrush(color);
        FillRect(dc, &ps.rcPaint, brush);
        DeleteObject(brush);
        EndPaint(hwnd, &ps);
        return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

struct Stimulus {
    HWND hwnd{};
    explicit Stimulus(RECT monitor) {
        WNDCLASSW klass{};
        klass.lpfnWndProc = stimulusProc;
        klass.hInstance = GetModuleHandleW(nullptr);
        klass.lpszClassName = L"SnapStackCaptureBenchmarkStimulus";
        RegisterClassW(&klass);
        hwnd = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            klass.lpszClassName, L"", WS_POPUP | WS_VISIBLE,
            monitor.right - 32, monitor.bottom - 32, 16, 16,
            nullptr, nullptr, klass.hInstance, nullptr);
        if (!hwnd) throw std::runtime_error("Could not create stimulus window");
        update();
    }
    ~Stimulus() { DestroyWindow(hwnd); }
    void update() const {
        InvalidateRect(hwnd, nullptr, FALSE);
        UpdateWindow(hwnd);
    }
};

struct Graphics {
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    com_ptr<IDXGIOutput1> output;
    HMONITOR monitor{};
    RECT bounds{};
    std::wstring adapterName;
    DWORD refreshHz{};

    Graphics() {
        monitor = MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
        com_ptr<IDXGIFactory1> factory;
        check(CreateDXGIFactory1(__uuidof(IDXGIFactory1), factory.put_void()), "CreateDXGIFactory1");
        for (UINT a = 0; ; ++a) {
            com_ptr<IDXGIAdapter1> adapter;
            if (factory->EnumAdapters1(a, adapter.put()) == DXGI_ERROR_NOT_FOUND) break;
            for (UINT o = 0; ; ++o) {
                com_ptr<IDXGIOutput> candidate;
                if (adapter->EnumOutputs(o, candidate.put()) == DXGI_ERROR_NOT_FOUND) break;
                DXGI_OUTPUT_DESC desc{};
                check(candidate->GetDesc(&desc), "GetDesc");
                if (desc.Monitor != monitor) continue;
                bounds = desc.DesktopCoordinates;
                DXGI_ADAPTER_DESC1 adapterDesc{};
                check(adapter->GetDesc1(&adapterDesc), "Adapter GetDesc1");
                adapterName = adapterDesc.Description;
                DEVMODEW displayMode{};
                displayMode.dmSize = sizeof(displayMode);
                if (EnumDisplaySettingsW(desc.DeviceName, ENUM_CURRENT_SETTINGS, &displayMode))
                    refreshHz = displayMode.dmDisplayFrequency;
                candidate.as(output);
                D3D_FEATURE_LEVEL level{};
                check(D3D11CreateDevice(adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                    device.put(), &level, context.put()), "D3D11CreateDevice");
                return;
            }
        }
        throw std::runtime_error("Primary monitor has no DXGI output");
    }

    com_ptr<ID3D11Texture2D> texture(UINT width, UINT height, D3D11_USAGE usage) const {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = usage;
        desc.CPUAccessFlags = usage == D3D11_USAGE_STAGING ? D3D11_CPU_ACCESS_READ : 0;
        com_ptr<ID3D11Texture2D> result;
        check(device->CreateTexture2D(&desc, nullptr, result.put()), "CreateTexture2D");
        return result;
    }
};

struct Cropper {
    Graphics& graphics;
    com_ptr<ID3D11Texture2D> staging;
    UINT width;
    UINT height;
    std::vector<unsigned char> pixels;
    volatile unsigned checksum = 0;

    Cropper(Graphics& graphics, UINT width, UINT height)
        : graphics(graphics), staging(graphics.texture(width, height, D3D11_USAGE_STAGING)),
          width(width), height(height), pixels(static_cast<size_t>(width) * height * 4) {}

    void crop(ID3D11Texture2D* source, Sample& sample) {
        D3D11_TEXTURE2D_DESC desc{};
        source->GetDesc(&desc);
        D3D11_BOX box{0, 0, 0, width, height, 1};
        if (desc.Width < width || desc.Height < height || desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
            throw std::runtime_error("Unexpected source texture dimensions or format");
        auto copyStart = stamp();
        graphics.context->CopySubresourceRegion(staging.get(), 0, 0, 0, 0, source, 0, &box);
        auto copyEnd = stamp();
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check(graphics.context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped), "Map staging");
        auto sourcePixels = static_cast<const unsigned char*>(mapped.pData);
        for (UINT row = 0; row < height; ++row) {
            std::memcpy(pixels.data() + static_cast<size_t>(row) * width * 4,
                sourcePixels + static_cast<size_t>(row) * mapped.RowPitch,
                static_cast<size_t>(width) * 4);
        }
        checksum = pixels[0];
        graphics.context->Unmap(staging.get(), 0);
        auto readEnd = stamp();
        sample.copyMs = milliseconds(copyStart, copyEnd);
        sample.readMs = milliseconds(copyEnd, readEnd);
    }
};

void benchmarkGdi(const Graphics& graphics, Stimulus& stimulus) {
    auto screen = GetDC(nullptr);
    auto dc = CreateCompatibleDC(screen);
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = kCropWidth;
    info.bmiHeader.biHeight = -static_cast<LONG>(kCropHeight);
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    info.bmiHeader.biCompression = BI_RGB;
    void* pixels{};
    auto dib = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    if (!dc || !dib) throw std::runtime_error("Could not create GDI DIB");
    auto previous = SelectObject(dc, dib);
    std::vector<Sample> samples;
    for (unsigned i = 0; i < kIterations + 5; ++i) {
        stimulus.update();
        auto start = stamp();
        if (!BitBlt(dc, 0, 0, kCropWidth, kCropHeight, screen,
            graphics.bounds.left, graphics.bounds.top, SRCCOPY | CAPTUREBLT))
            throw std::runtime_error("BitBlt failed");
        auto copied = stamp();
        volatile unsigned value = static_cast<unsigned char*>(pixels)[0];
        (void)value;
        auto end = stamp();
        if (i >= 5) samples.push_back({0, milliseconds(start, copied),
            milliseconds(copied, end), milliseconds(start, end)});
    }
    SelectObject(dc, previous);
    DeleteObject(dib);
    DeleteDC(dc);
    ReleaseDC(nullptr, screen);
    report("GDI BitBlt (warm DIB, selected region)", samples);
}

void benchmarkDuplication(Graphics& graphics, Stimulus& stimulus, bool persistent) {
    com_ptr<IDXGIOutputDuplication> duplication;
    check(graphics.output->DuplicateOutput(graphics.device.get(), duplication.put()), "DuplicateOutput");
    auto frameWidth = static_cast<UINT>(graphics.bounds.right - graphics.bounds.left);
    auto frameHeight = static_cast<UINT>(graphics.bounds.bottom - graphics.bounds.top);
    auto retained = persistent ? graphics.texture(frameWidth, frameHeight, D3D11_USAGE_DEFAULT) : nullptr;
    Cropper cropper(graphics, kCropWidth, kCropHeight);
    std::vector<Sample> samples;
    for (unsigned i = 0; i < kIterations + 5; ++i) {
        stimulus.update();
        auto start = stamp();
        DXGI_OUTDUPL_FRAME_INFO frameInfo{};
        com_ptr<IDXGIResource> resource;
        auto hr = duplication->AcquireNextFrame(1000, &frameInfo, resource.put());
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue;
        check(hr, "AcquireNextFrame");
        auto acquired = stamp();
        com_ptr<ID3D11Texture2D> source;
        resource.as(source);
        Sample sample;
        sample.waitMs = milliseconds(start, acquired);
        if (persistent) {
            graphics.context->CopyResource(retained.get(), source.get());
            graphics.context->Flush();
            check(duplication->ReleaseFrame(), "ReleaseFrame");
            cropper.crop(retained.get(), sample);
        } else {
            cropper.crop(source.get(), sample);
            check(duplication->ReleaseFrame(), "ReleaseFrame");
        }
        sample.totalMs = milliseconds(start, stamp());
        if (i >= 5) samples.push_back(sample);
    }
    report(persistent ? "DXGI persistent latest-frame copy" : "DXGI on-demand AcquireNextFrame", samples);
    if (persistent && !samples.empty()) {
        std::vector<Sample> snapshotSamples;
        for (unsigned i = 0; i < kIterations; ++i) {
            Sample sample;
            auto start = stamp();
            cropper.crop(retained.get(), sample);
            sample.totalMs = milliseconds(start, stamp());
            snapshotSamples.push_back(sample);
        }
        report("DXGI warm snapshot (frame already resident)", snapshotSamples);
    }
}

void benchmarkWgc(Graphics& graphics, Stimulus& stimulus) {
    auto interop = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{nullptr};
    check(interop->CreateForMonitor(graphics.monitor, guid_of<GraphicsCaptureItem>(),
        put_abi(item)), "GraphicsCaptureItem CreateForMonitor");
    com_ptr<IDXGIDevice> dxgiDevice;
    graphics.device.as(dxgiDevice);
    com_ptr<::IInspectable> inspectable;
    check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.get(), inspectable.put()),
        "CreateDirect3D11DeviceFromDXGIDevice");
    auto winrtDevice = inspectable.as<IDirect3DDevice>();
    auto size = item.Size();
    auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(winrtDevice,
        DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, size);
    auto session = pool.CreateCaptureSession(item);
    session.IsCursorCaptureEnabled(false);
    std::mutex mutex;
    std::condition_variable arrived;
    unsigned generation = 0;
    auto token = pool.FrameArrived([&](auto&&, auto&&) {
        std::lock_guard guard(mutex);
        ++generation;
        arrived.notify_one();
    });
    session.StartCapture();
    Cropper cropper(graphics, kCropWidth, kCropHeight);
    std::vector<Sample> samples;
    for (unsigned i = 0; i < kIterations + 5; ++i) {
        unsigned previous;
        {
            std::lock_guard guard(mutex);
            previous = generation;
        }
        stimulus.update();
        auto start = stamp();
        {
            std::unique_lock lock(mutex);
            if (!arrived.wait_for(lock, std::chrono::seconds(1), [&] { return generation != previous; }))
                continue;
        }
        auto frame = pool.TryGetNextFrame();
        if (!frame) continue;
        auto acquired = stamp();
        com_ptr<ID3D11Texture2D> source;
        using DxgiAccess = ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;
        com_ptr<DxgiAccess> access;
        check(get_unknown(frame.Surface())->QueryInterface(
            __uuidof(DxgiAccess), access.put_void()),
            "WGC surface interface access");
        check(access->GetInterface(__uuidof(ID3D11Texture2D), source.put_void()),
            "WGC surface as texture");
        Sample sample;
        sample.waitMs = milliseconds(start, acquired);
        cropper.crop(source.get(), sample);
        sample.totalMs = milliseconds(start, stamp());
        if (i >= 5) samples.push_back(sample);
    }
    pool.FrameArrived(token);
    session.Close();
    pool.Close();
    report("Windows.Graphics.Capture monitor", samples);
}

} // namespace

int main() {
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        Graphics graphics;
        printf("SnapStack capture API benchmark | QPC | %u iterations | crop %ux%u | monitor %ldx%ld\n",
            kIterations, kCropWidth, kCropHeight,
            graphics.bounds.right - graphics.bounds.left,
            graphics.bounds.bottom - graphics.bounds.top);
        printf("Primary output adapter: %ls\n", graphics.adapterName.c_str());
        printf("Reported display refresh: %lu Hz\n", graphics.refreshHz);
        Stimulus stimulus(graphics.bounds);
        benchmarkGdi(graphics, stimulus);
        benchmarkDuplication(graphics, stimulus, false);
        benchmarkDuplication(graphics, stimulus, true);
        try {
            benchmarkWgc(graphics, stimulus);
        } catch (const std::exception& error) {
            printf("WGC unavailable: %s\n", error.what());
        }
        return 0;
    } catch (const std::exception& error) {
        fprintf(stderr, "Benchmark failed: %s\n", error.what());
        return 1;
    }
}
