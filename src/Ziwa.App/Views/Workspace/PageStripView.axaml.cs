using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Ziwa.App.Views.Workspace;

public partial class PageStripView : UserControl
{
    public PageStripView()
    {
        InitializeComponent();
    }

    private void OnPageTabsPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        double delta = Math.Abs(e.Delta.X) > 0.01 ? e.Delta.X : e.Delta.Y;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        double maxX = Math.Max(0.0, PageTabsScroller.Extent.Width - PageTabsScroller.Viewport.Width);
        if (maxX <= 0.0)
        {
            return;
        }

        double nextX = Math.Clamp(PageTabsScroller.Offset.X - (delta * 48.0), 0.0, maxX);
        PageTabsScroller.Offset = new Vector(nextX, PageTabsScroller.Offset.Y);
        e.Handled = true;
    }
}
