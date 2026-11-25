using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Printing;
using Ziwa.Platform.Windows.Printing;

namespace Ziwa.Platform.Windows;

public static class WindowsPlatformServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddZiwaWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformCapabilities, WindowsPlatformCapabilities>();
        services.AddSingleton<IPrintSpooler, GdiPrintSpooler>();
        return services;
    }
}
