using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Ziwa.App.ViewModels;

namespace Ziwa.App.Services;

public sealed class DesktopProjectLifetime
{
    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private bool _closing;
    private bool _approved;
    private bool _shutdownRequested;

    public DesktopProjectLifetime(
        Window window,
        MainWindowViewModel viewModel,
        IClassicDesktopStyleApplicationLifetime desktop,
        string? startupProjectPath = null)
    {
        _window = window;
        _viewModel = viewModel;
        _desktop = desktop;
        window.Opened += async (_, _) =>
        {
            await viewModel.InitializeProjectsAsync();
            if (!string.IsNullOrWhiteSpace(startupProjectPath))
            {
                await viewModel.OpenExternalProjectFileAsync(startupProjectPath);
            }
        };
        window.Closing += OnClosing;
        desktop.ShutdownRequested += (_, args) =>
        {
            if (_approved) { return; }
            args.Cancel = true;
            _shutdownRequested = true;
            _ = RequestCloseAsync();
        };
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_approved) { return; }
        args.Cancel = true;
        _ = RequestCloseAsync();
    }

    private async Task RequestCloseAsync()
    {
        if (_closing) { return; }
        _closing = true;
        try
        {
            if (!await _viewModel.RequestCloseAsync()) { return; }
            _approved = true;
            if (_shutdownRequested) { _desktop.Shutdown(); }
            else { _window.Close(); }
        }
        finally { _closing = false; }
    }
}
