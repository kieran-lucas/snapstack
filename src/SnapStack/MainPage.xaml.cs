using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SnapStack.Capture;
using SnapStack.Clipboard;
using SnapStack.Core;

namespace SnapStack;

public sealed partial class MainPage : Page
{
    private readonly CaptureSession _session = new();
    private readonly ClipboardStackService _clipboardStackService = new();
    private readonly SequentialPasteService _sequentialPasteService = new();

    private SnippingToolCaptureService? _snippingToolCapture;
    private bool _snipInProgress;
    private bool _clipboardPublishInProgress;
    private bool _sequentialPasteInProgress;

    public MainPage()
    {
        InitializeComponent();

        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;

        RenderSession();
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;

        _snippingToolCapture = app.SnippingToolCapture;
        _snippingToolCapture.CaptureCompleted += SnippingToolCapture_CaptureCompleted;
        app.CaptureHotKeyRequested += App_CaptureHotKeyRequested;
        app.PasteHotKeyRequested += App_PasteHotKeyRequested;
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;

        app.CaptureHotKeyRequested -= App_CaptureHotKeyRequested;
        app.PasteHotKeyRequested -= App_PasteHotKeyRequested;

        if (_snippingToolCapture is not null)
        {
            _snippingToolCapture.CaptureCompleted -= SnippingToolCapture_CaptureCompleted;
        }

        app.SetCaptureHotKeyEnabled(false, out _);
        app.SetPasteHotKeyEnabled(false, out _);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Start();

        var app = (App)Application.Current;
        app.SetPasteHotKeyEnabled(false, out _);

        var hotKeyReady = app.SetCaptureHotKeyEnabled(true, out var hotKeyError);

        FeedbackText.Text = hotKeyReady
            ? "Session started. Press Ctrl+Shift+S to capture a rectangle."
            : $"Session started. Capture button is available; hotkey unavailable: {hotKeyError}";

        RenderSession();
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await BeginRectangleCaptureAsync();
    }

    private void App_CaptureHotKeyRequested(object? sender, EventArgs e)
    {
        _ = BeginRectangleCaptureAsync();
    }

    private void App_PasteHotKeyRequested(object? sender, EventArgs e)
    {
        _ = PasteStackFallbackAsync();
    }

    private async Task BeginRectangleCaptureAsync()
    {
        if (!_session.IsActive
            || _snipInProgress
            || _clipboardPublishInProgress
            || _sequentialPasteInProgress
            || _snippingToolCapture is null)
        {
            return;
        }

        _snipInProgress = true;
        FeedbackText.Text = "Select a rectangle in the Snipping Tool overlay.";
        RenderSession();

        try
        {
            var launched = await _snippingToolCapture.LaunchRectangleCaptureAsync();

            if (!launched)
            {
                _snipInProgress = false;
                FeedbackText.Text = "Could not launch Snipping Tool.";
                RenderSession();
            }
        }
        catch (Exception exception)
        {
            _snipInProgress = false;
            FeedbackText.Text = $"Capture failed: {exception.Message}";
            RenderSession();
        }
    }

    private void SnippingToolCapture_CaptureCompleted(
        object? sender,
        SnippingCaptureResult result)
    {
        DispatcherQueue.TryEnqueue(
            () => _ = HandleSnippingCaptureCompletedAsync(result));
    }

    private async Task HandleSnippingCaptureCompletedAsync(
        SnippingCaptureResult result)
    {
        _snipInProgress = false;

        if (result.Capture is not null)
        {
            if (_session.IsActive)
            {
                _session.AddCapture(
                    result.Capture.PngBytes.Span,
                    result.Capture.PixelWidth,
                    result.Capture.PixelHeight);

                FeedbackText.Text =
                    $"Captured image #{_session.Count}. Updating clipboard...";
                RenderSession();

                var clipboardError = await PublishStackToClipboardAsync();
                var app = (App)Application.Current;
                var fallbackReady =
                    app.SetPasteHotKeyEnabled(true, out var fallbackHotKeyError);

                FeedbackText.Text = clipboardError is null
                    ? fallbackReady
                        ? $"Captured image #{_session.Count}. Ctrl+V is ready; Ctrl+Shift+V is the compatibility fallback."
                        : $"Captured image #{_session.Count}. Ctrl+V is ready; fallback hotkey unavailable: {fallbackHotKeyError}"
                    : $"Captured image #{_session.Count}, but clipboard update failed: {clipboardError}";
            }
            else
            {
                FeedbackText.Text = "Capture received after the session ended.";
            }
        }
        else if (result.IsCancelled)
        {
            FeedbackText.Text = "Capture cancelled.";
        }
        else
        {
            FeedbackText.Text = $"Capture failed: {result.ErrorMessage}";
        }

        RenderSession();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).SetCaptureHotKeyEnabled(false, out _);

        _session.Stop();

