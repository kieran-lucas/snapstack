using Microsoft.UI.Xaml;
using SnapStack.Input;

namespace SnapStack;

public sealed partial class MainWindow : Window
{
    private const int CaptureHotKeyId = 1;
    private const uint VirtualKeyS = 0x53;

    private readonly GlobalHotKeyService _hotKeyService;
    private bool _captureHotKeyEnabled;

    public MainWindow()
    {
        InitializeComponent();
        RootFrame.Navigate(typeof(MainPage));

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hotKeyService = new GlobalHotKeyService(hwnd);
        _hotKeyService.HotKeyPressed += HotKeyService_HotKeyPressed;

        Closed += MainWindow_Closed;
    }

    internal bool SetCaptureHotKeyEnabled(bool enabled, out string? error)
    {
        error = null;

        if (enabled == _captureHotKeyEnabled)
        {
            return true;
        }

        try
        {
            if (enabled)
            {
                _hotKeyService.Register(
                    CaptureHotKeyId,
                    HotKeyModifiers.Control
                        | HotKeyModifiers.Shift
                        | HotKeyModifiers.NoRepeat,
                    VirtualKeyS);
            }
            else
            {
                _hotKeyService.Unregister();
            }

            _captureHotKeyEnabled = enabled;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void HotKeyService_HotKeyPressed(object? sender, EventArgs e)
    {
        ((App)Application.Current).RaiseCaptureHotKeyRequested();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _hotKeyService.HotKeyPressed -= HotKeyService_HotKeyPressed;
        _hotKeyService.Dispose();
    }
}
