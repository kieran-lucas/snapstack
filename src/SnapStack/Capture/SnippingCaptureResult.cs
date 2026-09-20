namespace SnapStack.Capture;

public sealed record SnippingCaptureResult(
    CapturePayload? Capture,
    bool IsCancelled,
    string? ErrorMessage)
{
    public static SnippingCaptureResult Success(CapturePayload capture) =>
        new(capture, false, null);

    public static SnippingCaptureResult Cancelled() =>
        new(null, true, null);

    public static SnippingCaptureResult Failed(string message) =>
        new(null, false, message);
}