        if (_session.Count == 0)
        {
            ((App)Application.Current).SetPasteHotKeyEnabled(false, out _);
            FeedbackText.Text = "Session stopped with no captures.";
            RenderSession();
            return;
        }

        FeedbackText.Text = "Finalizing clipboard stack...";
        RenderSession();

        var clipboardError = await PublishStackToClipboardAsync();
        var fallbackReady =
            ((App)Application.Current).SetPasteHotKeyEnabled(
                true,
                out var fallbackHotKeyError);

        FeedbackText.Text = clipboardError is null
            ? fallbackReady
                ? $"Ready. {_session.Count} image{(_session.Count == 1 ? string.Empty : "s")}: Ctrl+V first, Ctrl+Shift+V if needed."
                : $"Ready for Ctrl+V; fallback hotkey unavailable: {fallbackHotKeyError}"
            : $"Session stopped, but clipboard update failed: {clipboardError}";

        RenderSession();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.SetCaptureHotKeyEnabled(false, out _);
        app.SetPasteHotKeyEnabled(false, out _);

        _session.Clear();
        FeedbackText.Text = "Session cleared.";
        RenderSession();
    }

    private async Task PasteStackFallbackAsync()
    {
        if (_session.Count == 0
            || _snipInProgress
            || _clipboardPublishInProgress
            || _sequentialPasteInProgress)
        {
            return;
        }

        _sequentialPasteInProgress = true;
        FeedbackText.Text =
            $"Compatibility paste: sending {_session.Count} image{(_session.Count == 1 ? string.Empty : "s")}...";
        RenderSession();

        string? pasteError = null;

        try
        {
            await _sequentialPasteService.PasteAsync(_session.Captures);
        }
        catch (Exception exception)
        {
            pasteError = exception.Message;
        }

        // Sequential fallback temporarily replaces the clipboard with each
        // individual image. Restore the preferred multi-format stack after it.
        var restoreError = await PublishStackToClipboardAsync();

        _sequentialPasteInProgress = false;

        FeedbackText.Text = pasteError is not null
            ? $"Compatibility paste failed: {pasteError}"
            : restoreError is not null
                ? $"Images were pasted, but restoring the full clipboard stack failed: {restoreError}"
                : $"Compatibility paste sent {_session.Count} image{(_session.Count == 1 ? string.Empty : "s")}. Full stack restored to clipboard.";

        RenderSession();
    }

    private async Task<string?> PublishStackToClipboardAsync()
    {
        if (_session.Count == 0)
        {
            return null;
        }

        _clipboardPublishInProgress = true;
        RenderSession();

        try
        {
            await _clipboardStackService.PublishAsync(_session.Captures);
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
        finally
        {
            _clipboardPublishInProgress = false;
            RenderSession();
        }
    }

    private void RenderSession()
    {
        StatusText.Text = _session.State switch
        {
            CaptureSessionState.Idle => "Idle",
            _ when _sequentialPasteInProgress => "Compatibility paste",
            CaptureSessionState.Capturing when _snipInProgress => "Selecting region",
            CaptureSessionState.Capturing when _clipboardPublishInProgress => "Updating clipboard",
            CaptureSessionState.Capturing => "Capturing",
            CaptureSessionState.Ready when _clipboardPublishInProgress => "Finalizing clipboard",
            CaptureSessionState.Ready => "Ready to paste",
            _ => _session.State.ToString()
        };

        CountText.Text = _session.Count.ToString();

        var busy =
            _snipInProgress
            || _clipboardPublishInProgress
            || _sequentialPasteInProgress;

        ActivityRing.IsActive = busy;
        ActivityRing.Visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;

        var statusBrush = _session.State switch
        {
            CaptureSessionState.Capturing when _snipInProgress =>
                GetBrush("AppCyanBrush"),
            CaptureSessionState.Capturing =>
                GetBrush("AppSuccessBrush"),
            CaptureSessionState.Ready =>
                GetBrush("AppAccentBrightBrush"),
            _ =>
                GetBrush("AppTextMutedBrush")
        };

        StatusDot.Fill = statusBrush;
        StatusIcon.Foreground = statusBrush;

        FeedbackIcon.Foreground = busy
            ? GetBrush("AppCyanBrush")
            : _session.State == CaptureSessionState.Ready
                ? GetBrush("AppSuccessBrush")
                : GetBrush("AppAccentBrightBrush");

        StartButton.IsEnabled = !_session.IsActive && !busy;
        CaptureButton.IsEnabled = _session.IsActive && !busy;
        StopButton.IsEnabled = _session.IsActive && !busy;
        ClearButton.IsEnabled =
            !busy
            && (_session.State != CaptureSessionState.Idle || _session.Count > 0);
    }

    private static Brush GetBrush(string resourceKey)
    {
        return (Brush)Application.Current.Resources[resourceKey];
    }
}
