using Microsoft.Extensions.DependencyInjection;
using Ziwa.App.Services;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.App.ViewModels;
using Ziwa.Infrastructure.Ai;
using Ziwa.Infrastructure.Auth;
using Ziwa.Infrastructure.DependencyInjection;
using Ziwa.Infrastructure.Scanner;
#if WINDOWS
using Ziwa.Platform.Windows;
#endif

namespace Ziwa.App;

public static class Bootstrapper
{
    public static ServiceProvider BuildServiceProvider(
        ProjectStorageOptions? storageOptions = null,
        IProjectDialogs? dialogs = null,
        OfflineAiOptions? aiOptions = null,
        DocumentScannerOptions? scannerOptions = null,
        CloudAiOptions? cloudAiOptions = null,
        SupabaseAuthOptions? authOptions = null,
        IBrowserLauncher? browserLauncher = null)
    {
        var services = new ServiceCollection();
        services.AddZiwaCoreServices(storageOptions, aiOptions, scannerOptions, cloudAiOptions, authOptions, browserLauncher);
        services.AddSingleton<IProjectDialogs>(dialogs ?? new AvaloniaProjectDialogs());
        services.AddSingleton<ProjectWorkspace>();

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
