using Avalonia.Controls;
using Ziwa.App.ViewModels;

namespace Ziwa.App.Views;

public partial class AiMaskEditorView : UserControl
{
    public AiMaskEditorView()
    {
        InitializeComponent();
    }

    private void OnMaskCanvasModified()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.NotifyMaskEditorModified();
        }
    }
}
