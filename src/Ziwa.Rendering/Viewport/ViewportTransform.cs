using Ziwa.Domain.Math;

namespace Ziwa.Rendering.Viewport;

public readonly record struct ViewportTransform(
    double Zoom,
    double PanXDip,
    double PanYDip,
    double DpiScale = 1.0)
{
    public const double BaseDipPerInch = 96.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 16.0;

    public double DipPerMm => (BaseDipPerInch / DomainMath.MmPerInch) * Zoom;

    public double DevicePixelsPerMm => DipPerMm * DpiScale;

    public (double XDip, double YDip) PageMmToDip(double xMm, double yMm)
    {
        double scale = DipPerMm;
        return (PanXDip + (xMm * scale), PanYDip + (yMm * scale));
    }

    public (double XMm, double YMm) DipToPageMm(double xDip, double yDip)
    {
        double scale = DipPerMm;
        return ((xDip - PanXDip) / scale, (yDip - PanYDip) / scale);
    }

    public static ViewportTransform FitPage(
        double viewportWidthDip,
        double viewportHeightDip,
        double pageWidthMm,
        double pageHeightMm,
        double paddingDip = 32.0,
        double dpiScale = 1.0)
    {
        if (viewportWidthDip <= 0.0 || viewportHeightDip <= 0.0 || pageWidthMm <= 0.0 || pageHeightMm <= 0.0)
        {
            return new ViewportTransform(1.0, paddingDip, paddingDip, dpiScale);
        }

        double availableW = Math.Max(40.0, viewportWidthDip - (paddingDip * 2.0));
        double availableH = Math.Max(40.0, viewportHeightDip - (paddingDip * 2.0));

        double baseDipPerMm = BaseDipPerInch / DomainMath.MmPerInch;
        double pageBaseW = pageWidthMm * baseDipPerMm;
        double pageBaseH = pageHeightMm * baseDipPerMm;

        double fitZoom = DomainMath.Clamp(Math.Min(availableW / pageBaseW, availableH / pageBaseH), MinZoom, MaxZoom);
        double scaledW = pageBaseW * fitZoom;
        double scaledH = pageBaseH * fitZoom;

        double panX = (viewportWidthDip - scaledW) * 0.5;
        double panY = (viewportHeightDip - scaledH) * 0.5;
        return new ViewportTransform(fitZoom, panX, panY, dpiScale);
    }

    public ViewportTransform ZoomAroundCursor(double cursorXDip, double cursorYDip, double targetZoom)
    {
        double clampedZoom = DomainMath.Clamp(targetZoom, MinZoom, MaxZoom);
        (double anchorXMm, double anchorYMm) = DipToPageMm(cursorXDip, cursorYDip);
        double newScale = (BaseDipPerInch / DomainMath.MmPerInch) * clampedZoom;
        double newPanX = cursorXDip - (anchorXMm * newScale);
        double newPanY = cursorYDip - (anchorYMm * newScale);
        return this with { Zoom = clampedZoom, PanXDip = newPanX, PanYDip = newPanY };
    }

    public ViewportTransform PanBy(double deltaXDip, double deltaYDip)
    {
        return this with { PanXDip = PanXDip + deltaXDip, PanYDip = PanYDip + deltaYDip };
    }
}
