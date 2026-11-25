using System.Runtime.InteropServices;
using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Validation;

namespace Ziwa.Rendering.Export;

public enum RasterExportFormat
{
    Png,
    Jpeg,
}

public sealed class PngJpegExporter
{
    private readonly SceneRenderer _renderer;

    public PngJpegExporter(SceneRenderer renderer)
    {
        _renderer = renderer;
    }

    public byte[] ExportPageToBytes(
        PageModel page,
        double dpi,
        RasterExportFormat format = RasterExportFormat.Png,
        int jpegQuality = 95,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        int maxSingleSurfaceEdgePx = TiledRasterRenderer.DefaultMaxSingleSurfaceEdgePx,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        cancellationToken.ThrowIfCancellationRequested();

        double bleed = includeBleed ? page.BleedMm : 0.0;
        int widthPx = DomainMath.MmToPixelsRounded(page.WidthMm + (bleed * 2.0), dpi);
        int heightPx = DomainMath.MmToPixelsRounded(page.HeightMm + (bleed * 2.0), dpi);

        SKEncodedImageFormat encodedFormat = format == RasterExportFormat.Jpeg
            ? SKEncodedImageFormat.Jpeg
            : SKEncodedImageFormat.Png;
        int clampedQuality = Math.Clamp(jpegQuality, 1, 100);
        SKColor clearColor = format == RasterExportFormat.Jpeg ? SKColors.White : SKColors.Transparent;

        if (!includeCropMarks &&
            !includeRegistrationMarks &&
            !includeCutContour &&
            !TiledRasterRenderer.RequiresTiledRender(widthPx, heightPx, maxSingleSurfaceEdgePx))
        {
            var imageInfo = new SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKSurface surface = SKSurface.Create(imageInfo)
                ?? throw new InvalidOperationException($"Unable to allocate SKSurface ({widthPx}x{heightPx}).");

            surface.Canvas.Clear(clearColor);

            var options = new RenderOptions(
                Dpi: dpi,
                QualityMode: RenderQualityMode.PrintFullResolution,
                IsPreScaledToMillimeters: false,
                IncludeBleed: includeBleed);

            _renderer.RenderPage(surface.Canvas, page, options);
            surface.Canvas.Flush();

            using SKImage snapshot = surface.Snapshot();
            using SKData encoded = snapshot.Encode(encodedFormat, clampedQuality)
                ?? throw new InvalidOperationException($"Failed to encode page snapshot as {encodedFormat}.");

            ReadOnlySpan<byte> rawBytes = encoded.AsSpan();
            return format == RasterExportFormat.Jpeg
                ? RasterDpiMetadataInjector.InjectJpegJfifDensity(rawBytes, dpi)
                : RasterDpiMetadataInjector.InjectPngPhysChunk(rawBytes, dpi);
        }

        byte[] rgbaBytes = TiledRasterRenderer.RenderPageToRgbaBytes(
            _renderer,
            page,
            dpi,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks,
            includeCutContour,
            clearColor,
            out int totalW,
            out int totalH,
            maxSingleSurfaceEdgePx: maxSingleSurfaceEdgePx,
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var pixelInfo = new SKImageInfo(totalW, totalH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        GCHandle handle = GCHandle.Alloc(rgbaBytes, GCHandleType.Pinned);
        try
        {
            using SKImage image = SKImage.FromPixelCopy(pixelInfo, handle.AddrOfPinnedObject(), totalW * 4)
                ?? throw new InvalidOperationException($"Failed to wrap tiled RGBA pixels ({totalW}x{totalH}).");
            using SKData encoded = image.Encode(encodedFormat, clampedQuality)
                ?? throw new InvalidOperationException($"Failed to encode tiled page as {encodedFormat}.");

            ReadOnlySpan<byte> rawBytes = encoded.AsSpan();
            return format == RasterExportFormat.Jpeg
                ? RasterDpiMetadataInjector.InjectJpegJfifDensity(rawBytes, dpi)
                : RasterDpiMetadataInjector.InjectPngPhysChunk(rawBytes, dpi);
        }
        finally
        {
            handle.Free();
        }
    }
}
