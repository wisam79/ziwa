using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;

namespace Ziwa.App.Tests;

public sealed class Joint8AiMaskAndScannerHeadlessTests
{
    [AvaloniaFact]
    public Task MaskEditorInvertAndBrushActiveState_WorkCleanly() =>
        RunWithWindowAsync(async (vm, _) =>
        {
            await vm.ImportImageCommand.ExecuteAsync(null);
            await vm.OpenMaskEditorCommand.ExecuteAsync(null);
            Assert.True(vm.IsMaskEditorOpen);
            Assert.True(vm.IsEraseBrushActive);
            Assert.False(vm.IsRestoreBrushActive);

            vm.SetRestoreMaskBrushCommand.Execute(null);
            Assert.False(vm.IsEraseBrushActive);
            Assert.True(vm.IsRestoreBrushActive);

            byte beforeAlpha = vm.ActiveMaskSession!.AlphaSpan[0];
            vm.InvertMaskCommand.Execute(null);
            byte afterInvert = vm.ActiveMaskSession.AlphaSpan[0];
            Assert.Equal((byte)(255 - beforeAlpha), afterInvert);
            Assert.True(vm.CanUndoMaskStroke);

            vm.UndoMaskStrokeCommand.Execute(null);
            Assert.Equal(beforeAlpha, vm.ActiveMaskSession.AlphaSpan[0]);
            vm.CloseMaskEditorCommand.Execute(null);
        });

    [AvaloniaFact]
    public Task DocumentScannerHeadless_FullQuadReset_WorksCleanly() =>
        RunWithWindowAsync(async (vm, scanAssetId) =>
        {
            await vm.OpenScannerWithAssetAsync(scanAssetId);
            Assert.True(vm.IsDocumentScannerOpen);
            vm.AddManualScanRegionCommand.Execute(null);
            int idx = vm.SelectedScannerRegionIndex;
            Assert.True(vm.ScannerRegions[idx].Quad.TopLeft.X > 1.0);

            vm.ResetSelectedScanRegionToFullImageCommand.Execute(null);
            Assert.Equal(0.0, vm.ScannerRegions[idx].Quad.TopLeft.X, 1);
            Assert.Equal(0.0, vm.ScannerRegions[idx].Quad.TopLeft.Y, 1);
            Assert.Equal(159.0, vm.ScannerRegions[idx].Quad.BottomRight.X, 1);
            Assert.Equal(119.0, vm.ScannerRegions[idx].Quad.BottomRight.Y, 1);
        });

    private static async Task RunWithWindowAsync(Func<MainWindowViewModel, AssetId, Task> action)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-joint8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            byte[] pngBytes = CreateSyntheticPng(160, 120);
            string file = Path.Combine(tempRoot, "scan.png");
            await File.WriteAllBytesAsync(file, pngBytes);
            var dialogs = new TestDialogs(file);

            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(tempRoot), dialogs);
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            AssetStore assetStore = sp.GetRequiredService<AssetStore>();
            AssetId scanAssetId = assetStore.RegisterAsset(pngBytes);

            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                await action(vm, scanAssetId);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static byte[] CreateSyntheticPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Navy, Style = SKPaintStyle.Fill };
        surface.Canvas.DrawRect(24, 24, w - 48, h - 48, paint);
        using SKImage img = surface.Snapshot();
        using SKData encoded = img.Encode(SKEncodedImageFormat.Png, 90);
        return encoded.ToArray();
    }

    private sealed class TestDialogs(string imagePath) : IProjectDialogs
    {
        public Task<string?> PickOpenImageAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(imagePath);
        public Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
