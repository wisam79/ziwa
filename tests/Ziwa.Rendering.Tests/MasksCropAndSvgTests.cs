using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Svg;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed class MasksCropAndSvgTests
{
    [Fact]
    public async Task AllEightMasks_WithCropAndOuterShadow_RenderDeterministicallyAt300And600Dpi()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper, new TextOnCurveRenderer(registry, shaper));
        var exporter = new PngJpegExporter(renderer);

        AssetId photoId = store.RegisterAsset(CreateColorfulTestImagePng(240, 180));
        MaskShapeKind[] allMasks = Enum.GetValues<MaskShapeKind>();
        Assert.Equal(8, allMasks.Length);

        var nodes = new List<SceneNode>();
        for (int i = 0; i < allMasks.Length; i++)
        {
            int col = i % 4;
            int row = i / 4;
            nodes.Add(new ImageNode(
                Id: new NodeId(Guid.Parse($"10000000-0000-0000-0000-00000000000{i}")),
                Name: allMasks[i].ToString(),
                BoundsMm: new RectMm(8.0 + (col * 28.0), 8.0 + (row * 28.0), 22.0, 22.0),
                CurrentAssetId: photoId,
                OriginalAssetId: photoId,
                Crop: new CropSpec(Zoom: 1.6, PanX: 0.2, PanY: -0.1),
                Mask: allMasks[i],
                CornerRadiusMm: 2.0,
                Border: new StrokeSpec(new ColorRgba(11, 92, 171, 255), 0.5),
                Shadow: new ShadowSpec(new ColorRgba(0, 0, 0, 160), 2.0, 2.0, 1.5)));
        }

        // Verify outer shadow outside Circle mask (index 1) is NOT clipped by the mask
        var page = new PageModel(
            Id: new PageId(Guid.Parse("99999999-1111-1111-1111-111111111111")),
            Name: "أقنعة",
            WidthMm: 120.0,
            HeightMm: 64.0,
            BleedMm: 0.0,
            SafeMarginMm: 3.0,
            Background: new SolidBackground(ColorRgba.White),
            Nodes: [.. nodes]);

        byte[] png300 = exporter.ExportPageToBytes(page, 300.0, RasterExportFormat.Png);
        byte[] png600 = exporter.ExportPageToBytes(page, 600.0, RasterExportFormat.Png);

        using SKBitmap bmp300 = SKBitmap.Decode(png300);
        using SKBitmap bmp600 = SKBitmap.Decode(png600);

        Assert.Equal(DomainMath.MmToPixelsRounded(120.0, 300.0), bmp300.Width);
        Assert.Equal(DomainMath.MmToPixelsRounded(120.0, 600.0), bmp600.Width);

        // Circle mask bounds at index 1: X=[36..58], Y=[8..30]. Corner (57.5, 29.5) is OUTSIDE circle but gets shadow offset (+2, +2)
        int cornerShadowX = DomainMath.MmToPixelsRounded(57.5, 300.0);
        int cornerShadowY = DomainMath.MmToPixelsRounded(29.5, 300.0);
        SKColor cornerShadowColor = bmp300.GetPixel(cornerShadowX, cornerShadowY);
        Assert.True(cornerShadowColor.Red < 250, $"Expected unclipped outer shadow outside Circle mask, got {cornerShadowColor}.");

        await Verifier.Verify(new
        {
            MaskCount = allMasks.Length,
            Width300 = bmp300.Width,
            Height300 = bmp300.Height,
            Width600 = bmp600.Width,
            Height600 = bmp600.Height,
            HasUnclippedOuterShadowOutsideCircle = cornerShadowColor.Red < 250,
        });
    }

    [Fact]
    public void SvgSanitizer_StripsDangerousElementsAndAttributes_AndRejectsXxe()
    {
        const string maliciousSvg = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100" onload="alert(1)">
              <script>fetch('https://evil.example')</script>
              <foreignObject width="50" height="50"><div>html</div></foreignObject>
              <image xlink:href="https://evil.example/pixel.png" width="10" height="10" />
              <rect x="10" y="10" width="80" height="80" fill="#0B5CAB" onclick="steal()" style="fill:url(https://evil.example/x)" />
            </svg>
            """;

        string clean = SvgSanitizer.Sanitize(maliciousSvg);
        Assert.DoesNotContain("script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreignObject", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://evil.example", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<rect", clean, StringComparison.OrdinalIgnoreCase);

        const string xxeSvg = """
            <!DOCTYPE svg [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <svg xmlns="http://www.w3.org/2000/svg"><text>&xxe;</text></svg>
            """;
        Assert.Throws<InvalidDataException>(() => SvgSanitizer.Sanitize(xxeSvg));
    }

    [Fact]
    public void SvgNodeTint_And_ShapeImageFill_RenderCrisplyAt600Dpi()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper, new TextOnCurveRenderer(registry, shaper));
        var exporter = new PngJpegExporter(renderer);

        AssetId svgId = store.RegisterSvgAsset("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
              <circle cx="50" cy="50" r="45" fill="#0B5CAB" />
            </svg>
            """);
        AssetId photoId = store.RegisterAsset(CreateColorfulTestImagePng(128, 128));

        var page = new PageModel(
            PageId.New(),
            "متجهات",
            80,
            40,
            0,
            2,
            new SolidBackground(ColorRgba.White),
            [
                new SvgNode(NodeId.New(), "شعار ملون", new RectMm(5, 5, 30, 30), svgId, TintColor: new ColorRgba(180, 35, 24, 255)),
                new ShapeNode(NodeId.New(), "نجمة معبأة", new RectMm(45, 5, 30, 30), ShapeKind.Star, new ImageShapeFill(photoId, photoId, CropSpec.Identity)),
            ]);

        byte[] png600 = exporter.ExportPageToBytes(page, 600.0, RasterExportFormat.Png);
        using SKBitmap bmp = SKBitmap.Decode(png600);

        SKColor centerSvg = bmp.GetPixel(DomainMath.MmToPixelsRounded(20, 600.0), DomainMath.MmToPixelsRounded(20, 600.0));
        Assert.True(centerSvg.Red > 150 && centerSvg.Blue < 50, $"Expected tinted red SVG pixel, got {centerSvg}.");

        SKColor centerStar = bmp.GetPixel(DomainMath.MmToPixelsRounded(60, 600.0), DomainMath.MmToPixelsRounded(20, 600.0));
        Assert.NotEqual(SKColors.White, centerStar);
    }

    private static byte[] CreateColorfulTestImagePng(int w, int h)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(new SKColor(15, 118, 110, 255));
        using var p = new SKPaint { Color = new SKColor(245, 158, 11, 255) };
        surface.Canvas.DrawRect(0, 0, w * 0.5f, h * 0.5f, p);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
