namespace SnapStack.Capture;

public sealed record CapturePayload(
    ReadOnlyMemory<byte> PngBytes,
    int PixelWidth,
    int PixelHeight);
