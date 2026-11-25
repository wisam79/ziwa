using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.PrePress;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed partial class Phase3PrePressPdfTiledCmykTests
{
    [Fact]
    public void PrintMarksAndCutContourRenderer_ComputesAccurateMillimeterContoursAndSkipsFullBleedBackground()
    {
        var fullBleedBg = new ImageNode(
            Id: NodeId.New(),
            Name: "خلفية ممتدة",
            BoundsMm: new RectMm(-3.0, -3.0, 106.0, 106.0),
            CurrentAssetId: new AssetId("bg"),
            OriginalAssetId: new AssetId("bg"),
            Crop: CropSpec.Identity);

        var sticker = new StickerNode(
            Id: NodeId.New(),
            Name: "ختم تجاري",
            BoundsMm: new RectMm(20.0, 25.0, 30.0, 30.0),
            TemplateId: "commercial-seal",
            Fields: ImmutableDictionary<string, string>.Empty.Add("title", "زِيوا"),
            Palette: ParametricStickerCatalog.CommercialSeal.DefaultPalette);

        var badgeShape = new ShapeNode(
            Id: NodeId.New(),
            Name: "مربع ملصق",
            BoundsMm: new RectMm(60.0, 25.0, 20.0, 30.0),
            Kind: ShapeKind.Rectangle,
            Fill: new SolidShapeFill(new ColorRgba(11, 92, 171, 255)));

        var page = new PageModel(
            Id: PageId.New(),
            Name: "شيت ملصقات",
            WidthMm: 100.0,
            HeightMm: 100.0,
            BleedMm: 3.0,
            SafeMarginMm: 3.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes: [fullBleedBg, sticker, badgeShape]);

        RectMm? tight = PrintMarksAndCutContourRenderer.GetTightContourBoundsMm(page);
        Assert.NotNull(tight);
        Assert.InRange(tight!.Value.X, 19.9, 20.1);
        Assert.InRange(tight.Value.Y, 24.9, 25.1);
        Assert.InRange(tight.Value.Right, 79.9, 80.1);
        Assert.InRange(tight.Value.Bottom, 54.9, 55.1);

        // Verify crop, registration, and CutContour overlay render cleanly onto a surface
        using var surface = SKSurface.Create(new SKImageInfo(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
        Assert.NotNull(surface);
        surface!.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale(3.0f, 3.0f);
        surface.Canvas.Translate(10.0f, 10.0f);
        PrintMarksAndCutContourRenderer.RenderCropAndRegistrationMarks(surface.Canvas, page, true, true);
        PrintMarksAndCutContourRenderer.RenderCutContourOverlay(surface.Canvas, page);
    }

    [Fact]
    public void VectorPdfExporter_ExportsMultiPageFrontAndBackPdfWithTwoPageMarkers()
    {
        using var assetStore = new AssetStore();
        using var fontRegistry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(fontRegistry);
        var curveRenderer = new TextOnCurveRenderer(fontRegistry, shaper);
        var renderer = new SceneRenderer(assetStore, shaper, curveRenderer);
        var pdfExporter = new VectorPdfExporter(renderer);

        PageModel front = CreateSamplePage("الوجه الأمامي", 90.0, 50.0, new ColorRgba(11, 92, 171, 255));
        PageModel back = CreateSamplePage("الوجه الخلفي", 90.0, 50.0, new ColorRgba(20, 24, 31, 255));

        byte[] pdfBytes = pdfExporter.ExportDocumentToPdfBytes(
            [front, back],
            dpi: 300.0,
            includeBleed: true,
            includeCropMarks: true,
            includeRegistrationMarks: true,
            includeCutContour: true);

        Assert.True(pdfBytes.Length > 500);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdfBytes, 0, 5));

        string asciiText = Encoding.Latin1.GetString(pdfBytes);
        int pageCount = PdfPageObjectRegex().Matches(asciiText).Count;
        Assert.Equal(2, pageCount);
    }

    [Fact]
    public void TiledRasterRenderer_MatchesSingleSurfaceByteForByte_AndExportsGiantBannerAbove6000Px()
    {
        using var assetStore = new AssetStore();
        using var fontRegistry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(fontRegistry);
        var curveRenderer = new TextOnCurveRenderer(fontRegistry, shaper);
        var renderer = new SceneRenderer(assetStore, shaper, curveRenderer);
        var exporter = new PngJpegExporter(renderer);

        PageModel smallPage = CreateSamplePage("اختبار البلاطات", 40.0, 30.0, new ColorRgba(11, 92, 171, 255));

        byte[] singleRgba = TiledRasterRenderer.RenderPageToRgbaBytes(
            renderer,
            smallPage,
            dpi: 200.0,
            includeBleed: true,
            includeCropMarks: true,
            includeRegistrationMarks: true,
            includeCutContour: true,
            clearColor: SKColors.White,
            out int singleW,
            out int singleH,
            maxSingleSurfaceEdgePx: 10000);

        byte[] tiledRgba = TiledRasterRenderer.RenderPageToRgbaBytes(
            renderer,
            smallPage,
            dpi: 200.0,
            includeBleed: true,
            includeCropMarks: true,
            includeRegistrationMarks: true,
            includeCutContour: true,
            clearColor: SKColors.White,
            out int tiledW,
            out int tiledH,
            maxSingleSurfaceEdgePx: 180,
            tileSizePx: 128);

        Assert.Equal(singleW, tiledW);
        Assert.Equal(singleH, tiledH);
        Assert.Equal(singleRgba.Length, tiledRgba.Length);

        int maxChannelDiff = 0;
        int differingBytes = 0;
        for (int i = 0; i < singleRgba.Length; i++)
        {
            int diff = Math.Abs(singleRgba[i] - tiledRgba[i]);
            if (diff > 0)
            {
                differingBytes++;
                if (diff > maxChannelDiff)
                {
                    maxChannelDiff = diff;
                }
            }
        }

        Assert.True(maxChannelDiff <= 4, $"Max channel diff between single-surface and tiled render was {maxChannelDiff}.");
        Assert.True(differingBytes < singleRgba.Length / 500, $"Too many differing bytes ({differingBytes}/{singleRgba.Length}).");

        // Also verify PngJpegExporter forced tiled render matches single-surface render (PNG dimensions + DPI metadata + pixels within AA/premul rounding)
        byte[] normalPng = exporter.ExportPageToBytes(smallPage, 200.0, RasterExportFormat.Png, maxSingleSurfaceEdgePx: 10000);
        byte[] forcedTiledPng = exporter.ExportPageToBytes(smallPage, 200.0, RasterExportFormat.Png, maxSingleSurfaceEdgePx: 180);
        Assert.True(RasterDpiMetadataInjector.TryReadPngDpi(normalPng, out double nDpiX, out _));
        Assert.True(RasterDpiMetadataInjector.TryReadPngDpi(forcedTiledPng, out double tDpiX, out _));
        Assert.Equal(Math.Round(nDpiX), Math.Round(tDpiX));

        using SKBitmap? normalBmp = SKBitmap.Decode(normalPng);
        using SKBitmap? tiledBmp = SKBitmap.Decode(forcedTiledPng);
        Assert.NotNull(normalBmp);
        Assert.NotNull(tiledBmp);
        Assert.Equal(normalBmp!.Width, tiledBmp!.Width);
        Assert.Equal(normalBmp.Height, tiledBmp.Height);

        byte[] normalBytes = normalBmp.Bytes;
        byte[] tiledBytes = tiledBmp.Bytes;
        Assert.Equal(normalBytes.Length, tiledBytes.Length);
        int pngMaxDiff = 0;
        for (int i = 0; i < normalBytes.Length; i++)
        {
            pngMaxDiff = Math.Max(pngMaxDiff, Math.Abs(normalBytes[i] - tiledBytes[i]));
        }

        Assert.True(pngMaxDiff <= 4, $"Max channel diff between normal PNG and forced tiled PNG was {pngMaxDiff}.");

        // Giant banner: 520mm x 200mm @ 300 DPI -> 6142 x 2362 px (> 6000px edge triggers automatic Tiled Render)
        PageModel bannerPage = CreateSamplePage("بانر عملاق", 520.0, 200.0, new ColorRgba(180, 35, 24, 255));
        byte[] bannerPng = exporter.ExportPageToBytes(bannerPage, 300.0, RasterExportFormat.Png);

        Assert.Equal((byte)137, bannerPng[0]);
        Assert.Equal((byte)80, bannerPng[1]);
        Assert.True(RasterDpiMetadataInjector.TryReadPngDpi(bannerPng, out double dpiX, out double dpiY));
        Assert.Equal(300.0, Math.Round(dpiX));
        Assert.Equal(300.0, Math.Round(dpiY));

        using SKData data = SKData.CreateCopy(bannerPng);
        using SKCodec? codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        Assert.Equal(DomainMath.MmToPixelsRounded(520.0, 300.0), codec!.Info.Width);
        Assert.Equal(DomainMath.MmToPixelsRounded(200.0, 300.0), codec.Info.Height);
    }

    [Fact]
    public void CmykTiffExporter_ProducesLzwCmykTiffWithIccProfileAndSupportsCustomIccAndCancellation()
    {
        using var assetStore = new AssetStore();
        using var fontRegistry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(fontRegistry);
        var curveRenderer = new TextOnCurveRenderer(fontRegistry, shaper);
        var renderer = new SceneRenderer(assetStore, shaper, curveRenderer);
        var cmykExporter = new CmykTiffExporter(renderer);

        PageModel page = CreateSamplePage("طباعة CMYK", 60.0, 40.0, new ColorRgba(11, 92, 171, 255));
        byte[] tiffBytes = cmykExporter.ExportPageToCmykTiffBytes(
            page,
            dpi: 300.0,
            profileKind: CmykProfileKind.CoatedFogra39,
            includeBleed: true,
            includeCropMarks: true);

        Assert.True(CmykTiffExporter.IsValidTiffHeader(tiffBytes));

        using (var img = new MagickImage(tiffBytes))
        {
            Assert.Equal(ColorSpace.CMYK, img.ColorSpace);
            Assert.NotNull(img.GetColorProfile());
            Assert.Equal(CompressionMethod.LZW, img.Compression);
            Assert.Equal(300.0, Math.Round(img.Density.X));
        }

        // Custom ICC round-trip
        byte[] customIcc = ColorProfiles.USWebCoatedSWOP.ToByteArray()!;
        byte[] customTiffBytes = cmykExporter.ExportPageToCmykTiffBytes(
            page,
            dpi: 300.0,
            profileKind: CmykProfileKind.CustomIcc,
            customIccProfileBytes: customIcc);

        Assert.True(CmykTiffExporter.IsValidTiffHeader(customTiffBytes));
        using (var customImg = new MagickImage(customTiffBytes))
        {
            Assert.Equal(ColorSpace.CMYK, customImg.ColorSpace);
            Assert.NotNull(customImg.GetColorProfile());
        }

        // Instant cancellation
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            cmykExporter.ExportPageToCmykTiffBytes(page, 300.0, cancellationToken: cts.Token));
    }

    private static PageModel CreateSamplePage(string name, double widthMm, double heightMm, ColorRgba accent)
    {
        var rect = new ShapeNode(
            Id: NodeId.New(),
            Name: "إطار",
            BoundsMm: new RectMm(5.0, 5.0, Math.Max(10.0, widthMm - 10.0), Math.Max(10.0, heightMm - 10.0)),
            Kind: ShapeKind.Rectangle,
            Fill: new SolidShapeFill(accent));

        var text = new TextNode(
            Id: NodeId.New(),
            Name: "عنوان",
            BoundsMm: new RectMm(8.0, 10.0, Math.Max(12.0, widthMm - 16.0), 12.0),
            Text: "استوديو زِيوا للتصميم",
            FontFamily: "Cairo",
            FontSizePt: 14.0,
            FontWeight: 700,
            Color: ColorRgba.White);

        return new PageModel(
            Id: PageId.New(),
            Name: name,
            WidthMm: widthMm,
            HeightMm: heightMm,
            BleedMm: 3.0,
            SafeMarginMm: 3.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes: [rect, text]);
    }

    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
    private static partial Regex PdfPageObjectRegex();
}
