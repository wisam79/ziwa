using ImageMagick;
using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed class VerticalSliceParityAndDpiTests
{
    [Fact]
    public void PreviewAndExport300Dpi_ProduceExactSamePixelBuffer()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper);
        var exporter = new PngJpegExporter(renderer);

        AssetId emblemId = store.RegisterAsset(CreateTestEmblemPng(128));
        PageModel page = CreateDeterministicPage(emblemId);

        const double dpi = 300.0;
        int widthPx = DomainMath.MmToPixelsRounded(page.WidthMm, dpi);
        int heightPx = DomainMath.MmToPixelsRounded(page.HeightMm, dpi);

        // 1. Render via Exporter path (300 DPI PNG) and decode into the exact same Rgba8888 Premul layout
        byte[] exportedPng = exporter.ExportPageToBytes(page, dpi, RasterExportFormat.Png);
        using SKBitmap exportedBitmap = SKBitmap.Decode(
            exportedPng,
            new SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, SKAlphaType.Premul));

        // 2. Render via Pre-scaled Viewport path (simulating SkiaViewportHost scale matrix at 300 DPI)
        using var viewportBitmap = new SKBitmap(new SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(viewportBitmap))
        {
            canvas.Clear(SKColors.Transparent);
            float scale = (float)DomainMath.PixelsPerMm(dpi);
            canvas.Scale(scale, scale);
            renderer.RenderPage(
                canvas,
                page,
                new RenderOptions(
                    Dpi: dpi,
                    QualityMode: RenderQualityMode.PrintFullResolution,
                    IsPreScaledToMillimeters: true));
        }

        Assert.Equal(viewportBitmap.Width, exportedBitmap.Width);
        Assert.Equal(viewportBitmap.Height, exportedBitmap.Height);

        ReadOnlySpan<byte> expectedBytes = viewportBitmap.GetPixelSpan();
        ReadOnlySpan<byte> actualBytes = exportedBitmap.GetPixelSpan();
        Assert.True(expectedBytes.SequenceEqual(actualBytes), "Viewport pre-scaled render and 300 DPI export must be pixel-identical.");
    }

    [Theory]
    [InlineData(300.0, 11811u)]
    [InlineData(600.0, 23622u)]
    public void PngAndJpegExports_EmbedValidPhysicalDpiHeadersAndPixelDimensions(double dpi, uint expectedPpm)
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper);
        var exporter = new PngJpegExporter(renderer);

        AssetId emblemId = store.RegisterAsset(CreateTestEmblemPng(128));
        PageModel page = CreateDeterministicPage(emblemId);

        int expectedW = DomainMath.MmToPixelsRounded(page.WidthMm, dpi);
        int expectedH = DomainMath.MmToPixelsRounded(page.HeightMm, dpi);

        byte[] pngBytes = exporter.ExportPageToBytes(page, dpi, RasterExportFormat.Png);
        Assert.True(RasterDpiMetadataInjector.TryReadPngDpi(pngBytes, out double pngDpiX, out double pngDpiY));
        Assert.Equal(dpi, pngDpiX, precision: 1);
        Assert.Equal(dpi, pngDpiY, precision: 1);
        Assert.Equal(expectedPpm, DomainMath.DpiToPixelsPerMeter(dpi));

        using (var magickPng = new MagickImage(pngBytes))
        {
            Assert.Equal((uint)expectedW, magickPng.Width);
            Assert.Equal((uint)expectedH, magickPng.Height);
            Assert.Equal(DensityUnit.PixelsPerCentimeter, magickPng.Density.Units);
            double cmToInch = 2.54;
            Assert.Equal(dpi, magickPng.Density.X * cmToInch, precision: 1);
        }

        byte[] jpgBytes = exporter.ExportPageToBytes(page, dpi, RasterExportFormat.Jpeg, jpegQuality: 95);
        Assert.True(RasterDpiMetadataInjector.TryReadJpegDpi(jpgBytes, out double jpgDpiX, out double jpgDpiY));
        Assert.Equal(dpi, jpgDpiX, precision: 1);
        Assert.Equal(dpi, jpgDpiY, precision: 1);

        using (var magickJpg = new MagickImage(jpgBytes))
        {
            Assert.Equal((uint)expectedW, magickJpg.Width);
            Assert.Equal((uint)expectedH, magickJpg.Height);
            Assert.Equal(DensityUnit.PixelsPerInch, magickJpg.Density.Units);
            Assert.Equal(dpi, magickJpg.Density.X, precision: 1);
            Assert.Equal(dpi, magickJpg.Density.Y, precision: 1);
        }
    }

    [Fact]
    public async Task VerticalSlice_ExportMetadataAndColorPresence_MatchesVerifiedBaseline()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper);
        var exporter = new PngJpegExporter(renderer);

        AssetId emblemId = store.RegisterAsset(CreateTestEmblemPng(128));
        PageModel page = CreateDeterministicPage(emblemId);

        byte[] png300 = exporter.ExportPageToBytes(page, 300.0, RasterExportFormat.Png);
        using SKBitmap bitmap = SKBitmap.Decode(png300);

        int nonBackgroundPixels = 0;
        SKColor bg = new(250, 251, 253, 255);
        for (int y = 0; y < bitmap.Height; y += 8)
        {
            for (int x = 0; x < bitmap.Width; x += 8)
            {
                if (bitmap.GetPixel(x, y) != bg)
                {
                    nonBackgroundPixels++;
                }
            }
        }

        Assert.True(nonBackgroundPixels > 200);

        RasterDpiMetadataInjector.TryReadPngDpi(png300, out double dpiX, out double dpiY);
        var summary = new
        {
            PageWidthMm = page.WidthMm,
            PageHeightMm = page.HeightMm,
            ExportDpi = 300.0,
            PixelWidth = bitmap.Width,
            PixelHeight = bitmap.Height,
            PngPhysDpiX = Math.Round(dpiX, 2),
            PngPhysDpiY = Math.Round(dpiY, 2),
            HasRenderedContent = nonBackgroundPixels > 200,
            HeaderSampleColor = bitmap.GetPixel(bitmap.Width / 2, DomainMath.MmToPixelsRounded(20.0, 300.0)).ToString(),
        };

        await Verifier.Verify(summary);
    }

    private static PageModel CreateDeterministicPage(AssetId emblemId)
    {
        return new PageModel(
            Id: new PageId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            Name: "ورقة التطابق",
            WidthMm: 90.0,
            HeightMm: 55.0,
            BleedMm: 0.0,
            SafeMarginMm: 4.0,
            Background: new SolidBackground(new ColorRgba(250, 251, 253, 255)),
            Nodes:
            [
                new ShapeNode(
                    Id: new NodeId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                    Name: "شريط",
                    BoundsMm: new RectMm(4.0, 4.0, 82.0, 18.0),
                    Kind: ShapeKind.Rectangle,
                    Fill: new SolidShapeFill(new ColorRgba(11, 92, 171, 255)),
                    Radii: CornerRadiiMm.Uniform(2.5)),
                new ImageNode(
                    Id: new NodeId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                    Name: "شعار",
                    BoundsMm: new RectMm(6.0, 6.0, 14.0, 14.0),
                    CurrentAssetId: emblemId,
                    OriginalAssetId: emblemId,
                    Crop: CropSpec.Identity,
                    CornerRadiusMm: 2.0),
                new TextNode(
                    Id: new NodeId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
                    Name: "عنوان",
                    BoundsMm: new RectMm(22.0, 8.0, 60.0, 12.0),
                    Text: "زِيوا 300 DPI",
                    FontFamily: "Cairo",
                    FontSizePt: 12.0,
                    FontWeight: 700,
                    Color: ColorRgba.White,
                    Alignment: TextAlignmentKind.Right,
                    Direction: TextDirectionMode.Rtl),
            ]);
    }

    private static byte[] CreateTestEmblemPng(int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor(15, 118, 110, 255));
        using var paint = new SKPaint { Color = new SKColor(250, 204, 21, 255), IsAntialias = true };
        surface.Canvas.DrawCircle(size * 0.5f, size * 0.5f, size * 0.3f, paint);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
