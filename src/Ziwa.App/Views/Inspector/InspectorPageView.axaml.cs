using Avalonia.Controls;

namespace Ziwa.App.Views.Inspector;

public partial class InspectorPageView : UserControl
{
    public InspectorPageView()
    {
        InitializeComponent();
    }

    public void ScrollToPreflight() => PageScrollViewer.ScrollToHome();
}
