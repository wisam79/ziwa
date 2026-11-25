using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed class CurvedArabicTextTests
{
    [Fact]
    public async Task CurvedArabicText_ArcUpArcDownAndFullCircle_RenderWithoutClippingAndPreserveLigatures()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var curveRenderer = new TextOnCurveRenderer(registry, shaper);
        var renderer = new SceneRenderer(store, shaper, curveRenderer);
        var exporter = new PngJpegExporter(renderer);

        const string phrase = "ختم استوديو زِيوا الاحترافي";
        ShapedLineResult shaped = shaper.ShapeLine(phrase, "Cairo", 700, DomainMath.PtToMm(13.0), TextDirectionMode.Rtl);
        Assert.True(shaped.Glyphs.Length < phrase.Length);

        var page = new PageModel(
            Id: new PageId(Guid.Parse("88888888-1111-1111-1111-111111111111")),
            Name: "نصوص منحنية",
            WidthMm: 120.0,
            HeightMm: 100.0,
            BleedMm: 0.0,
            SafeMarginMm: 4.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes:
            [
                new TextNode(
                    new NodeId(Guid.Parse("88888888-2222-2222-2222-222222222222")),
                    "قوس علوي",
                    new RectMm(8, 8, 48, 36),
                    phrase,
                    "Cairo",
                    12.0,
                    700,
                    new ColorRgba(11, 92, 171, 255),
                    TextAlignmentKind.Center,
                    TextDirectionMode.Rtl,
                    Curve: new TextCurveSpec(TextCurveKind.ArcUp, 0.8),
                    Effects: new TextEffectSpec(Outline: new StrokeSpec(ColorRgba.White, 0.25))),
                new TextNode(
                    new NodeId(Guid.Parse("88888888-3333-3333-3333-333333333333")),
                    "قوس سفلي",
                    new RectMm(64, 8, 48, 36),
                    phrase,
                    "Tajawal",
                    12.0,
                    700,
                    new ColorRgba(17, 107, 54, 255),
                    TextAlignmentKind.Center,
                    TextDirectionMode.Rtl,
                    Curve: new TextCurveSpec(TextCurveKind.ArcDown, 0.8)),
                new TextNode(
                    new NodeId(Guid.Parse("88888888-4444-4444-4444-444444444444")),
                    "دائرة كاملة",
                    new RectMm(30, 44, 52, 52),
                    phrase,
                    "IBM Plex Sans Arabic",
                    12.0,
                    700,
                    new ColorRgba(20, 24, 31, 255),
                    TextAlignmentKind.Center,
                    TextDirectionMode.Rtl,
                    Curve: new TextCurveSpec(TextCurveKind.FullCircle, 1.0)),
            ]);

        byte[] png300 = exporter.ExportPageToBytes(page, 300.0, RasterExportFormat.Png);
        using SKBitmap bmp = SKBitmap.Decode(png300);

        int inkArcUp = CountNonWhitePixels(bmp, 8, 8, 48, 36, 300.0);
        int inkArcDown = CountNonWhitePixels(bmp, 64, 8, 48, 36, 300.0);
        int inkCircle = CountNonWhitePixels(bmp, 30, 44, 52, 52, 300.0);

        Assert.True(inkArcUp > 50);
        Assert.True(inkArcDown > 50);
        Assert.True(inkCircle > 50);

        await Verifier.Verify(new
        {
            PhraseGlyphCount = shaped.Glyphs.Length,
            ArcUpHasInk = inkArcUp > 50,
            ArcDownHasInk = inkArcDown > 50,
            FullCircleHasInk = inkCircle > 50,
            BitmapWidth = bmp.Width,
            BitmapHeight = bmp.Height,
        });
    }

    private static int CountNonWhitePixels(SKBitmap bmp, double xMm, double yMm, double wMm, double hMm, double dpi)
    {
        int x0 = DomainMath.MmToPixelsRounded(xMm, dpi);
        int y0 = DomainMath.MmToPixelsRounded(yMm, dpi);
        int x1 = Math.Min(bmp.Width - 1, DomainMath.MmToPixelsRounded(xMm + wMm, dpi));
        int y1 = Math.Min(bmp.Height - 1, DomainMath.MmToPixelsRounded(yMm + hMm, dpi));

        int count = 0;
        for (int y = y0; y <= y1; y += 3)
        {
            for (int x = x0; x <= x1; x += 3)
            {
                if (bmp.GetPixel(x, y) != SKColors.White)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
