using System.Collections.Immutable;
using SkiaSharp;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed class FiltersAndStickersVisualTests
{
    [Fact]
    public void ImageFilterPipeline_ThresholdAndBwPreset_MatchNumericallyAcrossPreviewAnd300Dpi()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper, new TextOnCurveRenderer(registry, shaper));
        var exporter = new PngJpegExporter(renderer);

        AssetId gradientId = store.RegisterAsset(CreateGradientPhotoPng(200, 120));

        // 1) LuminanceThreshold must output strictly binary pure black/white RGB pixels at both 96 DPI & 300 DPI
        var thresholdPage = new PageModel(
            PageId.New(),
            "عتبة ثنائية",
            60.0,
            40.0,
            0.0,
            2.0,
            new SolidBackground(ColorRgba.White),
            [
                new ImageNode(
                    NodeId.New(),
                    "صورة عتبة",
                    new RectMm(5, 5, 50, 30),
                    gradientId,
                    gradientId,
                    CropSpec.Identity,
                    Filters: new ImageFilterSpec(LuminanceThreshold: 0.5)),
            ]);

        byte[] export300 = exporter.ExportPageToBytes(thresholdPage, 300.0, RasterExportFormat.Png);
        using SKBitmap exportBmp = SKBitmap.Decode(export300);

        byte[] preview96 = exporter.ExportPageToBytes(thresholdPage, 96.0, RasterExportFormat.Png);
        using SKBitmap previewBmp = SKBitmap.Decode(preview96);

        AssertStrictlyBlackAndWhiteInsideRect(previewBmp, 8, 8, 44, 24, 96.0);
        AssertStrictlyBlackAndWhiteInsideRect(exportBmp, 8, 8, 44, 24, 300.0);

        // 2) ClassicBw preset + PixelateBlockSize produces neutral grayscale mosaic
        var bwPixelPage = thresholdPage with
        {
            Nodes =
            [
                new ImageNode(
                    NodeId.New(),
                    "أبيض وأسود منقّط",
                    new RectMm(5, 5, 50, 30),
                    gradientId,
                    gradientId,
                    CropSpec.Identity,
                    Filters: new ImageFilterSpec(Contrast: 0.15, PixelateBlockSize: 8, Preset: PhotoFilterPreset.ClassicBw)),
            ],
        };

        byte[] bwBytes = exporter.ExportPageToBytes(bwPixelPage, 300.0, RasterExportFormat.Png);
        using SKBitmap bwBmp = SKBitmap.Decode(bwBytes);
        SKColor sample = bwBmp.GetPixel(bwBmp.Width / 2, bwBmp.Height / 2);
        Assert.True(Math.Abs(sample.Red - sample.Green) <= 2 && Math.Abs(sample.Green - sample.Blue) <= 2,
            $"ClassicBw should yield neutral grayscale, got {sample}.");
    }

    [Fact]
    public async Task ParametricStickersAndCollageSheet_RenderCrisplyAt300Dpi()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper, new TextOnCurveRenderer(registry, shaper));
        var exporter = new PngJpegExporter(renderer);

        AssetId photoId = store.RegisterAsset(CreateGradientPhotoPng(180, 240));
        CollageLayoutModel collage = PhysicalGridBuilder.BuildOrReflowLayout(101.6, 152.4, CollagePresets.IdOfficial40x60);
        collage = collage with
        {
            AutoRepeatMode = true,
            Slots = collage.Slots.Select(s => s with
            {
                AssignedAssetId = photoId,
                OriginalAssetId = photoId,
                QuarterTurns = s.Index % 2,
                Filters = new ImageFilterSpec(Preset: PhotoFilterPreset.WarmStudio),
            }).ToImmutableArray(),
        };

        var stickerNodes = new List<SceneNode>();
        double[] ys = [6.0, 62.0];
        for (int i = 0; i < ParametricStickerCatalog.All.Length; i++)
        {
            StickerTemplateDefinition def = ParametricStickerCatalog.All[i];
            stickerNodes.Add(new StickerNode(
                new NodeId(Guid.Parse($"77777777-0000-0000-0000-00000000000{i}")),
                def.TitleAr,
                new RectMm(4.0 + ((i % 2) * 48.0), ys[i / 2], 42.0, 32.0),
                def.Id,
                def.Fields.ToImmutableDictionary(f => f.Key, f => f.DefaultValue),
                def.DefaultPalette));
        }

        var page = new PageModel(
            new PageId(Guid.Parse("77777777-1111-1111-1111-111111111111")),
            "كولاج وملصقات",
            101.6,
            152.4,
            0.0,
            3.0,
            new SolidBackground(ColorRgba.White),
            [.. stickerNodes],
            collage);

        byte[] png300 = exporter.ExportPageToBytes(page, 300.0, RasterExportFormat.Png);
        using SKBitmap bmp = SKBitmap.Decode(png300);

        await Verifier.Verify(new
        {
            StickerTemplateCount = ParametricStickerCatalog.All.Length,
            CollageSlotCount = collage.Slots.Length,
            ExportWidthPx = bmp.Width,
            ExportHeightPx = bmp.Height,
            SvgPicturesCached = store.SvgAssetCount,
        });
    }

    private static void AssertStrictlyBlackAndWhiteInsideRect(SKBitmap bmp, double xMm, double yMm, double wMm, double hMm, double dpi)
    {
        int x0 = (int)Math.Round(xMm / 25.4 * dpi);
        int y0 = (int)Math.Round(yMm / 25.4 * dpi);
        int x1 = Math.Min(bmp.Width - 1, (int)Math.Round((xMm + wMm) / 25.4 * dpi));
        int y1 = Math.Min(bmp.Height - 1, (int)Math.Round((yMm + hMm) / 25.4 * dpi));

        bool sawBlack = false;
        bool sawWhite = false;
        for (int y = y0; y <= y1; y += 2)
        {
            for (int x = x0; x <= x1; x += 2)
            {
                SKColor c = bmp.GetPixel(x, y);
                bool isBlack = c.Red == 0 && c.Green == 0 && c.Blue == 0;
                bool isWhite = c.Red == 255 && c.Green == 255 && c.Blue == 255;
                Assert.True(isBlack || isWhite, $"Threshold pixel must be 0 or 255, got {c}.");
                sawBlack |= isBlack;
                sawWhite |= isWhite;
            }
        }

        Assert.True(sawBlack && sawWhite, "Gradient image threshold must produce both black and white regions.");
    }

    private static byte[] CreateGradientPhotoPng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(w, h),
            [new SKColor(20, 30, 60, 255), new SKColor(245, 225, 190, 255)],
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        surface.Canvas.DrawRect(0, 0, w, h, paint);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
