using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SnapStack.Capture;
using Windows.ApplicationModel.Activation;

namespace SnapStack;

public partial class App : Application
{
    private bool _activationSubscribed;

    public Window? MainWindowInstance { get; private set; }

    public SnippingToolCaptureService SnippingToolCapture { get; } = new();

    private ICaptureEngine? _captureEngine;

    public ICaptureEngine CaptureEngine => _captureEngine ??= CreateCaptureEngine();

    public event EventHandler<CaptureHotKeyRequestedEventArgs>? CaptureHotKeyRequested;
    public event EventHandler? EndHotKeyRequested;
    public event EventHandler? PasteHotKeyRequested;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        EnsureMainWindow();
        SubscribeToActivation();

        if (Program.InitialActivation is not null)
        {
            HandleActivation(Program.InitialActivation);
        }
    }

    internal bool SetCaptureHotKeyEnabled(bool enabled, out string? error)
    {
        if (MainWindowInstance is not MainWindow window)
        {
            error = "The main window is not available.";
            return false;
        }

        return window.SetCaptureHotKeyEnabled(enabled, out error);
    }

    internal bool SetPasteHotKeyEnabled(bool enabled, out string? error)
    {
        if (MainWindowInstance is not MainWindow window)
        {
            error = "The main window is not available.";
            return false;
        }

        return window.SetPasteHotKeyEnabled(enabled, out error);
    }

    internal bool SetEndHotKeyEnabled(bool enabled, out string? error)
    {
        if (MainWindowInstance is not MainWindow window)
        {
            error = "The main window is not available.";
            return false;
        }

        return window.SetEndHotKeyEnabled(enabled, out error);
    }

    internal void RaiseCaptureHotKeyRequested(long detectedAt)
    {
        CaptureHotKeyRequested?.Invoke(this, new CaptureHotKeyRequestedEventArgs(detectedAt));
    }

    internal void RaiseEndHotKeyRequested()
    {
        EndHotKeyRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void RaisePasteHotKeyRequested()
    {
        PasteHotKeyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureMainWindow()
    {
        if (MainWindowInstance is not null)
        {
            return;
        }

        MainWindowInstance = new MainWindow();
        MainWindowInstance.Closed += (_, _) => (_captureEngine as IDisposable)?.Dispose();
        MainWindowInstance.Activate();
    }

    private ICaptureEngine CreateCaptureEngine()
    {
        var selected = Environment.GetEnvironmentVariable("SNAPSTACK_CAPTURE_ENGINE");
        if (!string.Equals(selected, "snipping", StringComparison.OrdinalIgnoreCase))
        {
            var native = NativeDxgiCaptureEngine.TryCreate();
            if (native is not null)
            {
                return new FallbackCaptureEngine(native, SnippingToolCapture);
            }
        }

        return SnippingToolCapture;
    }

    private void SubscribeToActivation()
    {
        if (_activationSubscribed || Program.PrimaryInstance is null)
        {
            return;
        }

        Program.PrimaryInstance.Activated += OnActivated;
        _activationSubscribed = true;
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
        {
            EnsureMainWindow();

            if (args.Kind != ExtendedActivationKind.Protocol)
            {
                MainWindowInstance?.Activate();
            }

            HandleActivation(args);
        });
    }

    private void HandleActivation(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.Protocol)
        {
            return;
        }

        if (args.Data is IProtocolActivatedEventArgs protocolArgs)
        {
            _ = SnippingToolCapture.TryHandleProtocolActivationAsync(protocolArgs.Uri);
        }
    }
}

public sealed record CaptureHotKeyRequestedEventArgs(long DetectedAt);
