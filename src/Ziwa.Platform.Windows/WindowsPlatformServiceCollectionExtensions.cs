using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Printing;
using Ziwa.Platform.Windows.Printing;
using Ziwa.Platform.Windows.Shell;
using Ziwa.Platform.Windows.SingleInstance;

namespace Ziwa.Platform.Windows;

public static class WindowsPlatformServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddZiwaWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformCapabilities, WindowsPlatformCapabilities>();
        services.AddSingleton<IPrintSpooler, GdiPrintSpooler>();
        services.AddSingleton<ISingleInstanceCoordinator, WindowsSingleInstanceCoordinator>();
        services.AddSingleton<IFileAssociationService, WindowsFileAssociationService>();
        return services;
    }
}
