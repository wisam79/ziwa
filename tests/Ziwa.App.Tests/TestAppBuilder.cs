using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Ziwa.App.Tests.TestAppBuilder))]

namespace Ziwa.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
    }
}
