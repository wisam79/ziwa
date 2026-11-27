using Avalonia.Controls;
using Avalonia.Input;
using Ziwa.App.ViewModels;

namespace Ziwa.App.Views;

public partial class ProjectLibraryView : UserControl
{
    public ProjectLibraryView()
    {
        InitializeComponent();
    }

    private void OnProjectListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { HasSelectedProject: true } vm)
        {
            vm.OpenSelectedProjectCommand.Execute(null);
        }
    }
}
