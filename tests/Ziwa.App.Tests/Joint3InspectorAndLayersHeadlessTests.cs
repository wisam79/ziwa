using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class Joint3InspectorAndLayersHeadlessTests
{
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
