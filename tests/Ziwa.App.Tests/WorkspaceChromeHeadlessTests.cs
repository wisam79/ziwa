using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.Controls;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Workspace;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class WorkspaceChromeHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(960, 640, true)]
    [InlineData(960, 640, false)]
    [InlineData(1360, 860, true)]
    [InlineData(1360, 860, false)]
    public void Chrome_KeepsActionsReachableWithLongArabicTextAndManyPages(int width, int height, bool dark)
    {
        using ServiceProvider services = BuildServices();
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        var app = global::Avalonia.Application.Current!;
        ThemeVariant? previousTheme = app.RequestedThemeVariant;
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        try
        {
            vm.IsDarkTheme = dark;
            const string title = "مشروع طباعة عربي طويل جدًا يحتوي على اسم عميل ووصف كامل للتصميم والورق والطلبات الخاصة والملاحظات النهائية للطباعة";
            vm.Session.ApplyCommand("مشروع اختباري", doc => doc with
            {
                Title = title,
                Pages = Enumerable.Range(0, 18).Select(i => doc.ActivePage with
                {
                    Id = i == 0 ? doc.ActivePage.Id : PageId.New(),
                    Name = $"عنوان طويل للصفحة رقم {i + 1} في مستند الطباعة",
                }).ToImmutableArray(),
            });
            vm.StatusMessage = title;
            window.Show();
            Layout(window);

            Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            CommandBarView toolbar = Find<CommandBarView>(window, "CommandBarRegion");
            WorkspaceRailView rail = Find<WorkspaceRailView>(window, "WorkspaceRailRegion");
            PageStripView pages = Find<PageStripView>(window, "PageStripRegion");
            WorkspaceStatusBarView status = Find<WorkspaceStatusBarView>(window, "StatusBarRegion");
            Rect workspace = BoundsIn(Find<Grid>(window, "CanvasWorkspaceRegion"), window);
            Assert.True(BoundsIn(rail, window).Left >= workspace.Right);
            Assert.True(BoundsIn(Find<Border>(window, "InspectorRegion"), window).Right <= workspace.Left);
            AssertRulersSharePageCoordinates(window);
            Assert.Equal(FlowDirection.RightToLeft, Find<Border>(window, "ContextualQuickBar").FlowDirection);
            Assert.True(rail.GetVisualDescendants().OfType<PathIcon>().First().Width >
                        toolbar.GetVisualDescendants().OfType<PathIcon>().First().Width);
            Assert.True(status.Bounds.Height < pages.Bounds.Height && pages.Bounds.Height < toolbar.Bounds.Height + 12);
            foreach (Button button in toolbar.GetVisualDescendants().OfType<Button>())
            {
                AssertInside(button, toolbar);
            }

            var titleLabel = Find<TextBlock>(toolbar, "DocumentTitleLabel");
            var message = Find<TextBlock>(status, "StatusMessageLabel");
            Assert.Equal(title, ToolTip.GetTip(titleLabel));
            Assert.Equal(title, ToolTip.GetTip(message));
            Assert.Equal(TextTrimming.CharacterEllipsis, titleLabel.TextTrimming);
            Assert.True(titleLabel.Bounds.Width > 20);
            AssertInside(titleLabel, toolbar);
            AssertInside(message, status);
            Rect titleBounds = BoundsIn(titleLabel, window);
            Assert.True(BoundsIn(Find<Button>(toolbar, "ProjectFileMenuButton"), window).X >= titleBounds.Right);
            Assert.True(BoundsIn(Find<Button>(toolbar, "Export300DpiButton"), window).Right <= titleBounds.X);

            ScrollViewer scroller = Find<ScrollViewer>(pages, "PageTabsScroller");
            var actions = Find<StackPanel>(pages, "PageActions");
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
            Rect actionBounds = BoundsIn(actions, pages);
            Assert.False(actionBounds.Intersects(BoundsIn(scroller, pages)));
            var tabs = scroller.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("pageTab")).ToArray();
            var first = Assert.Single(tabs, button => button.Classes.Contains("active"));
            Assert.Contains("الصفحة النشطة", AutomationProperties.GetName(first));
            var second = Assert.Single(tabs, button => button.CommandParameter is 1);
            Click(window, second);
            Assert.Equal(1, vm.Document.ActivePageIndex);

            scroller.Offset = new Vector(scroller.Extent.Width - scroller.Viewport.Width, 0);
            Layout(window);
            Assert.Equal(actionBounds, BoundsIn(actions, pages));
            Assert.All(actions.Children.OfType<Button>(), button => AssertInside(button, pages));
            var last = Assert.Single(tabs, button => button.CommandParameter is 17);
            double offset = scroller.Offset.X;
            Assert.True(offset > 0);
            Assert.True(last.Focus());
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal(17, vm.Document.ActivePageIndex);
            Assert.Same(last, Assert.Single(tabs, button => button.Classes.Contains("active")));
            Assert.Contains("الصفحة النشطة", AutomationProperties.GetName(last));
            Assert.True(Math.Abs(scroller.Offset.X - offset) < 1);
            Assert.All(toolbar.GetVisualDescendants().OfType<PathIcon>(), icon =>
            {
                Assert.NotNull(icon.Data);
                Assert.True(icon.Data!.Bounds.Width > 0 && icon.Data.Bounds.Height > 0);
            });
        }
        finally
        {
            window.Close();
            app.RequestedThemeVariant = previousTheme;
        }
    }

    [AvaloniaFact]
    public void Chrome_UsesRoutedClicksAndKeyboardForExistingCommands()
    {
        using ServiceProvider services = BuildServices();
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Layout(window);
            CommandBarView toolbar = Find<CommandBarView>(window, "CommandBarRegion");
            var file = Find<Button>(toolbar, "ProjectFileMenuButton");
            Click(window, file);
            Assert.True(file.Flyout!.IsOpen);
            file.Flyout.Hide();
            Layout(window);

            Click(window, Find<Button>(toolbar, "AccountHeaderButton"));
            Assert.True(vm.IsAccountDialogOpen);
            vm.CloseAccountDialogCommand.Execute(null);
            Layout(window);

            Click(window, Find<Button>(toolbar, "PrintDialogButton"));
            Assert.True(vm.IsPrintDialogOpen);
            vm.ClosePrintDialogCommand.Execute(null);
            Layout(window);

            Click(window, Find<Button>(toolbar, "ZoomInButton"));
            Assert.NotEqual("100%", vm.ZoomPercentText);
            Click(window, Find<Button>(toolbar, "ResetZoomButton"));
            Assert.Equal("100%", vm.ZoomPercentText);

            var host = Find<SkiaViewportHost>(window, "ViewportHost");
            Point canvasPoint = BoundsIn(host, window).Center;
            double zoom = host.Viewport.Zoom;
            window.MouseWheel(canvasPoint, new Vector(0, 1));
            Assert.True(host.Viewport.Zoom > zoom);
            double pan = host.Viewport.PanXDip;
            window.MouseDown(canvasPoint, MouseButton.Middle);
            window.MouseMove(canvasPoint + new Vector(20, 10), RawInputModifiers.MiddleMouseButton);
            window.MouseUp(canvasPoint + new Vector(20, 10), MouseButton.Middle);
            Layout(window);
            Assert.True(host.Viewport.PanXDip > pan);
            AssertRulersSharePageCoordinates(window);

            Click(window, Find<Button>(toolbar, "ZenModeButton"));
            Assert.True(vm.IsZenMode);
            Assert.False(toolbar.IsVisible);
            Assert.False(Find<PageStripView>(window, "PageStripRegion").IsVisible);
            window.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.Control);
            Assert.False(vm.IsZenMode);
            Assert.True(toolbar.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertRulersSharePageCoordinates(Window window)
    {
        var host = Find<SkiaViewportHost>(window, "ViewportHost");
        var horizontal = Find<CanvasRulerControl>(window, "HorizontalRuler");
        var vertical = Find<CanvasRulerControl>(window, "VerticalRuler");
        Assert.Equal(host.Viewport, horizontal.Viewport);
        Assert.Equal(host.Viewport, vertical.Viewport);
        Rect canvas = BoundsIn(host, window);
        Rect top = BoundsIn(horizontal, window);
        Rect left = BoundsIn(vertical, window);
        Assert.InRange(canvas.Top - top.Bottom, 0, 1);
        Assert.InRange(canvas.Left - left.Right, 0, 1);
        Assert.Equal(canvas.Right, top.Right, 1);
        Assert.Equal(canvas.Bottom, left.Bottom, 1);

        foreach (Control surface in new Control[] { host, horizontal, vertical })
        {
            Point origin = surface.TranslatePoint(default, window)!.Value;
            Vector right = surface.TranslatePoint(new Point(8, 0), window)!.Value - origin;
            Vector down = surface.TranslatePoint(new Point(0, 8), window)!.Value - origin;
            Assert.Equal(new Vector(8, 0), right);
            Assert.Equal(new Vector(0, 8), down);
        }

        foreach ((double x, double y) in new[] { (0d, 0d), (horizontal.PageLengthMm, vertical.PageLengthMm) })
        {
            (double xDip, double yDip) = host.Viewport.PageMmToDip(x, y);
            Point page = host.TranslatePoint(new Point(xDip, yDip), window)!.Value;
            double rulerX = horizontal.Viewport.PanXDip + x * horizontal.Viewport.DipPerMm;
            double rulerY = vertical.Viewport.PanYDip + y * vertical.Viewport.DipPerMm;
            Assert.Equal(page.X, horizontal.TranslatePoint(new Point(rulerX, 0), window)!.Value.X, 1);
            Assert.Equal(page.Y, vertical.TranslatePoint(new Point(0, rulerY), window)!.Value.Y, 1);
        }
    }

    private static ServiceProvider BuildServices() => Bootstrapper.BuildServiceProvider(
        storageOptions: new ProjectStorageOptions(Path.Combine(Path.GetTempPath(), "ziwa-ui-" + Guid.NewGuid().ToString("N"))));

    private static T Find<T>(Control owner, string name) where T : Control =>
        Assert.IsType<T>(owner.FindControl<T>(name));

    private static void AssertInside(Control control, Control owner)
    {
        Rect bounds = BoundsIn(control, owner);
        Assert.True(bounds.Width > 0 && bounds.Right <= owner.Bounds.Width + 1 && bounds.X >= -1,
            $"{control.Name}: {bounds} outside {owner.Name}: {owner.Bounds.Size}");
    }

    private static Rect BoundsIn(Control element, Control owner)
    {
        Point start = element.TranslatePoint(default, owner)!.Value;
        Point end = element.TranslatePoint(new Point(element.Bounds.Width, element.Bounds.Height), owner)!.Value;
        return new Rect(start, end).Normalize();
    }

    private static void Click(Window window, Button button)
    {
        Point point = BoundsIn(button, window).Center;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Layout(window);
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
