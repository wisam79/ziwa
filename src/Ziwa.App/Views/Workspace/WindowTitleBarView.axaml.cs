using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Ziwa.App.Views.Workspace;

public partial class WindowTitleBarView : UserControl
{
    private Window? _attachedWindow;

    public WindowTitleBarView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachWindowObserver();
        if (HostWindow is { } window)
        {
            _attachedWindow = window;
            _attachedWindow.PropertyChanged += OnHostWindowPropertyChanged;
            SyncMaximizeIcon(window.WindowState == WindowState.Maximized);
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => DetachWindowObserver();

    private void DetachWindowObserver()
    {
        if (_attachedWindow is not null)
        {
            _attachedWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _attachedWindow = null;
        }
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && _attachedWindow is { } window)
        {
            SyncMaximizeIcon(window.WindowState == WindowState.Maximized);
        }
    }

    private void SyncMaximizeIcon(bool isMaximized)
    {
        string key = isMaximized ? "IconWindowRestore" : "IconWindowMaximize";
        if (this.TryFindResource(key, out object? res) && res is StreamGeometry geometry)
        {
            MaximizeWindowIcon.Data = geometry;
        }

        string tip = isMaximized ? "استعادة حجم النافذة" : "تكبير النافذة";
        ToolTip.SetTip(MaximizeWindowButton, tip);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            HostWindow?.BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => HostWindow?.Close();

    private void ToggleMaximize()
    {
        if (HostWindow is not { } window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
