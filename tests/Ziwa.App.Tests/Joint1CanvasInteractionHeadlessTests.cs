using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.Controls;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Interaction;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class Joint1CanvasInteractionHeadlessTests
{
    [AvaloniaFact]
    public void InlineTextEdit_OpensUpdatesCommitsWithUndo_AndCancelDiscards()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TextNode textNode = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n is TextNode));
            string originalText = textNode.Text;

            vm.Session.SetSelection([textNode.Id]);
            vm.EnterSelectedTextEditCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsInlineTextEditOpen);
            Assert.Equal(originalText, vm.InlineTextEditDraft);

            vm.InlineTextEditDraft = "عنوان محدّث من المحرر الموضعي";
            vm.CommitInlineTextEditCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsInlineTextEditOpen);
            TextNode updated = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == textNode.Id));
            Assert.Equal("عنوان محدّث من المحرر الموضعي", updated.Text);

            vm.UndoCommand.Execute(null);
            TextNode reverted = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == textNode.Id));
            Assert.Equal(originalText, reverted.Text);

            vm.EnterSelectedTextEditCommand.Execute(null);
            vm.InlineTextEditDraft = "مسودة ملغاة";
            vm.CancelInlineTextEditCommand.Execute(null);
            Assert.False(vm.IsInlineTextEditOpen);
            TextNode unchanged = Assert.IsType<TextNode>(vm.ActivePage.Nodes.First(n => n.Id == textNode.Id));
            Assert.Equal(originalText, unchanged.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CropMode_QuickBarSwitchingZoomResetApplyAndCancel_WorkEndToEnd()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var asset = new AssetId("joint1-crop-asset");
            var imageNode = new ImageNode(
                Id: NodeId.New(),
                Name: "صورة اختبار القص",
                BoundsMm: new RectMm(30, 30, 80, 60),
                OriginalAssetId: asset,
                CurrentAssetId: asset,
                Crop: CropSpec.Identity);

            vm.Session.ApplyCommand("إدراج صورة للقص", doc => FreeformSceneOperations.AddNode(doc, imageNode));
            vm.Session.SetSelection([imageNode.Id]);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsImageSelected);
            Assert.True(vm.IsNotCropModeActive);

            vm.EnterSelectedCropModeCommand.Execute(null);
            Assert.True(vm.IsCropModeActive);
            Assert.False(vm.IsNotCropModeActive);
            Assert.Equal("100%", vm.CropZoomPercentText);

            vm.ZoomInCropCommand.Execute(null);
            vm.ZoomInCropCommand.Execute(null);
            Assert.NotEqual("100%", vm.CropZoomPercentText);

            vm.ApplyCropModeCommand.Execute(null);
            Assert.False(vm.IsCropModeActive);
            ImageNode cropped = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == imageNode.Id));
            Assert.True(cropped.Crop.Zoom > 1.2);

            vm.EnterSelectedCropModeCommand.Execute(null);
            vm.ResetCropSpecCommand.Execute(null);
            Assert.Equal("100%", vm.CropZoomPercentText);
            vm.CancelCropModeCommand.Execute(null);
            Assert.False(vm.IsCropModeActive);

            ImageNode afterCancel = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == imageNode.Id));
            Assert.Equal(cropped.Crop.Zoom, afterCancel.Crop.Zoom, precision: 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CanvasRulersTrackPointerAndSelection_AndDuplicateOrderSelectAllFitWork()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            SkiaViewportHost host = Assert.IsType<SkiaViewportHost>(window.FindControl<SkiaViewportHost>("ViewportHost"));
            CanvasRulerControl horizontal = Assert.IsType<CanvasRulerControl>(window.FindControl<CanvasRulerControl>("HorizontalRuler"));
            CanvasRulerControl vertical = Assert.IsType<CanvasRulerControl>(window.FindControl<CanvasRulerControl>("VerticalRuler"));

            Point center = host.TranslatePoint(new Point(host.Bounds.Width * 0.5, host.Bounds.Height * 0.5), window)!.Value;
            window.MouseMove(center, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(horizontal.PointerPositionMm);
            Assert.NotNull(vertical.PointerPositionMm);

            int initialCount = vm.ActivePage.Nodes.Length;
            Assert.True(initialCount >= 2);

            vm.SelectAllNodesCommand.Execute(null);
            Assert.Equal(vm.ActivePage.Nodes.Count(n => n.IsVisible && !n.IsLocked), vm.Session.SelectedNodeIds.Length);
            Assert.NotNull(horizontal.SelectionStartMm);
            Assert.NotNull(horizontal.SelectionEndMm);
            Assert.True(horizontal.SelectionEndMm > horizontal.SelectionStartMm);

            NodeId firstId = vm.ActivePage.Nodes[0].Id;
            vm.Session.SetSelection([firstId]);
            vm.BringForwardSelectionCommand.Execute(null);
            Assert.Equal(firstId, vm.ActivePage.Nodes[1].Id);

            vm.SendBackwardSelectionCommand.Execute(null);
            Assert.Equal(firstId, vm.ActivePage.Nodes[0].Id);

            vm.DuplicateSelectionCommand.Execute(null);
            Assert.Equal(initialCount + 1, vm.ActivePage.Nodes.Length);
            NodeId dupId = Assert.Single(vm.Session.SelectedNodeIds);
            Assert.NotEqual(firstId, dupId);

            double prevZoom = host.Viewport.Zoom;
            vm.ZoomInCommand.Execute(null);
            Assert.True(host.Viewport.Zoom > prevZoom);

            vm.FitPageCommand.Execute(null);
            Assert.True(host.Viewport.Zoom > 0.05);
        }
        finally
        {
            window.Close();
        }
    }
}
