using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;

namespace Ziwa.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnProjectMenuItemClick(object? sender, RoutedEventArgs e) =>
        this.FindControl<Button>("ProjectFileMenuButton")?.Flyout?.Hide();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm) { return; }
        if (e.Key == Key.Escape)
        {
            if (vm.IsDocumentScannerOpen && vm.IsScannerToolsEnabled) { vm.CloseDocumentScannerCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsMaskEditorOpen) { vm.CloseMaskEditorCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsProjectLibraryOpen) { vm.CloseProjectLibraryCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsPrintDialogOpen) { vm.ClosePrintDialogCommand.Execute(null); e.Handled = true; return; }
            if (vm.IsCommandPaletteOpen) { vm.IsCommandPaletteOpen = false; e.Handled = true; return; }
            if (vm.IsStickerStudioOpen) { vm.CloseStickerStudioCommand.Execute(null); e.Handled = true; return; }
        }

        string? gesture = KeyboardCommandRouter.GetGesture(e.Key, e.KeyModifiers);
        bool textInput = e.Source is TextBox || (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<TextBox>() is not null);
        if (textInput && gesture is "Ctrl+C" or "Ctrl+V" or "Ctrl+Z" or "Ctrl+Y" or "Delete") { return; }
        if (gesture is not null && vm.CommandRegistry.TryGetByShortcut(gesture, out _))
        {
            _ = vm.CommandRegistry.TryExecuteShortcutAsync(gesture);
            e.Handled = true;
        }
    }
}
