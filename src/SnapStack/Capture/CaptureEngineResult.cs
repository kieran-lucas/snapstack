namespace SnapStack.Capture;

public sealed record CaptureEngineResult(
    CapturePayload? Capture,
    bool IsCancelled,
    string? ErrorMessage,
    CaptureLatencyTrace? LatencyTrace)
{
    public static CaptureEngineResult Success(
        CapturePayload capture,
        CaptureLatencyTrace? trace = null) =>
        new(capture, false, null, trace);

    public static CaptureEngineResult Cancelled(CaptureLatencyTrace? trace = null) =>
        new(null, true, null, trace);

    public static CaptureEngineResult Failed(
        string message,
        CaptureLatencyTrace? trace = null) =>
        new(null, false, message, trace);
}
