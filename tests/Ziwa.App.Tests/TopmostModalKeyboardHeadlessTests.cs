using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.Controls;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Dialogs;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class TopmostModalKeyboardHeadlessTests
{
    [AvaloniaFact]
    public void PrintFromCanvas_IsolatesKeysAndRestoresTheViewportWithoutChangingSelection()
    {
        using ServiceProvider services = Bootstrapper.BuildServiceProvider();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            InspectorUi.Layout(window);
            var canvas = InspectorUi.Visible<SkiaViewportHost>(window);
            NodeId selected = vm.ActivePage.Nodes.First(n => n.IsVisible && !n.IsLocked).Id;
            vm.Session.SetSelection([selected]);
            var document = vm.Document;
            Assert.True(canvas.Focus());

            Press(window, PhysicalKey.P, RawInputModifiers.Control);
            Assert.True(vm.IsPrintDialogOpen);
            // Don't pump layout before the next key: the viewport may still be the routed target.
            Press(window, PhysicalKey.Backspace);
            InspectorUi.Layout(window);
            var shell = InspectorUi.Visible<StudioDialogShell>(window);
            Assert.True(shell.IsKeyboardFocusWithin);
            canvas.Focus(); // A delayed workspace focus request must not redirect editor commands.
            Press(window, PhysicalKey.Delete);
            Press(window, PhysicalKey.ArrowRight);
            Press(window, PhysicalKey.K, RawInputModifiers.Control);
            Assert.False(vm.IsCommandPaletteOpen);
            Assert.True(vm.IsPrintDialogOpen);
            Assert.Same(document, vm.Document);

            Press(window, PhysicalKey.Escape);
            InspectorUi.Layout(window);
            Assert.False(vm.IsPrintDialogOpen);
            Assert.True(canvas.IsFocused);
            Assert.Equal(selected, Assert.Single(vm.Session.SelectedNodeIds));
            Press(window, PhysicalKey.D, RawInputModifiers.Control);
            Assert.Equal(document.ActivePage.Nodes.Length + 1, vm.ActivePage.Nodes.Length);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DialogTextEditingAndTabCycle_AreLocal_BusyTopDialogDoesNotDismissTheOneBelow()
    {
        using ServiceProvider services = Bootstrapper.BuildServiceProvider();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            InspectorUi.Layout(window);
            var opener = InspectorUi.Visible<Button>(window, "PrintDialogButton");
            Assert.True(opener.Focus());
            Press(window, PhysicalKey.Enter);
            InspectorUi.Layout(window);
            var print = InspectorUi.Visible<PrintExportDialogView>(window);
            var shell = InspectorUi.Visible<StudioDialogShell>(print);
            var close = InspectorUi.Visible<Button>(shell, "PART_CloseButton");
            Assert.True(close.Focus());
            Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.False(close.IsFocused);
            Assert.True(shell.IsKeyboardFocusWithin);
            Press(window, PhysicalKey.Tab);
            Assert.True(close.IsFocused);

            var copies = InspectorUi.Visible<StudioNumberField>(print, "PrintCopiesNumeric");
            var editor = InspectorUi.Visible<TextBox>(copies);
            Assert.True(editor.Focus());
            Press(window, PhysicalKey.A, RawInputModifiers.Control);
            window.KeyTextInput("42");
            Press(window, PhysicalKey.Backspace);
            Assert.Equal("4", editor.Text);
            Press(window, PhysicalKey.Home);
            Press(window, PhysicalKey.Delete);
            Assert.Equal(string.Empty, editor.Text);
            window.KeyTextInput("3");
            Press(window, PhysicalKey.Enter);
            Assert.Equal(3, vm.PrintCopies);
            Press(window, PhysicalKey.K, RawInputModifiers.Control);
            Assert.False(vm.IsCommandPaletteOpen);

            vm.IsUpdateBusy = true;
            vm.IsUpdateDialogOpen = true;
            InspectorUi.Layout(window);
            var update = InspectorUi.Visible<AppUpdateView>(window);
            Assert.True(update.IsKeyboardFocusWithin);
            Press(window, PhysicalKey.Escape);
            Assert.True(vm.IsUpdateDialogOpen);
            Assert.True(vm.IsPrintDialogOpen);
            vm.IsUpdateBusy = false;
            Press(window, PhysicalKey.Escape);
            InspectorUi.Layout(window);
            Assert.False(vm.IsUpdateDialogOpen);
            Assert.True(editor.IsFocused);
            Assert.True(vm.IsPrintDialogOpen);
            Assert.True(close.Focus());
            Press(window, PhysicalKey.Escape);
            InspectorUi.Layout(window);
            Assert.True(opener.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PaletteEnter_KeepsUnavailableResultAndSearchFocus_ThenRunsAnAvailableResult()
    {
        using ServiceProvider services = Bootstrapper.BuildServiceProvider();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            InspectorUi.Layout(window);
            vm.Session.ClearSelection();
            var canvas = InspectorUi.Visible<SkiaViewportHost>(window);
            Assert.True(canvas.Focus());
            Press(window, PhysicalKey.K, RawInputModifiers.Control);
            InspectorUi.Layout(window);
            var search = InspectorUi.Visible<TextBox>(window, "CommandSearchTextBox");
            Assert.True(search.IsFocused);
            window.KeyTextInput("Delete");
            Assert.False(Assert.Single(vm.FilteredCommands).CanExecute());
            Press(window, PhysicalKey.Enter);
            Assert.True(vm.IsCommandPaletteOpen);
            Assert.True(search.IsFocused);
            Assert.Equal("Delete", vm.CommandSearchQuery);

            Press(window, PhysicalKey.A, RawInputModifiers.Control);
            window.KeyTextInput("Ctrl+P");
            Press(window, PhysicalKey.Enter);
            InspectorUi.Layout(window);
            Assert.False(vm.IsCommandPaletteOpen);
            Assert.True(vm.IsPrintDialogOpen);
            Assert.True(InspectorUi.Visible<PrintExportDialogView>(window).IsKeyboardFocusWithin);
            Press(window, PhysicalKey.Escape);
            InspectorUi.Layout(window);
            Assert.True(canvas.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }
}
