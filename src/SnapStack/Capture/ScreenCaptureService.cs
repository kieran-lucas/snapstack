using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace SnapStack.Capture;

public sealed class ScreenCaptureService : IDisposable
{
    private readonly CanvasDevice _canvasDevice = CanvasDevice.GetSharedDevice();

    public bool IsSupported => GraphicsCaptureSession.IsSupported();

    public async Task<CapturePayload?> CaptureAsync(nint ownerWindow)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this device.");
        }

        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);

        var item = await picker.PickSingleItemAsync();
        if (item is null)
        {
            return null;
        }

        return await CaptureItemOnceAsync(item);
    }

    public void Dispose()
    {
        _canvasDevice.Dispose();
    }

    private async Task<CapturePayload> CaptureItemOnceAsync(GraphicsCaptureItem item)
    {
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _canvasDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);

        using var session = framePool.CreateCaptureSession(item);

        var completion = new TaskCompletionSource<CapturePayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var frameClaimed = 0;

        async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (Interlocked.Exchange(ref frameClaimed, 1) != 0)
            {
                return;
            }

            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    Interlocked.Exchange(ref frameClaimed, 0);
                    return;
                }

                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                    _canvasDevice,
                    frame.Surface);

                var pngBytes = await EncodePngAsync(bitmap);
                var contentSize = frame.ContentSize;

                completion.TrySetResult(
                    new CapturePayload(
                        pngBytes,
                        contentSize.Width,
                        contentSize.Height));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        framePool.FrameArrived += OnFrameArrived;
        session.StartCapture();

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
        }
    }

    private static async Task<byte[]> EncodePngAsync(CanvasBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();

        await bitmap.SaveAsync(
            stream,
            CanvasBitmapFileFormat.Png,
            1.0f);

        var length = checked((int)stream.Size);
        var bytes = new byte[length];

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)length);
        reader.ReadBytes(bytes);

        return bytes;
    }
}
