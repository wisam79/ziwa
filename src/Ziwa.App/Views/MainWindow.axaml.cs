using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.Domain.Models;

namespace Ziwa.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDropAsync);
        InlineCanvasTextBox.AddHandler(KeyDownEvent, OnInlineCanvasTextKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += OnMainWindowDataContextChanged;
        Closed += (_, _) => UnsubscribeToast();
    }

    private void OnMainWindowDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeToast();
        if (DataContext is MainWindowViewModel vm)
        {
            _subscribedViewModel = vm;
            _subscribedViewModel.ToastRequested += OnToastRequested;
            _subscribedViewModel.ViewportFitRequested += OnViewportFitRequested;
            _subscribedViewModel.InlineTextEditOpened += OnInlineTextEditOpened;
        }
    }

    private void UnsubscribeToast()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ToastRequested -= OnToastRequested;
            _subscribedViewModel.ViewportFitRequested -= OnViewportFitRequested;
            _subscribedViewModel.InlineTextEditOpened -= OnInlineTextEditOpened;
            _subscribedViewModel = null;
        }
    }

    private void OnViewportFitRequested() => ViewportHost.FitToPage();

    private void OnInlineTextEditOpened(TextNode txt)
    {
        (double xDip, double yDip) = ViewportHost.Viewport.PageMmToDip(txt.BoundsMm.X, txt.BoundsMm.Y);
        double maxX = Math.Max(16.0, ViewportHost.Bounds.Width - 280.0);
        double maxY = Math.Max(16.0, ViewportHost.Bounds.Height - 130.0);
        Canvas.SetLeft(InlineTextEditOverlay, Math.Clamp(xDip, 16.0, maxX));
        Canvas.SetTop(InlineTextEditOverlay, Math.Clamp(yDip, 16.0, maxY));
        Dispatcher.UIThread.Post(() =>
        {
            InlineCanvasTextBox.Focus();
            InlineCanvasTextBox.SelectAll();
        });
    }

    private void OnInlineCanvasTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.CommitInlineTextEditCommand.Execute(null);
            ViewportHost.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelInlineTextEditCommand.Execute(null);
            ViewportHost.Focus();
            e.Handled = true;
        }
    }

    private void OnToastRequested(string title, string message, NotificationType type)
    {
        ToastNotificationManager?.Show(new Notification(title, message, type, TimeSpan.FromSeconds(4)));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            Padding = WindowState == WindowState.Maximized ? OffScreenMargin : default;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm) { return; }
        if (e.Key == Key.Escape)
        {
            if (vm.IsUpdateDialogOpen && vm.IsUpdateIdle) { vm.CloseUpdateDialogCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsPhoneBridgeDialogOpen) { vm.ClosePhoneBridgeDialogCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsAccountDialogOpen && vm.IsAccountIdle) { vm.CloseAccountDialogCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsDocumentScannerOpen && vm.IsScannerToolsEnabled) { vm.CloseDocumentScannerCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsMaskEditorOpen) { vm.CloseMaskEditorCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsProjectLibraryOpen) { vm.CloseProjectLibraryCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsPrintDialogOpen) { vm.ClosePrintDialogCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsCommandPaletteOpen) { vm.IsCommandPaletteOpen = false; e.Handled = true; return; }
            if (vm.IsStickerStudioOpen) { vm.CloseStickerStudioCommand.Execute(null); e.Handled = true; return; }
        }

        string? gesture = KeyboardCommandRouter.GetGesture(e.Key, e.KeyModifiers);
        bool textInput = e.Source is TextBox || (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<TextBox>() is not null);
        if (textInput && gesture is "Ctrl+A" or "Ctrl+C" or "Ctrl+V" or "Ctrl+Z" or "Ctrl+Y" or "Delete") { return; }
        if (gesture is not null && vm.CommandRegistry.TryGetByShortcut(gesture, out _))
        {
            _ = vm.CommandRegistry.TryExecuteShortcutAsync(gesture);
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        IEnumerable<IStorageItem>? items = e.DataTransfer.TryGetFiles();
        e.DragEffects = items is not null && items.Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDropAsync(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is null || items.Length == 0)
        {
            return;
        }

        string[] paths = items
            .Select(static item => item.TryGetLocalPath())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .ToArray();

        if (paths.Length > 0)
        {
            await vm.HandleDroppedPathsAsync(paths);
        }
    }
}
