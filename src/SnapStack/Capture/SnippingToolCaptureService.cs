using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace SnapStack.Capture;

public sealed class SnippingToolCaptureService
{
    private const string CallbackScheme = "snapstack";
    private const string CallbackHost = "capture-response";

    private string? _pendingCorrelationId;
    private CaptureLatencyTrace? _pendingTrace;

    public event EventHandler<SnippingCaptureResult>? CaptureCompleted;

    public bool IsCapturePending => _pendingCorrelationId is not null;

    public async Task<bool> LaunchRectangleCaptureAsync(CaptureLatencyTrace? trace = null)
    {
        if (IsCapturePending)
        {
            return false;
        }

        var correlationId = Guid.NewGuid().ToString();
        _pendingCorrelationId = correlationId;
        _pendingTrace = trace;

        var uri = new Uri(
            "ms-screenclip://capture/image"
            + "?rectangle"
            + "&enabledModes=RectangleSnip"
            + "&api-version=1.2"
            + "&user-agent=SnapStack"
            + "&redirect-uri=snapstack://capture-response"
            + "&x-request-correlation-id=" + correlationId);

        if (trace is not null)
        {
            trace.LaunchRequested = CaptureLatencyTrace.Now();
        }

        bool launched;
        try
        {
            launched = await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            _pendingCorrelationId = null;
            _pendingTrace = null;
            throw;
        }

        if (trace is not null)
        {
            trace.LaunchReturned = CaptureLatencyTrace.Now();
        }

        if (!launched)
        {
            _pendingCorrelationId = null;
            _pendingTrace = null;
        }

        return launched;
    }

    public async Task<bool> TryHandleProtocolActivationAsync(Uri uri)
    {
        if (!string.Equals(uri.Scheme, CallbackScheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, CallbackHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var activatedAt = CaptureLatencyTrace.Now();

        var query = new WwwFormUrlDecoder(uri.Query);
        var code = GetQueryValue(query, "code");
        var reason = GetQueryValue(query, "reason");
        var correlationId = GetQueryValue(query, "x-request-correlation-id");

        if (_pendingCorrelationId is not null
            && !string.Equals(
                _pendingCorrelationId,
                correlationId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trace = _pendingTrace;
        if (trace is not null)
        {
            trace.ProtocolActivated = activatedAt;
        }

        _pendingCorrelationId = null;
        _pendingTrace = null;

        if (code == "499")
        {
            CaptureCompleted?.Invoke(this, SnippingCaptureResult.Cancelled(trace));
            return true;
        }

        if (code != "200")
        {
            CaptureCompleted?.Invoke(
                this,
                SnippingCaptureResult.Failed(
                    string.IsNullOrWhiteSpace(reason)
                        ? $"Snipping Tool returned status {code ?? "unknown"}."
                        : reason,
                    trace));

            return true;
        }

        var token = GetQueryValue(query, "file-access-token");
        if (string.IsNullOrWhiteSpace(token))
        {
            CaptureCompleted?.Invoke(
                this,
                SnippingCaptureResult.Failed(
                    "Snipping Tool returned success without a file access token.",
                    trace));

            return true;
        }

        try
        {
            var file = await SharedStorageAccessManager.RedeemTokenForFileAsync(token);
            if (trace is not null)
            {
                trace.TokenRedeemed = CaptureLatencyTrace.Now();
            }

            var payload = await ReadCaptureAsync(file);
            if (trace is not null)
            {
                trace.FileRead = CaptureLatencyTrace.Now();
            }

            CaptureCompleted?.Invoke(
                this,
                SnippingCaptureResult.Success(payload, trace));
        }
        catch (Exception exception)
        {
            CaptureCompleted?.Invoke(
                this,
                SnippingCaptureResult.Failed(exception.Message, trace));
        }

        return true;
    }

    private static string? GetQueryValue(
        WwwFormUrlDecoder query,
        string name)
    {
        foreach (var entry in query)
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static async Task<CapturePayload> ReadCaptureAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var pixelWidth = checked((int)decoder.PixelWidth);
        var pixelHeight = checked((int)decoder.PixelHeight);

        var length = checked((int)stream.Size);
        var bytes = new byte[length];

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)length);
        reader.ReadBytes(bytes);

        return new CapturePayload(bytes, pixelWidth, pixelHeight);
    }
}
