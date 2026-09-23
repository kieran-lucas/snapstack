namespace SnapStack.Capture;

// Keeps the known-good protocol path available if a warm DXGI engine fails
// after startup (for example after a display mode change or unlock).
public sealed class FallbackCaptureEngine : ICaptureEngine, IDisposable
{
    private readonly NativeDxgiCaptureEngine _native;
    private readonly SnippingToolCaptureService _snipping;
    private volatile bool _useSnipping;

    public event EventHandler<CaptureEngineResult>? CaptureCompleted;

    public FallbackCaptureEngine(
        NativeDxgiCaptureEngine native,
        SnippingToolCaptureService snipping)
    {
        _native = native;
        _snipping = snipping;
        _native.CaptureCompleted += Native_CaptureCompleted;
        _snipping.CaptureCompleted += Snipping_CaptureCompleted;
    }

    public bool IsCapturePending => _useSnipping
        ? _snipping.IsCapturePending
        : _native.IsCapturePending;

    public async Task<bool> BeginRectangleCaptureAsync(CaptureLatencyTrace? trace)
    {
        if (!_useSnipping)
        {
            if (await _native.BeginRectangleCaptureAsync(trace))
                return true;
            _useSnipping = true;
        }
        return await _snipping.BeginRectangleCaptureAsync(trace);
    }

    private void Native_CaptureCompleted(object? sender, CaptureEngineResult result)
    {
        if (result.ErrorMessage is not null)
            _useSnipping = true;
        CaptureCompleted?.Invoke(this, result);
    }

    private void Snipping_CaptureCompleted(object? sender, CaptureEngineResult result) =>
        CaptureCompleted?.Invoke(this, result);

    public void Dispose()
    {
        _native.CaptureCompleted -= Native_CaptureCompleted;
        _snipping.CaptureCompleted -= Snipping_CaptureCompleted;
        _native.Dispose();
    }
}
