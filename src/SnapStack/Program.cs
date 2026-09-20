using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace SnapStack;

public static class Program
{
    private const string InstanceKey = "SnapStack.Main";

    internal static AppInstance? PrimaryInstance { get; private set; }

    internal static AppActivationArguments? InitialActivation { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primaryInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (!primaryInstance.IsCurrent)
        {
            RedirectActivation(activation, primaryInstance);
            return;
        }

        PrimaryInstance = primaryInstance;
        InitialActivation = activation;

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());

            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static void RedirectActivation(
        AppActivationArguments activation,
        AppInstance primaryInstance)
    {
        using var completed = new Semaphore(0, 1);

        Task.Run(() =>
        {
            try
            {
                primaryInstance
                    .RedirectActivationToAsync(activation)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                completed.Release();
            }
        });

        completed.WaitOne();
    }
}
