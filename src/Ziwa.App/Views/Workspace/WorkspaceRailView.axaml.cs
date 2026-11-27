using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ziwa.App.Views.Workspace;

public partial class WorkspaceRailView : UserControl
{
    public WorkspaceRailView()
    {
        InitializeComponent();
    }

    private void OnShapeMenuItemClick(object? sender, RoutedEventArgs e) =>
        ShapeToolButton.Flyout?.Hide();

    private void OnTextMenuItemClick(object? sender, RoutedEventArgs e) =>
        TextToolButton.Flyout?.Hide();
}
