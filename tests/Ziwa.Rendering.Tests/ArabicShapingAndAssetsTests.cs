using ImageMagick;
using SkiaSharp;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Typography;
using Ziwa.Rendering.Viewport;

namespace Ziwa.Rendering.Tests;

public sealed class ArabicShapingAndAssetsTests
{
    [Fact]
    public void FontRegistry_LoadsAllThreeOfflineArabicFamilies()
    {
        using var registry = new FontRegistry();
        foreach (string family in registry.RegisteredFamilies)
        {
            FontRegistry.FontEntry regular = registry.ResolveFont(family, 400);
            FontRegistry.FontEntry bold = registry.ResolveFont(family, 700);
            Assert.NotNull(regular.Typeface);
            Assert.NotNull(bold.Typeface);
            Assert.True(regular.UnitsPerEm > 0);
        }
    }

    [Fact]
    public void HarfBuzzShaper_ShapesLamAlefLigatureAndTashkeelAndMixedBiDi()
    {
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);

        // "لا" ligature fuses 2 codepoints into 1 glyph (plus the rest of the phrase)
        string ligaturePhrase = "الإعلام";
        ShapedLineResult shapedLigature = shaper.ShapeLine(ligaturePhrase, "Tajawal", 400, 6.0, TextDirectionMode.Rtl);
        Assert.True(shapedLigature.Glyphs.Length < ligaturePhrase.Length);
        Assert.True(shapedLigature.TotalAdvanceMm > 0f);

        // Vocalized Arabic with Tashkeel produces zero-advance combining mark glyphs
        string vocalized = "زِيوا";
        ShapedLineResult shapedVocalized = shaper.ShapeLine(vocalized, "IBM Plex Sans Arabic", 700, 6.0, TextDirectionMode.Rtl);
        Assert.Contains(shapedVocalized.Glyphs, g => g.AdvanceMm == 0f);

        // Mixed BiDi line places LTR token visually on the left and Arabic RTL run on the right
        List<HarfBuzzArabicShaper.TextRun> runs = HarfBuzzArabicShaper.SplitAndOrderVisualRuns(
            "الطباعة بدقة 300 DPI جاهزة",
            baseIsRtl: true);
        Assert.Equal(3, runs.Count);
        Assert.True(runs[0].IsRtl);
        Assert.False(runs[1].IsRtl);
        Assert.Contains("300 DPI", runs[1].Text, StringComparison.Ordinal);
        Assert.True(runs[2].IsRtl);
    }

    [Fact]
    public void AssetStore_NormalizesExifOrientation_AndCapsPreviewTo2048Px()
    {
        using var store = new AssetStore();

        // Create a 320x160 JPEG with EXIF RightTop (90° CW -> logical upright 160x320)
        byte[] exifJpegBytes;
        using (var magick = new MagickImage(MagickColors.RoyalBlue, 320, 160))
        {
            magick.Format = MagickFormat.Jpeg;
            magick.Orientation = Orientation.RightTop;
            var profile = new ExifProfile();
            profile.SetValue(ExifTag.Orientation, (ushort)6);
            magick.SetProfile(profile);
            exifJpegBytes = magick.ToByteArray();
        }

        AssetId id1 = store.RegisterAsset(exifJpegBytes);
        AssetId id2 = store.RegisterAsset(exifJpegBytes);
        Assert.Equal(id1, id2);
        Assert.Equal(1, store.AssetCount);

        Assert.True(store.TryGetMetadata(id1, out AssetMetadata? meta));
        Assert.NotNull(meta);
        Assert.Equal(SKEncodedOrigin.RightTop, meta!.Origin);
        Assert.Equal(160, meta.WidthPx);
        Assert.Equal(320, meta.HeightPx);

        using (AssetImageLease preview = store.AcquireImage(id1, RenderQualityMode.InteractivePreview))
        {
            Assert.NotNull(preview.Image);
            Assert.Equal(160, preview.Image!.Width);
            Assert.Equal(320, preview.Image!.Height);
        }

        // Large 3000x2000 PNG must downsample in InteractivePreview <= 2048px while keeping full res in PrintFullResolution
        byte[] largePng;
        using (var surface = SKSurface.Create(new SKImageInfo(3000, 2000, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            surface.Canvas.Clear(SKColors.Teal);
            using SKImage snap = surface.Snapshot();
            using SKData encoded = snap.Encode(SKEncodedImageFormat.Png, 100);
            largePng = encoded.ToArray();
        }

        AssetId largeId = store.RegisterAsset(largePng);
        using (AssetImageLease previewLease = store.AcquireImage(largeId, RenderQualityMode.InteractivePreview))
        {
            Assert.NotNull(previewLease.Image);
            Assert.True(Math.Max(previewLease.Image!.Width, previewLease.Image!.Height) <= AssetStore.MaxPreviewEdgePx);
        }

        using (AssetImageLease printLease = store.AcquireImage(largeId, RenderQualityMode.PrintFullResolution))
        {
            Assert.NotNull(printLease.Image);
            Assert.Equal(3000, printLease.Image!.Width);
            Assert.Equal(2000, printLease.Image!.Height);
        }
    }

    [Fact]
    public void ViewportTransform_ZoomAroundCursor_KeepsPointUnderCursorInvariant()
    {
        var initial = new ViewportTransform(Zoom: 1.0, PanXDip: 40.0, PanYDip: 60.0);
        const double cursorX = 240.0;
        const double cursorY = 180.0;

        (double mmXBefore, double mmYBefore) = initial.DipToPageMm(cursorX, cursorY);
        ViewportTransform zoomed = initial.ZoomAroundCursor(cursorX, cursorY, 2.5);
        (double mmXAfter, double mmYAfter) = zoomed.DipToPageMm(cursorX, cursorY);

        Assert.Equal(mmXBefore, mmXAfter, precision: 6);
        Assert.Equal(mmYBefore, mmYAfter, precision: 6);
    }
}
