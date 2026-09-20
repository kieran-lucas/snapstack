using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnapStack.Core;

namespace SnapStack;

public sealed partial class MainPage : Page
{
    private readonly CaptureSession _session = new();

    public MainPage()
    {
        InitializeComponent();
        RenderSession();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Start();
        RenderSession();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Stop();
        RenderSession();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Clear();
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
        StopButton.IsEnabled = _session.IsActive;
        ClearButton.IsEnabled = _session.State != CaptureSessionState.Idle || _session.Count > 0;
    }
}
