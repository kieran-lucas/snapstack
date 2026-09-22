using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private bool _stackPasteInProgress;

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
        app.EndHotKeyRequested += App_EndHotKeyRequested;
        app.PasteHotKeyRequested += App_PasteHotKeyRequested;
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;

        app.CaptureHotKeyRequested -= App_CaptureHotKeyRequested;
        app.EndHotKeyRequested -= App_EndHotKeyRequested;
        app.PasteHotKeyRequested -= App_PasteHotKeyRequested;

        if (_snippingToolCapture is not null)
        {
            _snippingToolCapture.CaptureCompleted -= SnippingToolCapture_CaptureCompleted;
        }

        app.SetCaptureHotKeyEnabled(false, out _);
        app.SetEndHotKeyEnabled(false, out _);
        app.SetPasteHotKeyEnabled(false, out _);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartSession();
    }

    private void StartKeyboardAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (StartButton.IsEnabled)
        {
            StartSession();
            args.Handled = true;
        }
    }

    private void StartSession()
    {
        _session.Start();

        var app = (App)Application.Current;
        app.SetPasteHotKeyEnabled(false, out _);

        var captureHotKeyReady =
            app.SetCaptureHotKeyEnabled(true, out var captureHotKeyError);
        var endHotKeyReady =
            app.SetEndHotKeyEnabled(true, out var endHotKeyError);

        FeedbackText.Text = captureHotKeyReady && endHotKeyReady
            ? "Session started. Ctrl+Z captures; Ctrl+X ends the session."
            : $"Session started. Shortcut issue: {captureHotKeyError ?? endHotKeyError}";

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

    private void App_EndHotKeyRequested(object? sender, EventArgs e)
    {
        _ = StopSessionAsync();
    }

    private void App_PasteHotKeyRequested(object? sender, EventArgs e)
    {
        _ = PasteStackAsync();
    }

    private async Task BeginRectangleCaptureAsync()
    {
        if (!_session.IsActive
            || _snipInProgress
            || _clipboardPublishInProgress
            || _stackPasteInProgress
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

                FeedbackText.Text = clipboardError is null
                    ? $"Captured image #{_session.Count}. Press Ctrl+Z for another, or Ctrl+X to finish."
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
        await StopSessionAsync();
    }

    private async Task StopSessionAsync()
    {
        if (!_session.IsActive
            || _snipInProgress
            || _clipboardPublishInProgress
            || _stackPasteInProgress)
        {
            return;
        }

        var app = (App)Application.Current;
        app.SetCaptureHotKeyEnabled(false, out _);
        app.SetEndHotKeyEnabled(false, out _);

        _session.Stop();

        if (_session.Count == 0)
        {
            app.SetPasteHotKeyEnabled(false, out _);
            FeedbackText.Text = "Session stopped with no captures.";
            RenderSession();
            return;
        }

        FeedbackText.Text = "Finalizing clipboard stack...";
        RenderSession();

        var clipboardError = await PublishStackToClipboardAsync();
        var pasteHotKeyReady =
            app.SetPasteHotKeyEnabled(
                true,
                out var pasteHotKeyError);

        FeedbackText.Text = clipboardError is null
            ? pasteHotKeyReady
                ? $"Ready. Press Ctrl+V once to paste all {_session.Count} image{(_session.Count == 1 ? string.Empty : "s")} in order."
                : $"Stack is ready, but Ctrl+V could not be registered: {pasteHotKeyError}"
            : $"Session stopped, but clipboard update failed: {clipboardError}";

        RenderSession();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.SetCaptureHotKeyEnabled(false, out _);
        app.SetEndHotKeyEnabled(false, out _);
        app.SetPasteHotKeyEnabled(false, out _);

        _session.Clear();
        FeedbackText.Text = "Session cleared.";
        RenderSession();
    }

    private async Task PasteStackAsync()
    {
        var app = (App)Application.Current;

        if (_session.State != CaptureSessionState.Ready
            || _session.Count == 0
            || _snipInProgress
            || _clipboardPublishInProgress
            || _stackPasteInProgress)
        {
            // MainWindow releases the Ctrl+V registration before dispatching
            // this request. Restore it if the request cannot run.
            if (_session.State == CaptureSessionState.Ready
                && _session.Count > 0)
            {
                app.SetPasteHotKeyEnabled(true, out _);
            }

            return;
        }

        _stackPasteInProgress = true;
        FeedbackText.Text =
            $"Pasting {_session.Count} image{(_session.Count == 1 ? string.Empty : "s")} in order...";
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

        // Sequential paste temporarily replaces the clipboard with each image.
        // Restore the full multi-format stack so it remains available later.
        var restoreError = await PublishStackToClipboardAsync();

        _stackPasteInProgress = false;

        var pasteHotKeyReady =
            app.SetPasteHotKeyEnabled(true, out var pasteHotKeyError);

        FeedbackText.Text = pasteError is not null
            ? $"Stack paste failed: {pasteError}"
            : restoreError is not null
                ? $"Images were pasted, but restoring the full clipboard stack failed: {restoreError}"
                : !pasteHotKeyReady
                    ? $"Images were pasted, but Ctrl+V could not be re-registered: {pasteHotKeyError}"
                    : $"Pasted {_session.Count} image{(_session.Count == 1 ? string.Empty : "s")} in order. Ctrl+V is ready to paste the stack again.";

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
            _ when _stackPasteInProgress => "Pasting stack",
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
            || _stackPasteInProgress;

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
