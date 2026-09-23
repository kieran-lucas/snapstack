namespace SnapStack.Capture;

// A selection-based engine delivers its result asynchronously. The Snipping
// Tool implementation uses a protocol callback; native engines can complete
// through their own overlay without changing the session/clipboard pipeline.
public interface ICaptureEngine
{
    event EventHandler<CaptureEngineResult>? CaptureCompleted;

    bool IsCapturePending { get; }

    Task<bool> BeginRectangleCaptureAsync(CaptureLatencyTrace? trace);
}
