namespace SnapStack.Core;

public sealed record CapturedImage(
    Guid Id,
    int Sequence,
    DateTimeOffset CapturedAt,
    int PixelWidth,
    int PixelHeight,
    ReadOnlyMemory<byte> PngBytes);
