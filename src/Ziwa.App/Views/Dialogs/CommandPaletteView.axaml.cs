using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ziwa.App.Controls;
using Ziwa.App.ViewModels;

namespace Ziwa.App.Views.Dialogs;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        ((StudioDialogShell)Content!).InitialTextInput = CommandSearchTextBox;
        CommandSearchTextBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            DataContext is MainWindowViewModel vm &&
            vm.FilteredCommands.Count > 0)
        {
            vm.ExecutePaletteCommandCommand.Execute(vm.FilteredCommands[0]);
            e.Handled = true;
        }
    }
}
