using System.Collections.Immutable;
using SkiaSharp;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Svg;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed class Phase4SvgSecurityTests
{
    [Fact]
    public void SvgArabicTextOutliner_OutlinesArabicStickerTextIntoVectorPaths()
    {
        using var fontRegistry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(fontRegistry);
        using var store = new AssetStore(fontRegistry, shaper);

        foreach (StickerTemplateDefinition def in ParametricStickerCatalog.ExtendedAll)
        {
            var node = new StickerNode(
                NodeId.New(),
                def.TitleAr,
                new RectMm(0, 0, def.DefaultWidthMm, def.DefaultHeightMm),
                def.Id,
                def.Fields.ToImmutableDictionary(f => f.Key, f => f.DefaultValue),
                def.DefaultPalette);

            string rawXml = ParametricStickerCatalog.BuildSvgXml(node);
            string outlined = SvgArabicTextOutliner.OutlineTextElements(rawXml, fontRegistry, shaper);

            Assert.DoesNotContain("<text", outlined, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<path", outlined, StringComparison.OrdinalIgnoreCase);

            SKPicture? picture = store.GetOrBuildSvgPicture(rawXml);
            Assert.NotNull(picture);
            Assert.True(picture!.CullRect.Width > 0 && picture.CullRect.Height > 0);
        }
    }

    [Theory]
    [InlineData("fill:url(file:///external.png)")]
    [InlineData("fill:url( https://invalid.example/image.png )")]
    [InlineData("fill:url(//invalid.example/image.png)")]
    public void Sanitizer_RemovesExternalCssReferencesBeforeProjectAssetsAreParsed(string style)
    {
        string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"10\" height=\"10\" style=\"{style}\" /></svg>";
        string safe = SvgSanitizer.Sanitize(svg);
        Assert.DoesNotContain("style=", safe);
        Assert.DoesNotContain("url(", safe);
    }

    [Fact]
    public void Sanitizer_RemovesStylesheetImportsAndProcessingInstructionsButKeepsInternalPaint()
    {
        string svg = "<?xml-stylesheet href=\"file:///external.css\"?><svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import 'https://invalid.example/style.css';</style><rect fill=\"url(#local)\" width=\"10\" height=\"10\" /></svg>";
        string safe = SvgSanitizer.Sanitize(svg);
        Assert.DoesNotContain("xml-stylesheet", safe);
        Assert.DoesNotContain("@import", safe);
        Assert.Contains("url(#local)", safe);
    }
}
