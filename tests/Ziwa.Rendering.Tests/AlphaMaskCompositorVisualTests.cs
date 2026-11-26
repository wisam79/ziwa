using SkiaSharp;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Ai;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Geometry;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Tests;

public sealed class AlphaMaskCompositorVisualTests
{
    [Fact]
    public void EditableAlphaMask_SupportsEraseRestoreSoftBrush_PatchUndoRedo_AndResetToAuto()
    {
        byte[] initial = new byte[64 * 64];
        Array.Fill(initial, (byte)255);
        var mask = new EditableAlphaMask(64, 64, initial);

        mask.BeginStroke();
        SKRectI eraseDirty = mask.ApplyBrushSegment(16f, 16f, 24f, 16f, radiusPx: 8f, softness: 0.5f, MaskBrushMode.Erase);
        Assert.False(eraseDirty.IsEmpty);
        Assert.True(mask.EndStroke());
        Assert.Equal(0, mask.AlphaSpan[(16 * 64) + 16]);
        Assert.Equal(255, mask.AlphaSpan[(55 * 64) + 55]);
        Assert.True(mask.CanUndo);

        // Undo erase stroke restores opaque pixel
        Assert.NotNull(mask.Undo());
        Assert.Equal(255, mask.AlphaSpan[(16 * 64) + 16]);
        Assert.True(mask.CanRedo);

        // Redo re-applies erase stroke
        Assert.NotNull(mask.Redo());
        Assert.Equal(0, mask.AlphaSpan[(16 * 64) + 16]);

        // Restore brush recovers erased area
        mask.BeginStroke();
        mask.ApplyBrushSegment(16f, 16f, 16f, 16f, radiusPx: 10f, softness: 0.4f, MaskBrushMode.Restore);
        Assert.True(mask.EndStroke());
        Assert.Equal(255, mask.AlphaSpan[(16 * 64) + 16]);

        // Erase again, then reset to initial AI baseline
        mask.BeginStroke();
        mask.ApplyBrushSegment(48f, 48f, 48f, 48f, radiusPx: 6f, softness: 0.3f, MaskBrushMode.Erase);
        mask.EndStroke();
        Assert.Equal(0, mask.AlphaSpan[(48 * 64) + 48]);

        Assert.NotNull(mask.ResetToInitialAiMask());
        Assert.Equal(255, mask.AlphaSpan[(48 * 64) + 48]);
    }

    [Fact]
    public async Task MaskCutoutAndIdFaceFraming_RenderDeterministicallyAt300Dpi()
    {
        using var store = new AssetStore();
        using var registry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(registry);
        var renderer = new SceneRenderer(store, shaper, new TextOnCurveRenderer(registry, shaper));
        var exporter = new PngJpegExporter(renderer);

        byte[] portraitBytes = CreateTestPortraitPng(320, 240, out FaceRectPx expectedFace);
        AssetId originalId = store.RegisterAsset(portraitBytes);

        // Apply alpha mask preserving subject and clearing background
        using SKBitmap decoded = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(portraitBytes);
        byte[] alpha = new byte[decoded.Width * decoded.Height];
        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                double dx = (x - 200.0) / 55.0;
                double dy = (y - 105.0) / 75.0;
                alpha[(y * decoded.Width) + x] = ((dx * dx) + (dy * dy) <= 1.0) ? (byte)255 : (byte)0;
            }
        }

        byte[] cutoutBytes = AlphaMaskCompositor.EncodeCutoutPngBytes(decoded, alpha);
        AssetId cutoutId = store.RegisterAsset(cutoutBytes);

        CropSpec framing = IdPhotoFramingCatalog.ComputeFramingCrop(
            decoded.Width,
            decoded.Height,
            frameWidthMm: 35.0,
            frameHeightMm: 45.0,
            face: expectedFace,
            preset: IdPhotoFramingPreset.PassportIcao35x45);

        SKRect srcRect = CropViewportMath.ComputeCoverSourceRect(decoded.Width, decoded.Height, 35.0, 45.0, framing);
        double faceCenterX = expectedFace.X + (expectedFace.Width * 0.5);
        Assert.InRange(Math.Abs(srcRect.MidX - faceCenterX), 0.0, 8.0);

        var page = new PageModel(
            Id: new PageId(Guid.Parse("88888888-1111-1111-1111-111111111111")),
            Name: "هوية معزولة ومؤطرة",
            WidthMm: 55.0,
            HeightMm: 65.0,
            BleedMm: 0.0,
            SafeMarginMm: 3.0,
            Background: new SolidBackground(new ColorRgba(240, 244, 248, 255)),
            Nodes:
            [
                new ImageNode(
                    Id: new NodeId(Guid.Parse("77777777-1111-1111-1111-111111111111")),
                    Name: "صورة الهوية",
                    BoundsMm: new RectMm(10.0, 10.0, 35.0, 45.0),
                    CurrentAssetId: cutoutId,
                    OriginalAssetId: originalId,
                    Crop: framing),
            ]);

        byte[] png300 = exporter.ExportPageToBytes(page, 300.0, RasterExportFormat.Png);
        using SKBitmap exported = SKBitmap.Decode(png300);

        // Background color outside subject shows page background, not original dark studio wall
        SKColor cornerPixel = exported.GetPixel(
            DomainMath.MmToPixelsRounded(11.5, 300.0),
            DomainMath.MmToPixelsRounded(11.5, 300.0));
        Assert.Equal(240, cornerPixel.Red);
        Assert.Equal(244, cornerPixel.Green);
        Assert.Equal(248, cornerPixel.Blue);

        await Verifier.Verify(new
        {
            ExportWidth300 = exported.Width,
            ExportHeight300 = exported.Height,
            FramingZoom = Math.Round(framing.Zoom, 3),
            FramingPanX = Math.Round(framing.PanX, 3),
            FramingPanY = Math.Round(framing.PanY, 3),
            CropMidXCentered = Math.Abs(srcRect.MidX - faceCenterX) <= 8.0,
            CutoutPreservesPageBackdrop = cornerPixel == new SKColor(240, 244, 248, 255),
        });
    }

    private static byte[] CreateTestPortraitPng(int w, int h, out FaceRectPx faceRect)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas c = surface.Canvas;
        c.Clear(new SKColor(22, 48, 72, 255));

        float cx = 200f;
        float cy = 96f;
        float rx = 28f;
        float ry = 36f;
        using var skinPaint = new SKPaint { Color = new SKColor(216, 168, 138, 255), IsAntialias = true };
        c.DrawOval(cx, cy, rx, ry, skinPaint);

        faceRect = new FaceRectPx(cx - rx, cy - ry, rx * 2.0, ry * 2.0, 0.95);
        using SKImage img = surface.Snapshot();
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
