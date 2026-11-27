using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ziwa.App.Controls;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.Domain.Models;

namespace Ziwa.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _subscribedViewModel;
    private readonly UserControl[] _dialogViews;
    private readonly Dictionary<StudioDialogShell, Control> _dialogFocus = new();
    private StudioDialogShell? _activeDialog;
    private Control? _workspaceFocus;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDropAsync);
        InlineCanvasTextBox.AddHandler(KeyDownEvent, OnInlineCanvasTextKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _dialogViews = ((Panel)Content!).Children.OfType<UserControl>()
            .Where(view => view.Content is StudioDialogShell).ToArray();
        Opened += (_, _) =>
        {
            foreach (var view in _dialogViews) { view.PropertyChanged += OnDialogViewChanged; }
            UpdateActiveDialog();
        };
        Closed += (_, _) =>
        {
            foreach (var view in _dialogViews) { view.PropertyChanged -= OnDialogViewChanged; }
            _activeDialog = null;
            _dialogFocus.Clear();
            _workspaceFocus = null;
            UnsubscribeToast();
        };
        AddHandler(KeyDownEvent, OnModalKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnModalTextInput, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, OnModalFocusChanged);
        DataContextChanged += OnMainWindowDataContextChanged;
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
            _subscribedViewModel.InspectorPreflightReviewRequested += InspectorPanelView.ScrollToPreflight;
        }
    }

    private void UnsubscribeToast()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ToastRequested -= OnToastRequested;
            _subscribedViewModel.ViewportFitRequested -= OnViewportFitRequested;
            _subscribedViewModel.InlineTextEditOpened -= OnInlineTextEditOpened;
            _subscribedViewModel.InspectorPreflightReviewRequested -= InspectorPanelView.ScrollToPreflight;
            _subscribedViewModel = null;
        }
    }

    private void OnViewportFitRequested() => ViewportHost.FitToPage();

    private Point _inlineAnchorMm;

    private void OnInlineTextEditOpened(TextNode txt)
    {
        _inlineAnchorMm = new Point(txt.BoundsMm.X, txt.BoundsMm.Y);
        PositionInlineEditor();
        Dispatcher.UIThread.Post(() =>
        {
            if (InlineCanvasTextBox.IsEffectivelyVisible && InlineCanvasTextBox.Focus())
            {
                InlineCanvasTextBox.SelectAll();
            }
        });
    }

    private void OnInlineEditorSizeChanged(object? sender, SizeChangedEventArgs e) => PositionInlineEditor();

    private void PositionInlineEditor()
    {
        if (DataContext is not MainWindowViewModel { IsInlineTextEditOpen: true } ||
            InlineTextEditOverlay.Parent is not Canvas layer)
        {
            return;
        }

        (double x, double y) = ViewportHost.Viewport.PageMmToDip(_inlineAnchorMm.X, _inlineAnchorMm.Y);
        const double margin = 16;
        Canvas.SetLeft(InlineTextEditOverlay, Math.Clamp(x, margin, Math.Max(margin, layer.Bounds.Width - InlineTextEditOverlay.Bounds.Width - margin)));
        Canvas.SetTop(InlineTextEditOverlay, Math.Clamp(y, margin, Math.Max(margin, layer.Bounds.Height - InlineTextEditOverlay.Bounds.Height - margin)));
    }

    private void OnCropBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && DataContext is MainWindowViewModel { IsCropModeActive: true } vm)
        {
            vm.CancelCropModeCommand.Execute(null);
            ViewportHost.Focus();
            e.Handled = true;
        }
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

    private void OnDialogViewChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == ZIndexProperty) { UpdateActiveDialog(); }
    }

    private void UpdateActiveDialog()
    {
        // Panel draws ascending ZIndex, then declaration order. Don't maintain a second Escape ladder.
        var top = (StudioDialogShell?)_dialogViews.Where(v => v.IsVisible)
            .OrderBy(v => v.ZIndex).LastOrDefault()?.Content;
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (top != _activeDialog)
        {
            if (_activeDialog is null) { _workspaceFocus = focused ?? ViewportHost; }
            else if (focused is not null && IsInDialog(_activeDialog, focused)) { _dialogFocus[_activeDialog] = focused; }
            _activeDialog = top;
        }

        foreach (var view in _dialogViews)
        {
            var shell = (StudioDialogShell)view.Content!;
            shell.SetCovered(view.IsVisible && shell != top);
            if (!view.IsVisible) { _dialogFocus.Remove(shell); }
        }

        if (top is not null)
        {
            // A previously hidden view may not have applied its template yet.
            Dispatcher.UIThread.Post(() => EnsureDialogFocus(top), DispatcherPriority.Loaded);
        }
        else if (_workspaceFocus is { } opener)
        {
            _workspaceFocus = null;
            if (!opener.Focus()) { ViewportHost.Focus(); }
        }
    }

    private static bool IsInDialog(StudioDialogShell dialog, object? target) => target is Visual visual &&
        (dialog == visual || dialog.IsVisualAncestorOf(visual) || dialog.IsLogicalAncestorOf(visual));

    private void EnsureDialogFocus(StudioDialogShell dialog)
    {
        if (dialog != _activeDialog || IsInDialog(dialog, FocusManager?.GetFocusedElement())) { return; }
        if (_dialogFocus.TryGetValue(dialog, out var previous) && previous.Focus()) { return; }
        dialog.FocusEntry();
    }

    private void OnModalFocusChanged(object? sender, GotFocusEventArgs e)
    {
        if (_activeDialog is { } dialog && !IsInDialog(dialog, e.Source))
        {
            Dispatcher.UIThread.Post(() => EnsureDialogFocus(dialog), DispatcherPriority.Input);
        }
    }

    private void OnModalTextInput(object? sender, TextInputEventArgs e)
    {
        if (_activeDialog is { } dialog && !IsInDialog(dialog, e.Source)) { e.Handled = true; }
    }

    private void OnModalKeyDown(object? sender, KeyEventArgs e)
    {
        // Stop stale viewport/inline/editor targets before their own key handlers can mutate the page.
        if (_activeDialog is { } dialog && !IsInDialog(dialog, e.Source))
        {
            e.Handled = true;
            if (e.Key == Key.Escape) { dialog.TryClose(); }
            else { EnsureDialogFocus(dialog); }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm) { return; }
        if (_activeDialog is { } dialog)
        {
            // Dialog controls get first refusal (text editing, dropdowns, Enter); never route to the workspace.
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.TryClose();
            }
            return;
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
