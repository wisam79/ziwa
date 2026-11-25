using Microsoft.Extensions.DependencyInjection;
using Ziwa.App.ViewModels;
using Ziwa.Infrastructure.DependencyInjection;
#if WINDOWS
using Ziwa.Platform.Windows;
#endif

namespace Ziwa.App;

public static class Bootstrapper
{
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddZiwaCoreServices();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            services.AddZiwaWindowsPlatform();
        }
#endif

        services.AddTransient<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
