using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Workspace;
using Ziwa.Domain.Models;

namespace Ziwa.App.Tests;

public sealed class Joint2WorkspaceChromePolishTests
{
    [AvaloniaFact]
    public void CommandBarAndTitleBar_ShowUndoDescriptionMaximizeIconAndFitAtMinWidth()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            WindowTitleBarView titleBar = Assert.IsType<WindowTitleBarView>(window.FindControl<WindowTitleBarView>("WindowTitleBarRegion"));
            CommandBarView commandBar = Assert.IsType<CommandBarView>(window.FindControl<CommandBarView>("CommandBarRegion"));
            Button maxBtn = Assert.IsType<Button>(titleBar.FindControl<Button>("MaximizeWindowButton"));

            Assert.Contains("تكبير", ToolTip.GetTip(maxBtn) as string);
            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("استعادة", ToolTip.GetTip(maxBtn) as string);

            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();

            // Adding a shape updates descriptive Undo tooltip and dirty save badge
            int beforeCount = vm.ActivePage.Nodes.Length;
            vm.AddRectangleShapeCommand.Execute(null);
            vm.AddEllipseShapeCommand.Execute(null);
            vm.AddLineShapeCommand.Execute(null);
            Assert.Equal(beforeCount + 3, vm.ActivePage.Nodes.Length);
            Assert.Contains("إدراج خط فاصل", vm.UndoToolTipText);
            Assert.Equal("● غير محفوظ", vm.ProjectSaveStateText);

            vm.UndoCommand.Execute(null);
            Assert.Contains("إدراج خط فاصل", vm.RedoToolTipText);

            // CommandBar stays within 960px minimum width without clipping action controls
            Button exportBtn = Assert.IsType<Button>(commandBar.FindControl<Button>("Export300DpiButton"));
            Button quickSaveBtn = Assert.IsType<Button>(commandBar.FindControl<Button>("QuickSaveButton"));
            Assert.True(exportBtn.Bounds.Width > 0);
            Assert.True(quickSaveBtn.Bounds.Width > 0);
            Assert.True(commandBar.Bounds.Width <= 961);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RailSelectToolAndPageStripWheelScroll_WorkEndToEnd()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Select tool button exits text edit mode cleanly
            TextNode textNode = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n is TextNode));
            vm.Session.EnterTextEditMode(textNode.Id);
            Assert.False(vm.IsSelectToolActive);
            Assert.True(vm.IsInlineTextEditOpen);

            vm.ActivateSelectToolCommand.Execute(null);
            Assert.True(vm.IsSelectToolActive);
            Assert.False(vm.IsInlineTextEditOpen);

            // Status bar pointer coordinates readout formatting
            vm.CanvasPointerXMm = 42.5;
            vm.CanvasPointerYMm = 80.0;
            Assert.Equal("X: 42.5  Y: 80 مم", vm.CanvasPointerCoordsText);
            vm.CanvasPointerXMm = null;
            Assert.Equal("X: --  Y: -- مم", vm.CanvasPointerCoordsText);

            // PageStrip horizontal mouse-wheel scrolling across many tabs
            for (int i = 0; i < 16; i++)
            {
                vm.AddPageCommand.Execute(null);
            }

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            PageStripView pageStrip = Assert.IsType<PageStripView>(window.FindControl<PageStripView>("PageStripRegion"));
            ScrollViewer scroller = Assert.IsType<ScrollViewer>(pageStrip.FindControl<ScrollViewer>("PageTabsScroller"));
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

            double initialX = scroller.Offset.X;
            Point center = scroller.TranslatePoint(new Point(scroller.Bounds.Width * 0.5, scroller.Bounds.Height * 0.5), window)!.Value;
            window.MouseWheel(center, new Vector(0, -1));
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroller.Offset.X > initialX);
        }
        finally
        {
            window.Close();
        }
    }
}
