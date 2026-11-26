using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using Ziwa.App.Services;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.App.Views.Workspace;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.Ai;

namespace Ziwa.App.Tests;

public sealed class Phase4OfflineAiHeadlessTests
{
    [AvaloniaFact]
    public async Task OfflineAiWorkflow_BackgroundMaskBrushFaceFramingAndRestore_AreAccessibleAndUndoable()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p402-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string portraitFile = Path.Combine(tempRoot, "portrait.png");
        await File.WriteAllBytesAsync(portraitFile, CreateSyntheticPortraitPng(200, 240));
        var dialogs = new TestDialogs(portraitFile);

        try
        {
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(tempRoot), dialogs);
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            // Import portrait via unified command
            await vm.ImportImageCommand.ExecuteAsync(null);
            ImageNode imported = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.Last());
            AssetId originalId = imported.OriginalAssetId;
            Assert.True(vm.IsPhotoAiTargetVisible);

            // Verify AiMaskEditorView buttons all have accessibility names and tooltips
            AiMaskEditorView maskEditor = Assert.Single(window.GetVisualDescendants().OfType<AiMaskEditorView>());
            Assert.Equal(FlowDirection.RightToLeft, maskEditor.FlowDirection);
            foreach (Button btn in maskEditor.GetVisualDescendants().OfType<Button>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(btn)));
                Assert.NotNull(ToolTip.GetTip(btn));
            }

            // 1. Offline Background Removal
            await vm.RemoveBackgroundCommand.ExecuteAsync(null);
            ImageNode afterBg = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == imported.Id));
            Assert.NotEqual(originalId, afterBg.CurrentAssetId);
            Assert.Equal(originalId, afterBg.OriginalAssetId);

            // 2. Open Mask Editor, paint with Erase/Restore, toggle Before/After mode, Undo stroke, and Commit
            await vm.OpenMaskEditorCommand.ExecuteAsync(null);
            Assert.True(vm.IsMaskEditorOpen);
            Assert.NotNull(vm.ActiveMaskSession);
            Assert.NotNull(vm.MaskEditorOriginalBitmap);
            Assert.NotNull(vm.MaskEditorCutoutBitmap);

            vm.SetEraseMaskBrushCommand.Execute(null);
            vm.ActiveMaskSession!.BeginStroke();
            vm.ActiveMaskSession.ApplyBrushSegment(100f, 100f, 108f, 108f, 12f, 0.5f, MaskBrushMode.Erase);
            vm.ActiveMaskSession.EndStroke();
            vm.NotifyMaskEditorModified();
            Assert.True(vm.CanUndoMaskStroke);

            vm.SelectedMaskPreviewMode = MaskPreviewMode.SplitBeforeAfter;
            Assert.True(vm.IsSplitMaskPreview);

            await vm.ApplyMaskEditorCommand.ExecuteAsync(null);
            Assert.False(vm.IsMaskEditorOpen);
            ImageNode afterMask = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == imported.Id));
            Assert.Equal(originalId, afterMask.OriginalAssetId);

            // 3. Auto Face Framing
            await vm.AutoFrameFaceCommand.ExecuteAsync(null);
            ImageNode afterFrame = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == imported.Id));
            Assert.True(afterFrame.Crop.Zoom >= 1.0);

            // 4. Restore Original
            vm.RestoreOriginalImageCommand.Execute(null);
            ImageNode restored = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == imported.Id));
            Assert.Equal(originalId, restored.CurrentAssetId);
            Assert.Equal(CropSpec.Identity, restored.Crop);

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
    public async Task OfflineAiCancellation_CancelsPromptlyAndLeavesDocumentUntouched()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-p402-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var aiOptions = new OfflineAiOptions(Path.Combine(tempRoot, "Models"), InferenceStepDelayMs: 500);
            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(
                new ProjectStorageOptions(tempRoot),
                new TestDialogs(null),
                aiOptions);

            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            window.Show();

            ImageNode img = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n is ImageNode));
            vm.Session.SetSelection([img.Id]);
            AssetId beforeAsset = img.CurrentAssetId;

            Task runTask = vm.RemoveBackgroundCommand.ExecuteAsync(null);
            window.UpdateLayout();
            var status = Assert.IsType<WorkspaceStatusBarView>(window.FindControl<WorkspaceStatusBarView>("StatusBarRegion"));
            var cancel = Assert.IsType<Button>(status.FindControl<Button>("CancelAiStatusBarButton"));
            Assert.True(cancel.IsVisible);
            var point = cancel.TranslatePoint(new Point(cancel.Bounds.Width / 2, cancel.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await runTask;
            Assert.False(cancel.IsVisible);

            ImageNode afterImg = Assert.IsType<ImageNode>(vm.ActivePage.Nodes.First(n => n.Id == img.Id));
            Assert.Equal(beforeAsset, afterImg.CurrentAssetId);
            Assert.False(vm.IsAiBusy);

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
    public void AiShortcutGestures_ResolveThroughUnifiedRouter()
    {
        Assert.Equal("Ctrl+Shift+B", KeyboardCommandRouter.GetGesture(Key.B, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal("Ctrl+Shift+F", KeyboardCommandRouter.GetGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal("Ctrl+Shift+I", KeyboardCommandRouter.GetGesture(Key.I, KeyModifiers.Control | KeyModifiers.Shift));
    }

    private static byte[] CreateSyntheticPortraitPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas c = surface.Canvas;
        c.Clear(new SKColor(214, 224, 236, 255));
        float cx = w * 0.58f;
        float cy = h * 0.36f;
        using var jacket = new SKPaint { Color = new SKColor(32, 52, 86, 255), IsAntialias = true };
        c.DrawOval(cx, h * 0.9f, w * 0.28f, h * 0.32f, jacket);
        using var hair = new SKPaint { Color = new SKColor(44, 30, 24, 255), IsAntialias = true };
        c.DrawOval(cx, cy - 12f, 34f, 44f, hair);
        using var skin = new SKPaint { Color = new SKColor(218, 168, 138, 255), IsAntialias = true };
        c.DrawOval(cx, cy, 30f, 38f, skin);
        using var eyes = new SKPaint { Color = new SKColor(32, 22, 20, 255), IsAntialias = true };
        c.DrawOval(cx - 10f, cy - 6f, 4f, 3f, eyes);
        c.DrawOval(cx + 10f, cy - 6f, 4f, 3f, eyes);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class TestDialogs(string? imagePath) : IProjectDialogs
    {
        public Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsavedChangesChoice.Cancel);
        public Task<string?> PickOpenImageAsync(CancellationToken cancellationToken = default) => Task.FromResult(imagePath);
    }
}
