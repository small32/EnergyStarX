using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace EnergyStarX;

// Single instance mode:
// https://blogs.windows.com/windowsdeveloper/2022/01/28/making-the-app-single-instanced-part-3/
public static class Program
{
    // Note that [STAThread] doesn't work with "async Task Main(string[] args)"
    // https://github.com/dotnet/roslyn/issues/22112
    [STAThread]
    private static void Main(string[] args)
    {
        AppInstance mainAppInstance = AppInstance.FindOrRegisterForKey(App.Guid);
        if (!mainAppInstance.IsCurrent)
        {
            // Redirect this activation to the running instance, so that its app window is brought to front.
            // The app may be running with a hidden app window or a hidden system tray icon, so we need this to show it again.
            AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(async () => await mainAppInstance.RedirectActivationToAsync(activationArgs)).GetAwaiter().GetResult();

            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(p =>
        {
            DispatcherQueueSynchronizationContext? context = new(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            new App();
        });
    }
}
