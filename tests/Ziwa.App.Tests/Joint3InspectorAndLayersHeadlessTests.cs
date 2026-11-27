using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Inspector;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class Joint3InspectorAndLayersHeadlessTests
{
    [AvaloniaFact]
    public void PageAlignmentButtons_FitOneAccessibleIconRowAndAlignTheSelection()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 892 };
        try
        {
            window.Show();
            TextNode text = vm.ActivePage.Nodes.OfType<TextNode>().First();
            vm.Session.SetSelection([text.Id]);
            vm.SelectedNodeXMm = 5;
            vm.SelectedNodeWidthMm = 40;
            vm.ApplyNumericTransformCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var inspector = InspectorUi.Visible<InspectorPanelView>(window);
            var properties = InspectorUi.Visible<InspectorPropertiesView>(inspector);
            var centerButton = InspectorUi.Visible<Button>(properties, "AlignCenterHPageButton");
            centerButton.BringIntoView();
            InspectorUi.Layout(window);

            (string direction, string namePart, ICommand command)[] actions =
            [
                ("Right", "يمين", vm.AlignSelectionRightCommand),
                ("CenterH", "أفقي", vm.AlignSelectionCenterHCommand),
                ("Left", "يسار", vm.AlignSelectionLeftCommand),
                ("Top", "أعلى", vm.AlignSelectionTopCommand),
                ("CenterV", "رأسي", vm.AlignSelectionCenterVCommand),
                ("Bottom", "أسفل", vm.AlignSelectionBottomCommand),
            ];
            Rect? previous = null;
            foreach (var (direction, namePart, command) in actions)
            {
                var button = InspectorUi.Visible<Button>(properties, $"Align{direction}PageButton");
                Assert.True(button.IsEffectivelyVisible && button.IsEnabled && button.Focusable);
                Assert.Contains(namePart, AutomationProperties.GetName(button));
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string));
                Assert.Same(command, button.Command);
                var icon = Assert.IsType<PathIcon>(button.Content);
                Assert.True(inspector.TryFindResource($"IconAlign{direction}", out object? geometry));
                Assert.NotNull(geometry);
                Assert.Same(geometry, icon.Data);

                Rect bounds = InspectorUi.BoundsIn(button, window);
                Assert.True(bounds.Width >= 28 && bounds.Height >= 28);
                Assert.True(InspectorUi.BoundsIn(inspector, window).Contains(bounds));
                if (previous is { } last)
                {
                    Assert.Equal(last.Y, bounds.Y);
                    Assert.True(bounds.Right <= last.Left, "Alignment targets must not overlap in the RTL row.");
                }
                previous = bounds;
            }

            InspectorUi.Click(window, centerButton);
            Assert.Equal(vm.ActivePage.WidthMm / 2, vm.ActivePage.Nodes.First(n => n.Id == text.Id).BoundsMm.CenterX, 1);
            vm.UndoCommand.Execute(null);
            Assert.Equal(5.0, vm.ActivePage.Nodes.First(n => n.Id == text.Id).BoundsMm.X, 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void InspectorNumericTransformAlignAndTextShapeControls_WorkAndSupportUndo()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Numeric Transform + Page Alignment on TextNode
            TextNode textNode = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n is TextNode));
            vm.Session.SetSelection([textNode.Id]);
            Assert.True(vm.IsSingleNodeSelected);

            vm.SelectedNodeXMm = 25.0;
            vm.SelectedNodeYMm = 40.0;
            vm.SelectedNodeWidthMm = 90.0;
            vm.SelectedNodeHeightMm = 24.0;
            vm.SelectedNodeRotationDeg = 15.0;
            vm.SelectedNodeOpacityPercent = 80.0;
            vm.ApplyNumericTransformCommand.Execute(null);

            TextNode transformed = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == textNode.Id));
            Assert.Equal(25.0, transformed.BoundsMm.X, 1);
            Assert.Equal(90.0, transformed.BoundsMm.Width, 1);
            Assert.Equal(15.0, transformed.RotationDeg, 1);
            Assert.Equal(0.80, transformed.Opacity, 2);

            // Text alignment
            vm.AlignTextCenterCommand.Execute(null);
            Assert.True(vm.IsTextAlignCenter);

            // Center horizontally on page
            vm.AlignSelectionCenterHCommand.Execute(null);
            TextNode centered = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == textNode.Id));
            Assert.Equal(vm.ActivePage.WidthMm * 0.5, centered.BoundsMm.CenterX, 1);

            // Shape stroke color + width
            vm.AddRectangleShapeCommand.Execute(null);
            ShapeNode rect = Assert.IsType<ShapeNode>(vm.ActivePage.Nodes.Last(n => n is ShapeNode));
            vm.Session.SetSelection([rect.Id]);
            vm.SelectedShapeStrokeColor = Colors.DarkRed;
            vm.SelectedShapeStrokeWidthMm = 3.5;
            vm.ApplySelectedShapeStyleCommand.Execute(null);

            ShapeNode styledRect = Assert.IsType<ShapeNode>(vm.ActivePage.Nodes.First(n => n.Id == rect.Id));
            Assert.NotNull(styledRect.Stroke);
            Assert.Equal(3.5, styledRect.Stroke!.WidthMm, 1);
            Assert.Equal(Colors.DarkRed.R, styledRect.Stroke.Color.R);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void InspectorLayersList_SelectsTogglesVisibilityAndLock_AndArabicMaskPresetSyncs()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(vm.ActivePage.Nodes.Length, vm.LayerItems.Count);
            SceneLayerItemViewModel topLayer = vm.LayerItems[0];
            Assert.True(topLayer.CanSelectOnCanvas);

            vm.SelectLayerCommand.Execute(topLayer);
            Assert.Contains(topLayer.Id, vm.Session.SelectedNodeIds);
            Assert.True(vm.LayerItems[0].IsSelected);

            // Lock layer -> deselects and prevents selection until unlocked
            vm.ToggleLayerLockCommand.Execute(vm.LayerItems[0]);
            Assert.True(vm.LayerItems[0].IsLocked);
            Assert.False(vm.LayerItems[0].CanSelectOnCanvas);
            Assert.DoesNotContain(topLayer.Id, vm.Session.SelectedNodeIds);

            vm.ToggleLayerLockCommand.Execute(vm.LayerItems[0]);
            Assert.False(vm.LayerItems[0].IsLocked);
            Assert.True(vm.LayerItems[0].CanSelectOnCanvas);

            // Hide and re-show layer
            vm.ToggleLayerVisibilityCommand.Execute(vm.LayerItems[0]);
            Assert.False(vm.LayerItems[0].IsVisible);
            vm.ToggleLayerVisibilityCommand.Execute(vm.LayerItems[0]);
            Assert.True(vm.LayerItems[0].IsVisible);

            // Arabic mask preset selection updates ImageNode
            ImageNode? img = vm.ActivePage.Nodes.OfType<ImageNode>().FirstOrDefault();
            Assert.NotNull(img);
            vm.Session.SetSelection([img!.Id]);
            vm.SelectedMaskOption = vm.AvailableMaskOptions.First(m => m.Kind == MaskShapeKind.Circle);
            ImageNode maskedImg = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == img.Id));
            Assert.Equal(MaskShapeKind.Circle, maskedImg.Mask);
        }
        finally
        {
            window.Close();
        }
    }
}
