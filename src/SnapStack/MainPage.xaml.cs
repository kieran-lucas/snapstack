using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnapStack.Capture;
using SnapStack.Core;

namespace SnapStack;

public sealed partial class MainPage : Page
{
    private readonly CaptureSession _session = new();
    private SnippingToolCaptureService? _snippingToolCapture;
    private bool _snipInProgress;

    public MainPage()
    {
        InitializeComponent();

        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;

        RenderSession();
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        _snippingToolCapture = ((App)Application.Current).SnippingToolCapture;
        _snippingToolCapture.CaptureCompleted += SnippingToolCapture_CaptureCompleted;
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_snippingToolCapture is not null)
        {
            _snippingToolCapture.CaptureCompleted -= SnippingToolCapture_CaptureCompleted;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Start();
        FeedbackText.Text = "Session started. Capture a rectangle.";
        RenderSession();
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsActive || _snipInProgress || _snippingToolCapture is null)
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
        DispatcherQueue.TryEnqueue(() =>
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

                    FeedbackText.Text = $"Captured image #{_session.Count}.";
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
        });
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Stop();
        FeedbackText.Text = _session.Count == 0
            ? "Session stopped with no captures."
            : "Session stopped. Captures are ready for the paste pipeline.";
        RenderSession();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Clear();
        FeedbackText.Text = "Session cleared.";
        RenderSession();
    }

    private void RenderSession()
    {
        StatusText.Text = _session.State switch
        {
            CaptureSessionState.Idle => "Idle",
            CaptureSessionState.Capturing => _snipInProgress
                ? "Selecting region"
                : "Capturing",
            CaptureSessionState.Ready => "Ready to paste",
            _ => _session.State.ToString()
        };

        CountText.Text = _session.Count.ToString();

        StartButton.IsEnabled = !_session.IsActive && !_snipInProgress;
        CaptureButton.IsEnabled = _session.IsActive && !_snipInProgress;
        StopButton.IsEnabled = _session.IsActive && !_snipInProgress;
        ClearButton.IsEnabled =
            !_snipInProgress
            && (_session.State != CaptureSessionState.Idle || _session.Count > 0);
    }
}
