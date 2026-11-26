using Avalonia.Input;

namespace Ziwa.App.Services;

public static class KeyboardCommandRouter
{
    public static string? GetGesture(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Delete && modifiers == KeyModifiers.None) { return "Delete"; }
        if (!modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Alt) || modifiers.HasFlag(KeyModifiers.Meta)) { return null; }
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        return key switch
        {
            Key.Z => shift ? "Ctrl+Shift+Z" : "Ctrl+Z",
            Key.Y => "Ctrl+Y",
            Key.C => "Ctrl+C",
            Key.V => "Ctrl+V",
            Key.G => shift ? "Ctrl+Shift+G" : "Ctrl+G",
            Key.S => shift ? "Ctrl+Shift+S" : "Ctrl+S",
            Key.O => "Ctrl+O",
            Key.E => "Ctrl+E",
            Key.P => "Ctrl+P",
            Key.N when shift => "Ctrl+Shift+N",
            Key.B when shift => "Ctrl+Shift+B",
            Key.F when shift => "Ctrl+Shift+F",
            Key.I when shift => "Ctrl+Shift+I",
            Key.K => "Ctrl+K",
            Key.OemPeriod or Key.Decimal => "Ctrl+.",
            _ => null,
        };
    }
}
