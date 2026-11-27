using Avalonia;
using Avalonia.Controls;

namespace Ziwa.App.Controls;

public enum StudioCardAccent
{
    Default,
    Focus,
    Warning,
    Destructive,
    Subtle,
    Info,
}

/// <summary>
/// Standardized Apple HIG card container for inspector sections and dialog groups.
/// </summary>
public sealed class StudioCard : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<StudioCard, string?>(nameof(Header));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<StudioCard, string?>(nameof(Subtitle));

    public static readonly StyledProperty<object?> HeaderBadgeProperty =
        AvaloniaProperty.Register<StudioCard, object?>(nameof(HeaderBadge));

    public static readonly StyledProperty<StudioCardAccent> AccentKindProperty =
        AvaloniaProperty.Register<StudioCard, StudioCardAccent>(nameof(AccentKind), defaultValue: StudioCardAccent.Default);

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? HeaderBadge
    {
        get => GetValue(HeaderBadgeProperty);
        set => SetValue(HeaderBadgeProperty, value);
    }

    public StudioCardAccent AccentKind
    {
        get => GetValue(AccentKindProperty);
        set => SetValue(AccentKindProperty, value);
    }
}
