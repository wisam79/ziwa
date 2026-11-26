using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ziwa.App.Views.Workspace;

public partial class CommandBarView : UserControl
{
    public CommandBarView()
    {
        InitializeComponent();
    }

    private void OnProjectMenuItemClick(object? sender, RoutedEventArgs e) =>
        ProjectFileMenuButton.Flyout?.Hide();

    private void OnTemplateMenuItemClick(object? sender, RoutedEventArgs e) =>
        TemplatesMenuButton.Flyout?.Hide();
}
