using Avalonia.Controls;
using Ziwa.App.ViewModels;
using Ziwa.Domain.Scanner;

namespace Ziwa.App.Views;

public partial class DocumentScannerView : UserControl
{
    public DocumentScannerView()
    {
        InitializeComponent();
    }

    private void OnScannerCornerMoved(int regionIndex, int cornerIndex, PointPx newPointPx)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.UpdateScannerRegionCorner(regionIndex, cornerIndex, newPointPx, refreshRectifiedPreview: false);
        }
    }

    private void OnScannerCornerDragCompleted()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.RefreshScannerRectifiedPreview();
        }
    }
}
