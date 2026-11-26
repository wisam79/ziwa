using System.Collections.Immutable;
using SkiaSharp;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Scanner;
using Ziwa.Infrastructure.Scanner;
using Ziwa.Rendering.Ai;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Filters;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase4OpenCvScannerTests
{
    [Fact]
    public async Task OpenCvDocumentScanner_DetectsSkewedSingleDocument_AndMultipleIdCards_AndSupportsCancellation()
    {
        using var store = new AssetStore();
        var scanner = new OpenCvDocumentScanner(store);

        // 1. Skewed single document on dark wood desk
        AssetId singleDocAsset = store.RegisterAsset(CreateSkewedSingleDocumentPng(480, 360));
        ScanDetectionResult singleDetection = await scanner.DetectDocumentsAsync(singleDocAsset, ScanDetectionMode.AutoSingleOrMulti);
        Assert.Single(singleDetection.Regions);
        DocumentQuadPx detectedQuad = singleDetection.Regions[0].Quad;
        Assert.True(detectedQuad.TopLeft.X is >= 35 and <= 125);
        Assert.True(detectedQuad.BottomRight.X is >= 355 and <= 445);

        ImmutableArray<ExtractedScanDocument> extractedSingle = await scanner.ExtractDocumentsAsync(
            singleDocAsset,
            [new ScanExtractionRequestItem(singleDetection.Regions[0].Id, "مستند", detectedQuad, DocumentScanFilterKind.MagicColor)]);
        Assert.Single(extractedSingle);
        Assert.NotEqual(extractedSingle[0].UnfilteredAssetId, extractedSingle[0].FilteredAssetId);

        // 2. Two tilted ID cards on the same photo -> detects both cards
        AssetId multiCardAsset = store.RegisterAsset(CreateTwoIdCardsPhotoPng(600, 420));
        ScanDetectionResult multiDetection = await scanner.DetectDocumentsAsync(multiCardAsset, ScanDetectionMode.AutoSingleOrMulti);
        Assert.Equal(2, multiDetection.Regions.Length);

        // 3. Cancellation check
        var slowScanner = new OpenCvDocumentScanner(store, new DocumentScannerOptions(ProcessingStepDelayMs: 350));
        using var cts = new CancellationTokenSource(35);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            slowScanner.DetectDocumentsAsync(singleDocAsset, ScanDetectionMode.AutoSingleOrMulti, cts.Token));
    }

    [Fact]
    public async Task OpenCvDocumentScanner_FilterOutputs_MatchImageFilterPipeline_PixelForPixel()
    {
        using var store = new AssetStore();
        var scanner = new OpenCvDocumentScanner(store);

        AssetId cardAsset = store.RegisterAsset(CreateRectifiedSampleCardPng(160, 100));
        var fullQuad = new DocumentQuadPx(
            new PointPx(0, 0),
            new PointPx(159, 0),
            new PointPx(159, 99),
            new PointPx(0, 99));

        foreach (DocumentScanFilterKind kind in DocumentScanFilterCatalog.All.Where(k => k != DocumentScanFilterKind.None))
        {
            ImmutableArray<ExtractedScanDocument> extracted = await scanner.ExtractDocumentsAsync(
                cardAsset,
                [new ScanExtractionRequestItem(Guid.NewGuid(), "بطاقة", fullQuad, kind)]);

            ExtractedScanDocument doc = Assert.Single(extracted);
            Assert.True(store.TryGetEncodedBytes(doc.UnfilteredAssetId, out byte[]? unfilteredBytes));
            Assert.True(store.TryGetEncodedBytes(doc.FilteredAssetId, out byte[]? filteredBytes));

            using SKBitmap unfilteredBmp = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(unfilteredBytes!);
            using SKBitmap scannerFilteredBmp = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(filteredBytes!);

            using SKBitmap pipelineBmp = unfilteredBmp.Copy()!;
            DocumentScanFilterEngine.ApplyInPlace(pipelineBmp, kind);

            Assert.Equal(pipelineBmp.Width, scannerFilteredBmp.Width);
            Assert.Equal(pipelineBmp.Height, scannerFilteredBmp.Height);

            byte[] expectedRgba = new byte[pipelineBmp.Width * pipelineBmp.Height * 4];
            byte[] actualRgba = new byte[scannerFilteredBmp.Width * scannerFilteredBmp.Height * 4];
            AlphaMaskCompositor.ReadRgbaPixels(pipelineBmp, expectedRgba);
            AlphaMaskCompositor.ReadRgbaPixels(scannerFilteredBmp, actualRgba);
            Assert.Equal(expectedRgba, actualRgba);
        }
    }

    private static byte[] CreateSkewedSingleDocumentPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas c = surface.Canvas;
        c.Clear(new SKColor(28, 34, 44, 255));

        using var path = new SKPath();
        path.MoveTo(90f, 55f);
        path.LineTo(410f, 38f);
        path.LineTo(395f, 315f);
        path.LineTo(62f, 295f);
        path.Close();

        using var paper = new SKPaint { Color = new SKColor(246, 240, 212, 255), IsAntialias = true };
        c.DrawPath(path, paper);

        using var ink = new SKPaint { Color = new SKColor(28, 32, 38, 255), IsAntialias = true };
        c.DrawRect(150f, 110f, 160f, 18f, ink);
        c.DrawRect(150f, 150f, 120f, 14f, ink);

        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateTwoIdCardsPhotoPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas c = surface.Canvas;
        c.Clear(new SKColor(24, 28, 36, 255));

        DrawTiltedCard(c, 165f, 210f, 190f, 120f, -8f, new SKColor(244, 246, 250, 255));
        DrawTiltedCard(c, 435f, 210f, 190f, 120f, 7f, new SKColor(242, 244, 248, 255));

        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawTiltedCard(SKCanvas c, float cx, float cy, float cardW, float cardH, float angleDeg, SKColor fill)
    {
        c.Save();
        c.Translate(cx, cy);
        c.RotateDegrees(angleDeg);
        using var cardPaint = new SKPaint { Color = fill, IsAntialias = true };
        c.DrawRoundRect(SKRect.Create(-cardW * 0.5f, -cardH * 0.5f, cardW, cardH), 8f, 8f, cardPaint);
        using var stripePaint = new SKPaint { Color = new SKColor(18, 86, 164, 255), IsAntialias = true };
        c.DrawRect(-cardW * 0.4f, -cardH * 0.3f, cardW * 0.8f, 14f, stripePaint);
        c.Restore();
    }

    private static byte[] CreateRectifiedSampleCardPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas c = surface.Canvas;
        c.Clear(new SKColor(242, 230, 178, 255));
        using var ink = new SKPaint { Color = new SKColor(24, 30, 42, 255), IsAntialias = true };
        c.DrawRect(24, 24, 80, 12, ink);
        c.DrawCircle(120, 62, 16, ink);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
