using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;

namespace Ziwa.App;

public sealed class App : global::Avalonia.Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _serviceProvider = Bootstrapper.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            if (_serviceProvider.GetRequiredService<IProjectDialogs>() is AvaloniaProjectDialogs dialogs)
            {
                dialogs.Owner = desktop.MainWindow;
            }

            _ = new DesktopProjectLifetime(desktop.MainWindow, viewModel, desktop);
            desktop.Exit += (_, _) => _serviceProvider?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
