using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using Ziwa.App.Controls;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Scanner;
using Ziwa.Infrastructure.Scanner;
using Ziwa.Rendering.Assets;

namespace Ziwa.App.Tests;

public sealed class Phase4DocumentScannerHeadlessTests
{
    [AvaloniaFact]
    public async Task DocumentScannerView_MagnifierCornerDragIdSplitAndSingleUndoInsert_WorkEndToEnd()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p403-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(tempRoot));
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            AssetStore assetStore = sp.GetRequiredService<AssetStore>();
            AssetId scanAssetId = assetStore.RegisterAsset(CreateTwoCardScanPng(520, 360));

            var window = new MainWindow { DataContext = vm };
            window.Show();

            DocumentScannerView scannerView = Assert.Single(window.GetVisualDescendants().OfType<DocumentScannerView>());
            Assert.Equal(FlowDirection.RightToLeft, scannerView.FlowDirection);
            foreach (Button btn in scannerView.GetVisualDescendants().OfType<Button>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)));
                Assert.NotNull(ToolTip.GetTip(btn));
            }

            await vm.OpenScannerWithAssetAsync(scanAssetId);
            Assert.True(vm.IsDocumentScannerOpen);
            Assert.Equal(2, vm.ScannerRegions.Count);
            Assert.NotNull(vm.ScannerSourceBitmap);
            Assert.NotNull(vm.ScannerRectifiedPreviewBitmap);

            // Force layout pass so ScannerQuadCanvasControl has non-zero bounds for corner drag + loupe test
            window.UpdateLayout();
            ScannerQuadCanvasControl quadCanvas = Assert.Single(scannerView.GetVisualDescendants().OfType<ScannerQuadCanvasControl>());
            Assert.True(quadCanvas.TryGetCornerDipPosition(0, 0, out Point cornerDip));
            Point rootCornerDip = quadCanvas.TranslatePoint(cornerDip, window) ?? cornerDip;
            PointPx beforeCorner = vm.ScannerRegions[0].Quad.TopLeft;

            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            quadCanvas.RaiseEvent(new PointerPressedEventArgs(
                quadCanvas,
                pointer,
                window,
                rootCornerDip,
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None));

            Assert.True(quadCanvas.IsMagnifierLoupeVisible);

            Point rootMovedDip = new(rootCornerDip.X + 12.0, rootCornerDip.Y + 8.0);
            quadCanvas.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent,
                quadCanvas,
                pointer,
                window,
                rootMovedDip,
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
                KeyModifiers.None));

            quadCanvas.RaiseEvent(new PointerReleasedEventArgs(
                quadCanvas,
                pointer,
                window,
                rootMovedDip,
                0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None,
                MouseButton.Left));

            Assert.False(quadCanvas.IsMagnifierLoupeVisible);
            Assert.NotEqual(beforeCorner, vm.ScannerRegions[0].Quad.TopLeft);

            // Split first card horizontally (2 -> 3 regions)
            vm.SplitScanCardHorizontalCommand.Execute(null);
            Assert.Equal(3, vm.ScannerRegions.Count);

            // Switch document filter to OtsuBlackAndWhite
            vm.SelectedScanFilterOption = vm.AvailableScanFilterOptions.First(o => o.Filter == DocumentScanFilterKind.OtsuBlackAndWhite);
            Assert.Equal(DocumentScanFilterKind.OtsuBlackAndWhite, vm.ScannerRegions[0].Filter);

            // Insert all extracted cards into Freeform in 1 Undo step
            int nodesBefore = vm.ActivePage.Nodes.Length;
            await vm.InsertScannedToFreeformCommand.ExecuteAsync(null);
            Assert.False(vm.IsDocumentScannerOpen);
            Assert.Equal(nodesBefore + 3, vm.ActivePage.Nodes.Length);
            Assert.Equal(1, vm.Session.UndoCount);

            vm.UndoCommand.Execute(null);
            Assert.Equal(nodesBefore, vm.ActivePage.Nodes.Length);

            // Re-open and insert into Collage in 1 Undo step
            await vm.OpenScannerWithAssetAsync(scanAssetId);
            await vm.InsertScannedToCollageCommand.ExecuteAsync(null);
            Assert.False(vm.IsDocumentScannerOpen);
            Assert.NotNull(vm.ActivePage.CollageLayout);
            Assert.Equal(2, vm.ActivePage.CollageLayout!.Slots.Count(s => s.AssignedAssetId is not null));

            window.Close();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task DocumentScanner_LocksEditingToolsDuringProcessing_AndCancelsCleanly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p403-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                new ProjectStorageOptions(tempRoot),
                scannerOptions: new DocumentScannerOptions(ProcessingStepDelayMs: 450));

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            AssetStore assetStore = sp.GetRequiredService<AssetStore>();
            AssetId scanAssetId = assetStore.RegisterAsset(CreateTwoCardScanPng(400, 280));

            var window = new MainWindow { DataContext = vm };
            window.Show();

            Task openTask = vm.OpenScannerWithAssetAsync(scanAssetId);
            await Task.Delay(40);
            Assert.True(vm.IsScannerProcessing);
            Assert.False(vm.IsScannerToolsEnabled);

            vm.CancelScannerOperationCommand.Execute(null);
            await openTask;

            Assert.False(vm.IsScannerProcessing);
            Assert.True(vm.IsScannerToolsEnabled);
            window.Close();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ScannerShortcutGesture_ResolvesThroughUnifiedRouter()
    {
        Assert.Equal("Ctrl+Shift+D", KeyboardCommandRouter.GetGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift));
    }

    private static byte[] CreateTwoCardScanPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas c = surface.Canvas;
        c.Clear(new SKColor(22, 26, 34, 255));
        using var card = new SKPaint { Color = new SKColor(242, 244, 248, 255), IsAntialias = true };
        c.DrawRect(36, 55, 190, 240, card);
        c.DrawRect(280, 60, 195, 235, card);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
