using Avalonia;
#if WINDOWS
using Ziwa.Platform.Windows.SingleInstance;
#endif

namespace Ziwa.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.StartupArgs = args;

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var singleInstance = new WindowsSingleInstanceCoordinator();
            if (!singleInstance.TryAcquireOrForward(args, out bool forwardedToPrimary))
            {
                singleInstance.Dispose();
                if (forwardedToPrimary)
                {
                    return;
                }
            }
            else
            {
                App.PrimarySingleInstanceCoordinator = singleInstance;
            }
        }
#endif

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            (App.PrimarySingleInstanceCoordinator as IDisposable)?.Dispose();
            App.PrimarySingleInstanceCoordinator = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
