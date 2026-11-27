using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ziwa.App.ViewModels;

namespace Ziwa.App.Views.Dialogs;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        CommandSearchTextBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            Dispatcher.UIThread.Post(() =>
            {
                CommandSearchTextBox.Focus();
                CommandSearchTextBox.SelectAll();
            });
        }
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
