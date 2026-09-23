namespace SnapStack.Capture;

public sealed record SnippingCaptureResult(
    CapturePayload? Capture,
    bool IsCancelled,
    string? ErrorMessage,
    CaptureLatencyTrace? LatencyTrace)
{
    public static SnippingCaptureResult Success(
        CapturePayload capture,
        CaptureLatencyTrace? trace = null) =>
        new(capture, false, null, trace);

    public static SnippingCaptureResult Cancelled(CaptureLatencyTrace? trace = null) =>
        new(null, true, null, trace);

    public static SnippingCaptureResult Failed(
        string message,
        CaptureLatencyTrace? trace = null) =>
        new(null, false, message, trace);
}
