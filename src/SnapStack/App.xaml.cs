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

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        EnsureMainWindow();
        SubscribeToActivation();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        HandleActivation(activation);
    }

    private void EnsureMainWindow()
    {
        if (MainWindowInstance is not null)
        {
            return;
        }

        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }

    private void SubscribeToActivation()
    {
        if (_activationSubscribed)
        {
            return;
        }

        AppInstance.GetCurrent().Activated += OnActivated;
        _activationSubscribed = true;
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        EnsureMainWindow();
        MainWindowInstance?.Activate();
        HandleActivation(args);
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
