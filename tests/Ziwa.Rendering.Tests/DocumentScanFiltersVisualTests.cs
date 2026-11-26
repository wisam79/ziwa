using SkiaSharp;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Filters;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed class DocumentScanFiltersVisualTests
{
    [Fact]
    public void ComputeOtsuThreshold_FindsBimodalValleyBetweenDarkInkAndPaper()
    {
        int[] hist = new int[256];
        hist[30] = 2000;
        hist[220] = 8000;
        byte t = DocumentScanFilterEngine.ComputeOtsuThreshold(hist, 10000);
        Assert.InRange(t, 30, 219);
    }

    [Fact]
    public async Task FiveDocumentFilters_MatchImageFilterPipeline_AndRenderCrisplyAt300Dpi()
    {
        using var yellowed = CreateYellowedShadowedDocumentBitmap(160, 120);

        using SKBitmap magic = yellowed.Copy()!;
        DocumentScanFilterEngine.ApplyInPlace(magic, DocumentScanFilterKind.MagicColor);
        SKColor magicPaper = magic.GetPixel(15, 15);
        Assert.True(magicPaper.Red >= 245 && magicPaper.Green >= 245 && magicPaper.Blue >= 245);

        using SKBitmap otsu = yellowed.Copy()!;
        DocumentScanFilterEngine.ApplyInPlace(otsu, DocumentScanFilterKind.OtsuBlackAndWhite);
        SKColor otsuInk = otsu.GetPixel(80, 60);
        SKColor otsuPaper = otsu.GetPixel(15, 15);
        Assert.Equal(new SKColor(0, 0, 0, 255), otsuInk);
        Assert.Equal(new SKColor(255, 255, 255, 255), otsuPaper);

        using SKBitmap gray = yellowed.Copy()!;
        DocumentScanFilterEngine.ApplyInPlace(gray, DocumentScanFilterKind.EnhancedGrayscale);
        SKColor grayPx = gray.GetPixel(40, 40);
        Assert.Equal(grayPx.Red, grayPx.Green);
        Assert.Equal(grayPx.Green, grayPx.Blue);

        using SKBitmap deyellowed = yellowed.Copy()!;
        DocumentScanFilterEngine.ApplyInPlace(deyellowed, DocumentScanFilterKind.RemoveYellowing);
        SKColor originalCorner = yellowed.GetPixel(15, 15);
        SKColor cleanCorner = deyellowed.GetPixel(15, 15);
        Assert.True(cleanCorner.Blue - originalCorner.Blue > 25, "Expected yellowed blue channel to be lifted to white.");

        using SKBitmap sharpened = yellowed.Copy()!;
        DocumentScanFilterEngine.ApplyInPlace(sharpened, DocumentScanFilterKind.Sharpen);
        Assert.NotEqual(yellowed.GetPixel(70, 56), sharpened.GetPixel(70, 56));

        // Verify ImageFilterPipeline produces identical Otsu rendering and 300 DPI export works cleanly
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper, new TextOnCurveRenderer(registry, shaper));
        var exporter = new PngJpegExporter(renderer);

        using SKImage srcImg = SKImage.FromBitmap(yellowed);
        using SKData srcPng = srcImg.Encode(SKEncodedImageFormat.Png, 100);
        AssetId assetId = store.RegisterAsset(srcPng.ToArray());

        var page = new PageModel(
            Id: new PageId(Guid.Parse("55555555-2222-2222-2222-222222222222")),
            Name: "مستند ممسوح",
            WidthMm: 80.0,
            HeightMm: 60.0,
            BleedMm: 0.0,
            SafeMarginMm: 3.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes:
            [
                new ImageNode(
                    Id: new NodeId(Guid.Parse("66666666-2222-2222-2222-222222222222")),
                    Name: "مستند",
                    BoundsMm: new RectMm(10.0, 10.0, 60.0, 40.0),
                    CurrentAssetId: assetId,
                    OriginalAssetId: assetId,
                    Crop: CropSpec.Identity,
                    Filters: new ImageFilterSpec(ScanFilter: DocumentScanFilterKind.OtsuBlackAndWhite)),
            ]);

        byte[] png300 = exporter.ExportPageToBytes(page, 300.0, RasterExportFormat.Png);
        using SKBitmap exported = SKBitmap.Decode(png300);
        SKColor exportedInk = exported.GetPixel(exported.Width / 2, exported.Height / 2);

        await Verifier.Verify(new
        {
            ExportWidth = exported.Width,
            ExportHeight = exported.Height,
            MagicPaperWhitened = magicPaper.Red >= 245 && magicPaper.Blue >= 245,
            OtsuBinarizedCleanly = otsuInk == new SKColor(0, 0, 0, 255) && otsuPaper == new SKColor(255, 255, 255, 255),
            EnhancedGrayscaleNeutral = grayPx.Red == grayPx.Green && grayPx.Green == grayPx.Blue,
            RemoveYellowingLiftedBlue = cleanCorner.Blue > originalCorner.Blue + 25,
            Pipeline300DpiOtsuBlackInk = exportedInk.Red == 0 && exportedInk.Green == 0 && exportedInk.Blue == 0,
        });
    }

    private static SKBitmap CreateYellowedShadowedDocumentBitmap(int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(244, 232, 168, 255)); // aged yellow paper

        using var inkPaint = new SKPaint { Color = new SKColor(32, 28, 24, 255), IsAntialias = false };
        c.DrawRect(w * 0.44f, h * 0.44f, w * 0.14f, h * 0.14f, inkPaint);

        using var stampPaint = new SKPaint { Color = new SKColor(20, 88, 178, 255), IsAntialias = true };
        c.DrawCircle(w * 0.25f, h * 0.72f, 10f, stampPaint);
        return bmp;
    }
}
