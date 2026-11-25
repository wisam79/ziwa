using System.Runtime.InteropServices;
using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Validation;
using Ziwa.Rendering.PrePress;

namespace Ziwa.Rendering.Export;

public static class TiledRasterRenderer
{
    public const int DefaultMaxSingleSurfaceEdgePx = 6000;
    public const int DefaultTileSizePx = 2048;

    public static (double BleedMm, double MarksMarginMm, double TotalWidthMm, double TotalHeightMm) ComputeExportMediaGeometry(
        PageModel page,
        bool includeBleed,
        bool includeCropMarks,
        bool includeRegistrationMarks)
    {
        ArgumentNullException.ThrowIfNull(page);

        double bleedMm = includeBleed ? page.BleedMm : 0.0;
        double marksMm = (includeCropMarks || includeRegistrationMarks)
            ? PrintMarksAndCutContourRenderer.DefaultMarksMarginMm
            : 0.0;
        double padMm = bleedMm + marksMm;

        return (bleedMm, marksMm, page.WidthMm + (2.0 * padMm), page.HeightMm + (2.0 * padMm));
    }

    public static bool RequiresTiledRender(
        int widthPx,
        int heightPx,
        int maxSingleSurfaceEdgePx = DefaultMaxSingleSurfaceEdgePx)
    {
        return widthPx > maxSingleSurfaceEdgePx || heightPx > maxSingleSurfaceEdgePx;
    }

