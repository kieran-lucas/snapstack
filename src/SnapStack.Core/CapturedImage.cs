namespace SnapStack.Core;

public sealed record CapturedImage
{
    private readonly Task<ReadOnlyMemory<byte>> _pngTask;

    public Guid Id { get; }
    public int Sequence { get; }
    public DateTimeOffset CapturedAt { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    // Synchronous consumers must not accidentally block the capture/UI path.
    // Clipboard preparation awaits GetPngBytesAsync before using this property.
    public ReadOnlyMemory<byte> PngBytes =>
        _pngTask.IsCompletedSuccessfully
            ? _pngTask.Result
            : throw new InvalidOperationException("PNG encoding is not complete.");

    public CapturedImage(
        Guid id,
        int sequence,
        DateTimeOffset capturedAt,
        int pixelWidth,
        int pixelHeight,
        ReadOnlyMemory<byte> pngBytes)
        : this(id, sequence, capturedAt, pixelWidth, pixelHeight,
            Task.FromResult(pngBytes))
    {
    }

    public CapturedImage(
        Guid id,
        int sequence,
        DateTimeOffset capturedAt,
        int pixelWidth,
        int pixelHeight,
        Task<ReadOnlyMemory<byte>> pngTask)
    {
        Id = id;
        Sequence = sequence;
        CapturedAt = capturedAt;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        _pngTask = pngTask ?? throw new ArgumentNullException(nameof(pngTask));
    }

    public Task<ReadOnlyMemory<byte>> GetPngBytesAsync() => _pngTask;
}
