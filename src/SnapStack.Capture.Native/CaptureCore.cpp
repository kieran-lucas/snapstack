#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

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