    public static void RenderPageWithMarksOnMmCanvas(
        SKCanvas canvas,
        SceneRenderer renderer,
        PageModel page,
        double dpi,
        bool includeBleed,
        bool includeCropMarks,
        bool includeRegistrationMarks,
        bool includeCutContour)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(page);
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));

        (double bleedMm, double marksMm, _, _) = ComputeExportMediaGeometry(
            page,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks);

        int pageSave = canvas.Save();
        try
        {
            if (marksMm > 0.0)
            {
                canvas.Translate((float)marksMm, (float)marksMm);
            }

            var options = new RenderOptions(
                Dpi: dpi,
                QualityMode: RenderQualityMode.PrintFullResolution,
                IsPreScaledToMillimeters: true,
                IncludeBleed: includeBleed);

            renderer.RenderPage(canvas, page, options);
        }
        finally
        {
            canvas.RestoreToCount(pageSave);
        }

        if (includeCutContour || includeCropMarks || includeRegistrationMarks)
        {
            int marksSave = canvas.Save();
            try
            {
                double trimOriginMm = marksMm + bleedMm;
                if (trimOriginMm > 0.0)
                {
                    canvas.Translate((float)trimOriginMm, (float)trimOriginMm);
                }

                if (includeCutContour)
                {
                    PrintMarksAndCutContourRenderer.RenderCutContourOverlay(canvas, page);
                }

                if (includeCropMarks || includeRegistrationMarks)
                {
                    PrintMarksAndCutContourRenderer.RenderCropAndRegistrationMarks(
                        canvas,
                        page,
                        includeCropMarks,
                        includeRegistrationMarks);
                }
            }
            finally
            {
                canvas.RestoreToCount(marksSave);
            }
        }
    }

    public static byte[] RenderPageToRgbaBytes(
        SceneRenderer renderer,
        PageModel page,
        double dpi,
        bool includeBleed,
        bool includeCropMarks,
        bool includeRegistrationMarks,
        bool includeCutContour,
        SKColor clearColor,
        out int totalWidthPx,
        out int totalHeightPx,
        int maxSingleSurfaceEdgePx = DefaultMaxSingleSurfaceEdgePx,
        int tileSizePx = DefaultTileSizePx,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(page);
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        cancellationToken.ThrowIfCancellationRequested();

        tileSizePx = Math.Max(64, tileSizePx);

        (_, _, double totalWmm, double totalHmm) = ComputeExportMediaGeometry(
            page,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks);

        totalWidthPx = DomainMath.MmToPixelsRounded(totalWmm, dpi);
        totalHeightPx = DomainMath.MmToPixelsRounded(totalHmm, dpi);

        int totalStrideBytes = checked(totalWidthPx * 4);
        byte[] fullRgba = new byte[checked(totalStrideBytes * totalHeightPx)];
        float pxPerMm = (float)DomainMath.PixelsPerMm(dpi);

        if (!RequiresTiledRender(totalWidthPx, totalHeightPx, maxSingleSurfaceEdgePx))
        {
            RenderSingleSurface(
                renderer,
                page,
                dpi,
                includeBleed,
                includeCropMarks,
                includeRegistrationMarks,
                includeCutContour,
                clearColor,
                totalWidthPx,
                totalHeightPx,
                pxPerMm,
                totalStrideBytes,
                fullRgba);
            return fullRgba;
        }

        RenderTiledSurfaces(
            renderer,
            page,
            dpi,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks,
            includeCutContour,
            clearColor,
            totalWidthPx,
            totalHeightPx,
            tileSizePx,
            pxPerMm,
            totalStrideBytes,
            fullRgba,
            cancellationToken);

        return fullRgba;
    }

    private static void RenderSingleSurface(
        SceneRenderer renderer,
        PageModel page,
        double dpi,
        bool includeBleed,
        bool includeCropMarks,
        bool includeRegistrationMarks,
        bool includeCutContour,
        SKColor clearColor,
        int totalWidthPx,
        int totalHeightPx,
        float pxPerMm,
        int totalStrideBytes,
        byte[] fullRgba)
    {
        var surfaceInfo = new SKImageInfo(totalWidthPx, totalHeightPx, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(surfaceInfo)
            ?? throw new InvalidOperationException($"Unable to allocate SKSurface ({totalWidthPx}x{totalHeightPx}).");

        SKCanvas canvas = surface.Canvas;
        int saveCount = canvas.Save();
        try
        {
            canvas.Clear(clearColor);
            canvas.Scale(pxPerMm, pxPerMm);
            RenderPageWithMarksOnMmCanvas(
                canvas,
                renderer,
                page,
                dpi,
                includeBleed,
                includeCropMarks,
                includeRegistrationMarks,
                includeCutContour);
            canvas.Flush();
        }
        finally
        {
            canvas.RestoreToCount(saveCount);
        }

        var readInfo = new SKImageInfo(totalWidthPx, totalHeightPx, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        GCHandle handle = GCHandle.Alloc(fullRgba, GCHandleType.Pinned);
        try
        {
            if (!surface.ReadPixels(readInfo, handle.AddrOfPinnedObject(), totalStrideBytes, 0, 0))
            {
                throw new InvalidOperationException("Failed to read pixels from SKSurface.");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static void RenderTiledSurfaces(
        SceneRenderer renderer,
        PageModel page,
        double dpi,
        bool includeBleed,
        bool includeCropMarks,
        bool includeRegistrationMarks,
        bool includeCutContour,
        SKColor clearColor,
        int totalWidthPx,
        int totalHeightPx,
        int tileSizePx,
        float pxPerMm,
        int totalStrideBytes,
        byte[] fullRgba,
        CancellationToken cancellationToken)
    {
        int surfaceWidth = Math.Min(tileSizePx, totalWidthPx);
        int surfaceHeight = Math.Min(tileSizePx, totalHeightPx);
        var surfaceInfo = new SKImageInfo(surfaceWidth, surfaceHeight, SKColorType.Rgba8888, SKAlphaType.Premul);

        using SKSurface tileSurface = SKSurface.Create(surfaceInfo)
            ?? throw new InvalidOperationException($"Unable to allocate tile SKSurface ({surfaceWidth}x{surfaceHeight}).");

        byte[] tileBuffer = new byte[checked(surfaceWidth * surfaceHeight * 4)];
        GCHandle tileHandle = GCHandle.Alloc(tileBuffer, GCHandleType.Pinned);
        try
        {
            nint tilePtr = tileHandle.AddrOfPinnedObject();
            SKCanvas tileCanvas = tileSurface.Canvas;

            for (int tileY = 0; tileY < totalHeightPx; tileY += tileSizePx)
            {
                int tileH = Math.Min(tileSizePx, totalHeightPx - tileY);
                for (int tileX = 0; tileX < totalWidthPx; tileX += tileSizePx)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int tileW = Math.Min(tileSizePx, totalWidthPx - tileX);
                    int tileSave = tileCanvas.Save();
                    try
                    {
                        tileCanvas.Clear(clearColor);
                        tileCanvas.Translate(-tileX, -tileY);
                        tileCanvas.Scale(pxPerMm, pxPerMm);
                        RenderPageWithMarksOnMmCanvas(
                            tileCanvas,
                            renderer,
                            page,
                            dpi,
                            includeBleed,
                            includeCropMarks,
                            includeRegistrationMarks,
                            includeCutContour);
                        tileCanvas.Flush();
                    }
                    finally
                    {
                        tileCanvas.RestoreToCount(tileSave);
                    }

                    var tileReadInfo = new SKImageInfo(tileW, tileH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    int tileRowBytes = tileW * 4;
                    if (!tileSurface.ReadPixels(tileReadInfo, tilePtr, tileRowBytes, 0, 0))
                    {
                        throw new InvalidOperationException($"Failed to read pixels from tile SKSurface ({tileW}x{tileH} at {tileX},{tileY}).");
                    }

                    for (int y = 0; y < tileH; y++)
                    {
                        int srcOffset = y * tileRowBytes;
                        int dstOffset = ((tileY + y) * totalStrideBytes) + (tileX * 4);
                        tileBuffer.AsSpan(srcOffset, tileRowBytes).CopyTo(fullRgba.AsSpan(dstOffset, tileRowBytes));
                    }
                }
            }
        }
        finally
        {
            tileHandle.Free();
        }
    }
}
