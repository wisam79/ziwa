using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Platform;

namespace Ziwa.App;

public sealed class App : global::Avalonia.Application
{
    private ServiceProvider? _serviceProvider;
    private ISingleInstanceCoordinator? _activeSingleInstance;

    internal static string[] StartupArgs { get; set; } = [];
    internal static ISingleInstanceCoordinator? PrimarySingleInstanceCoordinator { get; set; }

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
            IStructuredAppLogger? logger = _serviceProvider.GetService<IStructuredAppLogger>();
            IFileAssociationService? association = _serviceProvider.GetService<IFileAssociationService>();

            try
            {
                association?.EnsureZiwaExtensionRegistered();
            }
            catch (Exception ex)
            {
                logger?.LogWarning("FileAssociation", $"Could not register .ziwa association: {ex.Message}");
            }

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;
            if (_serviceProvider.GetRequiredService<IProjectDialogs>() is AvaloniaProjectDialogs dialogs)
            {
                dialogs.Owner = mainWindow;
            }

            string? startupProjectPath = StartupArgumentParser.TryExtractZiwaProjectPath(StartupArgs);
            _ = new DesktopProjectLifetime(mainWindow, viewModel, desktop, startupProjectPath);

            _activeSingleInstance = PrimarySingleInstanceCoordinator
                ?? _serviceProvider.GetService<ISingleInstanceCoordinator>();

            if (_activeSingleInstance is not null)
            {
                _activeSingleInstance.SecondaryInstanceActivated += openPath =>
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        if (mainWindow.WindowState == WindowState.Minimized)
                        {
                            mainWindow.WindowState = WindowState.Normal;
                        }

                        mainWindow.Activate();
                        await viewModel.OpenExternalProjectFileAsync(openPath);
                    });
                };
                _activeSingleInstance.StartListening();
            }

            desktop.Exit += (_, _) =>
            {
                _activeSingleInstance?.Dispose();
                _serviceProvider?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
