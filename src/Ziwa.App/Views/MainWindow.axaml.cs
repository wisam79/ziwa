using Avalonia.Controls;
using Avalonia.Input;
using Ziwa.App.ViewModels;

namespace Ziwa.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.IsPrintDialogOpen)
            {
                vm.ClosePrintDialogCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (vm.IsCommandPaletteOpen)
            {
                vm.IsCommandPaletteOpen = false;
                e.Handled = true;
                return;
            }

            if (vm.IsStickerStudioOpen)
            {
                vm.CloseStickerStudioCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.P)
            {
                vm.OpenPrintDialogCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                vm.AddPageCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.K)
            {
                vm.ToggleCommandPaletteCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.OemPeriod or Key.Decimal)
            {
                vm.ToggleZenModeCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
