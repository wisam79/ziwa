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
            Key.D when !shift => "Ctrl+D",
            Key.A when !shift => "Ctrl+A",
            Key.D0 or Key.NumPad0 when !shift => "Ctrl+0",
            Key.D1 or Key.NumPad1 when !shift => "Ctrl+1",
            Key.OemCloseBrackets when !shift => "Ctrl+]",
            Key.OemOpenBrackets when !shift => "Ctrl+[",
            Key.G => shift ? "Ctrl+Shift+G" : "Ctrl+G",
            Key.S => shift ? "Ctrl+Shift+S" : "Ctrl+S",
            Key.O => "Ctrl+O",
            Key.E => "Ctrl+E",
            Key.P => shift ? "Ctrl+Shift+P" : "Ctrl+P",
            Key.N when shift => "Ctrl+Shift+N",
            Key.B when shift => "Ctrl+Shift+B",
            Key.F when shift => "Ctrl+Shift+F",
            Key.U when shift => "Ctrl+Shift+U",
            Key.A when shift => "Ctrl+Shift+A",
            Key.I => shift ? "Ctrl+Shift+I" : "Ctrl+I",
            Key.D when shift => "Ctrl+Shift+D",
            Key.K => "Ctrl+K",
            Key.OemPeriod or Key.Decimal => "Ctrl+.",
            _ => null,
        };
    }
}
