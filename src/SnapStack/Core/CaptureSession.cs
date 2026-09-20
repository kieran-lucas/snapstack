namespace SnapStack.Core;

public sealed class CaptureSession
{
    private readonly List<CapturedImage> _captures = [];

    public CaptureSessionState State { get; private set; } = CaptureSessionState.Idle;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public IReadOnlyList<CapturedImage> Captures => _captures;

    public int Count => _captures.Count;

    public bool IsActive => State == CaptureSessionState.Capturing;

    public void Start()
    {
        _captures.Clear();
        StartedAt = DateTimeOffset.UtcNow;
        EndedAt = null;
        State = CaptureSessionState.Capturing;
    }

    public CapturedImage AddCapture(ReadOnlySpan<byte> pngBytes, int pixelWidth, int pixelHeight)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("A capture session must be active before adding images.");
        }

        if (pngBytes.IsEmpty)
        {
            throw new ArgumentException("Capture data cannot be empty.", nameof(pngBytes));
        }

        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        var capture = new CapturedImage(
            Guid.NewGuid(),
            _captures.Count + 1,
            DateTimeOffset.UtcNow,
            pixelWidth,
            pixelHeight,
            pngBytes.ToArray());

        _captures.Add(capture);
        return capture;
    }

    public void Stop()
    {
        if (!IsActive)
        {
            throw new InvalidOperationException("No capture session is currently active.");
        }

        EndedAt = DateTimeOffset.UtcNow;
        State = CaptureSessionState.Ready;
    }

    public void Clear()
    {
        _captures.Clear();
        StartedAt = null;
        EndedAt = null;
        State = CaptureSessionState.Idle;
    }
}
