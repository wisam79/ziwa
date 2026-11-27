using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Ziwa.App.Controls;

/// <summary>
/// Unified modal dialog container providing Apple HIG surface elevation, scrim backdrop,
/// standardized RTL header with close button, scrollable body, and optional pinned footer.
/// </summary>
public sealed class StudioDialogShell : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<StudioDialogShell, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<StudioDialogShell, string?>(nameof(Subtitle));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<StudioDialogShell, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<bool> IsCloseEnabledProperty =
        AvaloniaProperty.Register<StudioDialogShell, bool>(nameof(IsCloseEnabled), defaultValue: true);

    public static readonly StyledProperty<string> CloseToolTipProperty =
        AvaloniaProperty.Register<StudioDialogShell, string>(nameof(CloseToolTip), defaultValue: "إغلاق النافذة (Esc)");

    public static readonly StyledProperty<string> CloseAccessibleNameProperty =
        AvaloniaProperty.Register<StudioDialogShell, string>(nameof(CloseAccessibleName), defaultValue: "إغلاق النافذة");

    public static readonly StyledProperty<object?> HeaderExtraContentProperty =
        AvaloniaProperty.Register<StudioDialogShell, object?>(nameof(HeaderExtraContent));

    public static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<StudioDialogShell, object?>(nameof(FooterContent));

    public static readonly StyledProperty<double> DialogWidthProperty =
        AvaloniaProperty.Register<StudioDialogShell, double>(nameof(DialogWidth), defaultValue: 520.0);

    public static readonly StyledProperty<double> DialogHeightProperty =
        AvaloniaProperty.Register<StudioDialogShell, double>(nameof(DialogHeight), defaultValue: double.NaN);

    public static readonly StyledProperty<double> DialogMaxHeightProperty =
        AvaloniaProperty.Register<StudioDialogShell, double>(nameof(DialogMaxHeight), defaultValue: 740.0);

    public static readonly StyledProperty<bool> UseScrimProperty =
        AvaloniaProperty.Register<StudioDialogShell, bool>(nameof(UseScrim), defaultValue: true);

    public static readonly StyledProperty<bool> ScrollableBodyProperty =
        AvaloniaProperty.Register<StudioDialogShell, bool>(nameof(ScrollableBody), defaultValue: true);

    public static readonly StyledProperty<HorizontalAlignment> DialogHorizontalAlignmentProperty =
        AvaloniaProperty.Register<StudioDialogShell, HorizontalAlignment>(nameof(DialogHorizontalAlignment), defaultValue: HorizontalAlignment.Center);

    public static readonly StyledProperty<VerticalAlignment> DialogVerticalAlignmentProperty =
        AvaloniaProperty.Register<StudioDialogShell, VerticalAlignment>(nameof(DialogVerticalAlignment), defaultValue: VerticalAlignment.Center);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public bool IsCloseEnabled
    {
        get => GetValue(IsCloseEnabledProperty);
        set => SetValue(IsCloseEnabledProperty, value);
    }

    public string CloseToolTip
    {
        get => GetValue(CloseToolTipProperty);
        set => SetValue(CloseToolTipProperty, value);
    }

    public string CloseAccessibleName
    {
        get => GetValue(CloseAccessibleNameProperty);
        set => SetValue(CloseAccessibleNameProperty, value);
    }

    public object? HeaderExtraContent
    {
        get => GetValue(HeaderExtraContentProperty);
        set => SetValue(HeaderExtraContentProperty, value);
    }

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    public double DialogWidth
    {
        get => GetValue(DialogWidthProperty);
        set => SetValue(DialogWidthProperty, value);
    }

    public double DialogHeight
    {
        get => GetValue(DialogHeightProperty);
        set => SetValue(DialogHeightProperty, value);
    }

    public double DialogMaxHeight
    {
        get => GetValue(DialogMaxHeightProperty);
        set => SetValue(DialogMaxHeightProperty, value);
    }

    public bool UseScrim
    {
        get => GetValue(UseScrimProperty);
        set => SetValue(UseScrimProperty, value);
    }

    public bool ScrollableBody
    {
        get => GetValue(ScrollableBodyProperty);
        set => SetValue(ScrollableBodyProperty, value);
    }

    public HorizontalAlignment DialogHorizontalAlignment
    {
        get => GetValue(DialogHorizontalAlignmentProperty);
        set => SetValue(DialogHorizontalAlignmentProperty, value);
    }

    public VerticalAlignment DialogVerticalAlignment
    {
        get => GetValue(DialogVerticalAlignmentProperty);
        set => SetValue(DialogVerticalAlignmentProperty, value);
    }
}
