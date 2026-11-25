using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Primitives;

namespace Ziwa.Rendering.Geometry;

public static class CropViewportMath
{
    public static SKRect ComputeCoverSourceRect(
        int imageWidthPx,
        int imageHeightPx,
        double slotWidthMm,
        double slotHeightMm,
        CropSpec crop)
    {
        if (imageWidthPx <= 0 || imageHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidthPx), "Image dimensions must be positive.");
        }

        if (slotWidthMm <= 0.0 || slotHeightMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotWidthMm), "Slot dimensions must be positive.");
        }

        double zoom = DomainMath.Clamp(crop.Zoom, 1.0, 20.0);
        double imageAspect = (double)imageWidthPx / imageHeightPx;
        double slotAspect = slotWidthMm / slotHeightMm;

        double baseCropW;
        double baseCropH;
        if (imageAspect > slotAspect)
        {
            baseCropH = imageHeightPx;
            baseCropW = imageHeightPx * slotAspect;
        }
        else
        {
            baseCropW = imageWidthPx;
            baseCropH = imageWidthPx / slotAspect;
        }

        double cropW = baseCropW / zoom;
        double cropH = baseCropH / zoom;

        double maxPanX = Math.Max(0.0, (imageWidthPx - cropW) * 0.5);
        double maxPanY = Math.Max(0.0, (imageHeightPx - cropH) * 0.5);

        double panXFactor = DomainMath.Clamp(crop.PanX, -1.0, 1.0);
        double panYFactor = DomainMath.Clamp(crop.PanY, -1.0, 1.0);

        double centerX = (imageWidthPx * 0.5) + (panXFactor * maxPanX);
        double centerY = (imageHeightPx * 0.5) + (panYFactor * maxPanY);

        float left = (float)(centerX - (cropW * 0.5));
        float top = (float)(centerY - (cropH * 0.5));
        return SKRect.Create(left, top, (float)cropW, (float)cropH);
    }
}
