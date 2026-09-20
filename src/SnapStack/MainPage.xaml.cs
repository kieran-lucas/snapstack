using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnapStack.Capture;
using SnapStack.Core;

namespace SnapStack;

public sealed partial class MainPage : Page
{
    private readonly CaptureSession _session = new();
    private readonly ScreenCaptureService _captureService = new();

    public MainPage()
    {
        InitializeComponent();
        RenderSession();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Start();
        FeedbackText.Text = "Session started. Capture a window or display.";
        RenderSession();
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsActive)
        {
            return;
        }

        CaptureButton.IsEnabled = false;
        FeedbackText.Text = "Choose a window or display to capture.";

        try
        {
            var app = (App)Application.Current;
            var window = app.MainWindowInstance
                ?? throw new InvalidOperationException("The main window is not available.");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var capture = await _captureService.CaptureAsync(hwnd);

            if (capture is null)
            {
                FeedbackText.Text = "Capture cancelled.";
                return;
            }

            _session.AddCapture(
                capture.PngBytes.Span,
                capture.PixelWidth,
                capture.PixelHeight);

            FeedbackText.Text = $"Captured image #{_session.Count}.";
        }
        catch (Exception exception)
        {
            FeedbackText.Text = $"Capture failed: {exception.Message}";
        }
        finally
        {
            RenderSession();
        }
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
            CaptureSessionState.Capturing => "Capturing",
            CaptureSessionState.Ready => "Ready to paste",
            _ => _session.State.ToString()
        };

        CountText.Text = _session.Count.ToString();

        StartButton.IsEnabled = !_session.IsActive;
        CaptureButton.IsEnabled = _session.IsActive;
        StopButton.IsEnabled = _session.IsActive;
        ClearButton.IsEnabled = _session.State != CaptureSessionState.Idle || _session.Count > 0;
    }
}
