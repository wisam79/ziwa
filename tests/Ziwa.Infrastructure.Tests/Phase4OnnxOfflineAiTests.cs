using SkiaSharp;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.Ai;
using Ziwa.Infrastructure.Assets;
using Ziwa.Infrastructure.IO;
using Ziwa.Infrastructure.Persistence;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase4OnnxOfflineAiTests
{
    [Fact]
    public async Task OnnxBackgroundRemover_IsolatesPortraitWithHairFeathering_AndSupportsExternalModelAndCancellation()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-onnx-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var store = new AssetStore();
            byte[] portraitPng = CreateStudioPortraitWithHairPng(240, 280);
            AssetId originalId = store.RegisterAsset(portraitPng);

            var options = new OfflineAiOptions(tempRoot);
            using var remover = new OnnxBackgroundRemover(store, options);

            MaskEstimationResult result = await remover.RemoveBackgroundAsync(originalId);
            Assert.False(result.UsedExternalModel);
            Assert.NotEqual(originalId, result.CutoutAssetId);
            Assert.Equal(240, result.WidthPx);
            Assert.Equal(280, result.HeightPx);

            Assert.True(store.TryGetEncodedBytes(result.CutoutAssetId, out byte[]? cutoutBytes));
            using SKBitmap cutout = SKBitmap.Decode(cutoutBytes);

            // Top-left background pixel must be transparent
            Assert.True(cutout.GetPixel(10, 10).Alpha < 15);
            // Center face/torso must stay opaque
            Assert.True(cutout.GetPixel(120, 110).Alpha > 240);
            Assert.True(cutout.GetPixel(120, 240).Alpha > 240);

            // Hair-edge band around crown/temples preserves semi-transparent fine strands
            int fractionalCount = result.AlphaMask.Count(a => a is > 8 and < 245);
            Assert.True(fractionalCount > 10, $"Expected soft hair-edge alpha transitions, found {fractionalCount}.");

            // External .onnx model loading when placed in Models/background-remover.onnx
            Directory.CreateDirectory(options.ModelsDirectory);
            await File.WriteAllBytesAsync(options.BackgroundModelPath, EmbeddedOnnxModelBuilder.BuildTestExternal3ChannelSegmentationModelBytes());
            MaskEstimationResult externalResult = await remover.RemoveBackgroundAsync(originalId);
            Assert.True(externalResult.UsedExternalModel);

            // Instant cancellation test
            var delayedOptions = new OfflineAiOptions(Path.Combine(tempRoot, "empty"), InferenceStepDelayMs: 400);
            using var cancellableRemover = new OnnxBackgroundRemover(store, delayedOptions);
            using var cts = new CancellationTokenSource(35);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cancellableRemover.RemoveBackgroundAsync(originalId, cts.Token));
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
    public async Task OnnxFaceFramingService_DetectsPortraitFace_AndRejectsBlankImage()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-onnx-face-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var store = new AssetStore();
            byte[] portraitPng = CreateStudioPortraitWithHairPng(240, 280, faceCenterXRatio: 0.62f, faceCenterYRatio: 0.36f);
            AssetId portraitId = store.RegisterAsset(portraitPng);
            AssetId blankId = store.RegisterAsset(CreateBlankPng(160, 160));

            using var faceService = new OnnxFaceFramingService(store, new OfflineAiOptions(tempRoot));

            FaceDetectionOutcome portraitOutcome = await faceService.DetectPrimaryFaceAsync(portraitId);
            Assert.True(portraitOutcome.FaceFound);
            Assert.NotNull(portraitOutcome.FaceBox);
            double detectedCenterX = portraitOutcome.FaceBox!.Value.X + (portraitOutcome.FaceBox.Value.Width * 0.5);
            Assert.InRange(detectedCenterX, 240 * 0.42, 240 * 0.82);

            FaceDetectionOutcome blankOutcome = await faceService.DetectPrimaryFaceAsync(blankId);
            Assert.False(blankOutcome.FaceFound);
            Assert.Null(blankOutcome.FaceBox);
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
    public async Task FileImageAssetImporter_And_ZiwaProjectStore_PreserveOriginalAndCutoutAcrossSaveReload()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ziwa-ai-persist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var store = new AssetStore();
            var importer = new FileImageAssetImporter(store);

            string validPath = Path.Combine(tempRoot, "portrait.png");
            await File.WriteAllBytesAsync(validPath, CreateStudioPortraitWithHairPng(180, 220));
            AssetId originalId = await importer.ImportImageFileAsync(validPath);

            // Reject non-image payload disguised as .png (Magic Bytes guard)
            string fakePath = Path.Combine(tempRoot, "fake.png");
            await File.WriteAllTextAsync(fakePath, "not-a-real-png-header");
            await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportImageFileAsync(fakePath));

            using var remover = new OnnxBackgroundRemover(store, new OfflineAiOptions(tempRoot));
            MaskEstimationResult bg = await remover.RemoveBackgroundAsync(originalId);

            var imageNode = new ImageNode(
                Id: NodeId.New(),
                Name: "بورتريه معزول",
                BoundsMm: new RectMm(20, 20, 40, 60),
                CurrentAssetId: bg.CutoutAssetId,
                OriginalAssetId: originalId,
                Crop: new CropSpec(1.35, 0.1, -0.2));
            var page = new PageModel(PageId.New(), "صفحة 1", 100, 150, 0, 3, new SolidBackground(ColorRgba.White), [imageNode]);
            var doc = new ZiwaDocument(DocumentId.New(), 1, "مشروع عزل محلي", EditorWorkflow.Freeform, [page], 0, new PrintTicketSpec(300), DateTimeOffset.UtcNow);

            var projectStore = new ZiwaProjectStore(new AtomicFileWriter());
            var assetBridge = new RenderingProjectAssetStore(store);
            string projectPath = Path.Combine(tempRoot, "cutout.ziwa");
            await projectStore.SaveAsync(projectPath, assetBridge.Capture(doc));

            using var reloadStore = new AssetStore();
            ProjectSnapshot loaded = await projectStore.LoadAsync(projectPath);
            new RenderingProjectAssetStore(reloadStore).Restore(loaded);

            ImageNode loadedNode = Assert.IsType<ImageNode>(loaded.Document.ActivePage.Nodes[0]);
            Assert.Equal(bg.CutoutAssetId, loadedNode.CurrentAssetId);
            Assert.Equal(originalId, loadedNode.OriginalAssetId);
            Assert.True(reloadStore.TryGetEncodedBytes(originalId, out _));
            Assert.True(reloadStore.TryGetEncodedBytes(bg.CutoutAssetId, out _));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static byte[] CreateStudioPortraitWithHairPng(
        int w,
        int h,
        float faceCenterXRatio = 0.50f,
        float faceCenterYRatio = 0.38f)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas c = surface.Canvas;
        c.Clear(new SKColor(216, 226, 236, 255));

        float cx = w * faceCenterXRatio;
        float cy = h * faceCenterYRatio;
        float headRx = w * 0.16f;
        float headRy = h * 0.19f;

        // Torso / jacket
        using var jacketPaint = new SKPaint { Color = new SKColor(28, 48, 84, 255), IsAntialias = true };
        c.DrawOval(cx, h * 0.90f, w * 0.30f, h * 0.34f, jacketPaint);

        // Neck
        using var skinPaint = new SKPaint { Color = new SKColor(218, 168, 138, 255), IsAntialias = true };
        c.DrawRect(cx - (headRx * 0.42f), cy + (headRy * 0.65f), headRx * 0.84f, headRy * 0.65f, skinPaint);

        // Hair cap + fine translucent hair wisps around edge
        using var hairPaint = new SKPaint { Color = new SKColor(42, 30, 24, 255), IsAntialias = true };
        c.DrawOval(cx, cy - (headRy * 0.18f), headRx * 1.15f, headRy * 1.05f, hairPaint);

        using var wispPaint = new SKPaint
        {
            Color = new SKColor(42, 30, 24, 150),
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };
        for (int i = -6; i <= 6; i++)
        {
            float sx = cx + (i * headRx * 0.16f);
            float sy = cy - (headRy * 0.95f);
            c.DrawLine(sx, sy, sx + (i * 1.6f), sy - 8f, wispPaint);
        }

        // Face oval
        c.DrawOval(cx, cy, headRx, headRy, skinPaint);

        // Eyebrows & eyes (dark high-contrast symmetric landmarks)
        using var featurePaint = new SKPaint { Color = new SKColor(34, 24, 20, 255), IsAntialias = true };
        float eyeY = cy - (headRy * 0.14f);
        float eyeDx = headRx * 0.36f;
        c.DrawOval(cx - eyeDx, eyeY, headRx * 0.14f, headRy * 0.08f, featurePaint);
        c.DrawOval(cx + eyeDx, eyeY, headRx * 0.14f, headRy * 0.08f, featurePaint);
        c.DrawRect(cx - eyeDx - (headRx * 0.16f), eyeY - (headRy * 0.16f), headRx * 0.32f, 2.5f, featurePaint);
        c.DrawRect(cx + eyeDx - (headRx * 0.16f), eyeY - (headRy * 0.16f), headRx * 0.32f, 2.5f, featurePaint);

        // Mouth
        using var mouthPaint = new SKPaint { Color = new SKColor(152, 74, 72, 255), IsAntialias = true };
        c.DrawOval(cx, cy + (headRy * 0.44f), headRx * 0.24f, headRy * 0.07f, mouthPaint);

        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateBlankPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
