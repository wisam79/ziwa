using Ziwa.Domain.Validation;

namespace Ziwa.Domain.Math;

public static class DomainMath
{
    public const double MmPerInch = 25.4;
    public const double PointsPerInch = 72.0;

    public static double MmToPx(double mm, double dpi)
    {
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        if (!double.IsFinite(mm))
        {
            throw new ArgumentOutOfRangeException(nameof(mm), "Millimeter value must be finite.");
        }

        return (mm / MmPerInch) * dpi;
    }

    public static double PxToMm(double px, double dpi)
    {
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        if (!double.IsFinite(px))
        {
            throw new ArgumentOutOfRangeException(nameof(px), "Pixel value must be finite.");
        }

        return (px / dpi) * MmPerInch;
    }

    public static double MmToPt(double mm)
    {
        if (!double.IsFinite(mm))
        {
            throw new ArgumentOutOfRangeException(nameof(mm), "Millimeter value must be finite.");
        }

        return (mm / MmPerInch) * PointsPerInch;
    }

    public static double PtToMm(double pt)
    {
        if (!double.IsFinite(pt))
        {
            throw new ArgumentOutOfRangeException(nameof(pt), "Point value must be finite.");
        }

        return (pt / PointsPerInch) * MmPerInch;
    }

    public static double PixelsPerMm(double dpi)
    {
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        return dpi / MmPerInch;
    }

    public static int MmToPixelsRounded(double mm, double dpi)
    {
        SchemaValidator.AssertFinitePositive(mm, nameof(mm));
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        int rounded = (int)System.Math.Round(MmToPx(mm, dpi), MidpointRounding.AwayFromZero);
        return System.Math.Max(1, rounded);
    }

    public static uint DpiToPixelsPerMeter(double dpi)
    {
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        return (uint)System.Math.Round((dpi * 1000.0) / MmPerInch, MidpointRounding.AwayFromZero);
    }

    public static double Clamp(double value, double min, double max)
    {
        if (!double.IsFinite(value) || !double.IsFinite(min) || !double.IsFinite(max))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Clamp arguments must be finite numbers.");
        }

        if (min > max)
        {
            throw new ArgumentException("Minimum clamp bound cannot exceed maximum bound.", nameof(min));
        }

        return System.Math.Clamp(value, min, max);
    }
}
