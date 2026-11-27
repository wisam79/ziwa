using System.Windows.Input;
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
using Ziwa.App.Views.Inspector;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;
using static Ziwa.App.Tests.InspectorUi;

namespace Ziwa.App.Tests;

public sealed class InspectorNavigationHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(960, 640, false)]
    [InlineData(960, 640, true)]
    [InlineData(1360, 892, false)]
    [InlineData(1360, 892, true)]
    public void TabsAndFixedHeader_StayReachableWithLongArabicSelection(int width, int height, bool dark)
    {
        using ServiceProvider services = Bootstrapper.BuildServiceProvider();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var window = new MainWindow { DataContext = vm, Width = width, Height = height, RequestedThemeVariant = theme };
        try
        {
            const string name = "اسم طبقة نصية طويل جداً يحتوي على وصف طلب الطباعة وتفاصيل العميل والملاحظات النهائية على تصميم الصفحة";
            var text = vm.ActivePage.Nodes.OfType<TextNode>().First() with
            {
                Id = NodeId.New(),
                Name = name,
                BoundsMm = new RectMm(0.5, 10, 40, 12),
            };
            vm.Session.ApplyCommand("إضافة نص قريب للقص", doc => doc with
            {
                Pages = doc.Pages.SetItem(doc.ActivePageIndex, doc.ActivePage with { Nodes = doc.ActivePage.Nodes.Add(text) }),
            });
            vm.Session.SetSelection([text.Id]);
            window.Show();
            Layout(window);

            var panel = Visible<InspectorPanelView>(window);
            var tabs = Visible<TabControl>(panel, "InspectorTabs");
            Assert.Equal(new Size(width, height), window.ClientSize);
            Assert.Equal(theme, panel.ActualThemeVariant);
            Assert.Equal(FlowDirection.RightToLeft, panel.FlowDirection);
            Assert.Equal(new[] { "PropertiesTab", "LayersTab", "PageTab" }, tabs.Items.OfType<TabItem>().Select(t => t.Name));
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.Equal(0, vm.InspectorTabIndex);
            var properties = Visible<InspectorPropertiesView>(tabs);
            var summary = Visible<TextBlock>(properties, "PropertiesSelectionSummary");
            Assert.Equal(name, summary.Text);
            Assert.Equal(name, ToolTip.GetTip(summary));
            Assert.Equal(TextTrimming.CharacterEllipsis, summary.TextTrimming);

            var close = Visible<Button>(panel, "CloseInspectorButton");
            var warnings = Visible<Button>(panel, "InspectorWarningsButton");
            Assert.Same(vm.CloseInspectorCommand, close.Command);
            Assert.Equal(vm.PreflightSummaryText, AutomationProperties.GetName(warnings));
            Rect closeBounds = BoundsIn(close, window);
            Rect warningBounds = BoundsIn(warnings, window);
            Assert.False(closeBounds.Intersects(warningBounds));
            foreach (Control target in tabs.Items.Cast<Control>().Append(close).Append(warnings))
            {
                Rect bounds = BoundsIn(target, window);
                Assert.True(bounds.Width >= 28 && bounds.Height >= 28, target.Name);
                Assert.True(BoundsIn(panel, window).Contains(bounds), target.Name);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(target)));
            }

            var effects = Visible<Expander>(properties, "TextEffectsExpander");
            Assert.False(effects.IsExpanded);
            effects.IsExpanded = true;
            Layout(window);
            Visible<Button>(properties, "ApplyTextEffectsButton").BringIntoView();
            Layout(window);
            Assert.Equal(closeBounds, BoundsIn(close, window));
            Assert.Equal(warningBounds, BoundsIn(warnings, window));

            var firstTab = Visible<TabItem>(tabs, "PropertiesTab");
            Assert.True(firstTab.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Layout(window);
            Assert.Equal(1, vm.InspectorTabIndex);
            var list = Visible<ItemsControl>(Visible<InspectorLayersView>(tabs), "LayersListControl");
            var select = LayerAction(list, vm.SelectLayerCommand, text.Id);
            var hide = LayerAction(list, vm.ToggleLayerVisibilityCommand, text.Id);
            var lockButton = LayerAction(list, vm.ToggleLayerLockCommand, text.Id);
            Assert.Equal($"تحديد الطبقة {name}", AutomationProperties.GetName(select));
            Assert.Equal($"إخفاء الطبقة {name}", AutomationProperties.GetName(hide));
            Assert.Equal($"قفل الطبقة {name}", AutomationProperties.GetName(lockButton));
            var label = Assert.Single(select.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == name);
            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
            Assert.True(BoundsIn(panel, window).Contains(BoundsIn(select, window)));
            Assert.True(BoundsIn(lockButton, window).Right <= BoundsIn(hide, window).Left);
            Assert.True(BoundsIn(hide, window).Right <= BoundsIn(select, window).Left);

            // Navigate directly from Layers to the live warning, without selecting the Page tab first.
            Assert.True(warnings.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Layout(window);
            Assert.Equal(2, vm.InspectorTabIndex);
            Assert.Equal(2, tabs.SelectedIndex);
            var page = Visible<InspectorPageView>(tabs);
            var preflight = Visible<StudioCard>(page, "PreflightCard");
            var issue = Assert.Single(preflight.GetVisualDescendants().OfType<Button>(), b =>
                b.Command == vm.SelectPreflightIssueCommand && b.CommandParameter is PreflightIssue i && i.NodeId == text.Id);
            Assert.True(BoundsIn(page, window).Contains(BoundsIn(issue, window)));
            var pageScroll = Visible<ScrollViewer>(page, "PageScrollViewer");
            pageScroll.ScrollToEnd();
            Layout(window);
            Click(window, warnings);
            Assert.Equal(0, pageScroll.Offset.Y);
            Click(window, Visible<TabItem>(tabs, "PropertiesTab"));
            Assert.Equal(0, vm.InspectorTabIndex);
            Click(window, close);
            Assert.False(vm.IsInspectorOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LayerActions_KeepLayersActiveThroughSelectionLockHideAndUndo()
    {
        using ServiceProvider services = Bootstrapper.BuildServiceProvider();
        var vm = services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
        try
        {
            vm.Session.SetSelection([]);
            window.Show();
            Layout(window);
            var tabs = Visible<TabControl>(Visible<InspectorPanelView>(window), "InspectorTabs");
            var emptyPageLink = Visible<Button>(Visible<InspectorPropertiesView>(tabs), "ShowInspectorPageButton");
            Assert.Same(vm.ShowInspectorPageCommand, emptyPageLink.Command);
            Click(window, emptyPageLink);
            Assert.Equal(2, tabs.SelectedIndex);
            Click(window, Visible<TabItem>(tabs, "LayersTab"));
            var list = Visible<ItemsControl>(Visible<InspectorLayersView>(tabs), "LayersListControl");
            NodeId id = vm.LayerItems.First(row => row.CanSelectOnCanvas).Id;

            SceneLayerItemViewModel CurrentRow()
            {
                Assert.Equal(1, vm.InspectorTabIndex);
                Assert.Equal(1, tabs.SelectedIndex);
                Assert.True(list.IsEffectivelyVisible);
                return Assert.Single(vm.LayerItems, row => row.Id == id);
            }

            // Session changes rebuild the rows; never click a detached template from the previous state.
            Click(window, LayerAction(list, vm.SelectLayerCommand, id));
            Assert.True(CurrentRow().IsSelected);
            Assert.Contains(id, vm.Session.SelectedNodeIds);
            Click(window, LayerAction(list, vm.ToggleLayerLockCommand, id));
            Assert.True(CurrentRow().IsLocked);
            Assert.DoesNotContain(id, vm.Session.SelectedNodeIds);
            Assert.False(LayerAction(list, vm.SelectLayerCommand, id).IsEffectivelyEnabled);
            vm.UndoCommand.Execute(null);
            Layout(window);
            Assert.False(CurrentRow().IsLocked);
            Assert.True(LayerAction(list, vm.SelectLayerCommand, id).IsEffectivelyEnabled);

            vm.Session.SetSelection([id]);
            Layout(window);
            Assert.True(CurrentRow().IsSelected);
            Click(window, LayerAction(list, vm.ToggleLayerVisibilityCommand, id));
            Assert.False(CurrentRow().IsVisible);
            Assert.False(LayerAction(list, vm.SelectLayerCommand, id).IsEffectivelyEnabled);
            vm.UndoCommand.Execute(null);
            Layout(window);
            Assert.True(CurrentRow().IsVisible);
            Assert.True(LayerAction(list, vm.SelectLayerCommand, id).IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    private static Button LayerAction(ItemsControl list, ICommand command, NodeId id) =>
        Assert.Single(list.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible &&
            b.Command == command && b.CommandParameter is SceneLayerItemViewModel row && row.Id == id);
}

internal static class InspectorUi
{
    internal static T Visible<T>(Control root, string? name = null) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), c => c.IsEffectivelyVisible && (name is null || c.Name == name));

    internal static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    internal static Rect BoundsIn(Control target, Control owner) => new Rect(
        target.TranslatePoint(default, owner)!.Value,
        target.TranslatePoint(new Point(target.Bounds.Width, target.Bounds.Height), owner)!.Value).Normalize();

    internal static void Click(Window window, Control target)
    {
        target.BringIntoView();
        Layout(window);
        Point point = BoundsIn(target, window).Center;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Layout(window);
    }
}
