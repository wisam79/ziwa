using System.Collections.Immutable;
using Ziwa.Domain.Math;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Domain.Scanner;

public readonly record struct PointPx(double X, double Y)
{
    public double DistanceTo(PointPx other)
    {
        double dx = other.X - X;
        double dy = other.Y - Y;
        return System.Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static PointPx Lerp(PointPx a, PointPx b, double t)
    {
        double clampedT = DomainMath.Clamp(t, 0.0, 1.0);
        return new PointPx(
            a.X + ((b.X - a.X) * clampedT),
            a.Y + ((b.Y - a.Y) * clampedT));
    }
}

public enum IdCardSplitAxis
{
    HorizontalTopBottom = 0,
    VerticalLeftRight = 1,
}

public enum ScanDetectionMode
{
    AutoSingleOrMulti = 0,
    SingleDocument = 1,
    MultiCards = 2,
}

public sealed record DocumentQuadPx(
    PointPx TopLeft,
    PointPx TopRight,
    PointPx BottomRight,
    PointPx BottomLeft)
{
    public PointPx GetCorner(int index) => index switch
    {
        0 => TopLeft,
        1 => TopRight,
        2 => BottomRight,
        3 => BottomLeft,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Corner index must be within [0, 3]."),
    };

    public DocumentQuadPx WithCorner(int index, PointPx point, int imageWidthPx, int imageHeightPx)
    {
        if (imageWidthPx <= 0 || imageHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidthPx), "Image dimensions must be positive.");
        }

        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            return this;
        }

        var clamped = new PointPx(
            DomainMath.Clamp(point.X, 0.0, imageWidthPx),
            DomainMath.Clamp(point.Y, 0.0, imageHeightPx));

        DocumentQuadPx candidate = index switch
        {
            0 => this with { TopLeft = clamped },
            1 => this with { TopRight = clamped },
            2 => this with { BottomRight = clamped },
            3 => this with { BottomLeft = clamped },
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "Corner index must be within [0, 3]."),
        };

        return candidate.ComputeAreaPx() >= 16.0 ? candidate : this;
    }

    public double ComputeAreaPx()
    {
        ReadOnlySpan<PointPx> pts = [TopLeft, TopRight, BottomRight, BottomLeft];
        double sum = 0.0;
        for (int i = 0; i < 4; i++)
        {
            PointPx a = pts[i];
            PointPx b = pts[(i + 1) % 4];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return System.Math.Abs(sum) * 0.5;
    }

    public (int WidthPx, int HeightPx) ComputeOutputDimensionsPx(int maxEdgePx = 4096)
    {
        double topW = TopLeft.DistanceTo(TopRight);
        double botW = BottomLeft.DistanceTo(BottomRight);
        double leftH = TopLeft.DistanceTo(BottomLeft);
        double rightH = TopRight.DistanceTo(BottomRight);

        double rawW = System.Math.Max(16.0, System.Math.Max(topW, botW));
        double rawH = System.Math.Max(16.0, System.Math.Max(leftH, rightH));

        int safeMax = System.Math.Max(64, maxEdgePx);
        double maxSide = System.Math.Max(rawW, rawH);
        if (maxSide > safeMax)
        {
            double scale = safeMax / maxSide;
            rawW *= scale;
            rawH *= scale;
        }

        int w = System.Math.Max(16, (int)System.Math.Round(rawW, MidpointRounding.AwayFromZero));
        int h = System.Math.Max(16, (int)System.Math.Round(rawH, MidpointRounding.AwayFromZero));
        return (w, h);
    }

    public (DocumentQuadPx First, DocumentQuadPx Second) SplitForIdCard(
        IdCardSplitAxis axis,
        double splitRatio = 0.5,
        double gutterTrimRatio = 0.02)
    {
        double ratio = DomainMath.Clamp(splitRatio, 0.2, 0.8);
        double halfGutter = DomainMath.Clamp(gutterTrimRatio, 0.0, 0.12) * 0.5;
        double t1 = DomainMath.Clamp(ratio - halfGutter, 0.10, 0.88);
        double t2 = DomainMath.Clamp(ratio + halfGutter, 0.12, 0.90);

        if (axis == IdCardSplitAxis.HorizontalTopBottom)
        {
            var top = new DocumentQuadPx(
                TopLeft,
                TopRight,
                PointPx.Lerp(TopRight, BottomRight, t1),
                PointPx.Lerp(TopLeft, BottomLeft, t1));

            var bottom = new DocumentQuadPx(
                PointPx.Lerp(TopLeft, BottomLeft, t2),
                PointPx.Lerp(TopRight, BottomRight, t2),
                BottomRight,
                BottomLeft);

            return (top, bottom);
        }

        var left = new DocumentQuadPx(
            TopLeft,
            PointPx.Lerp(TopLeft, TopRight, t1),
            PointPx.Lerp(BottomLeft, BottomRight, t1),
            BottomLeft);

        var right = new DocumentQuadPx(
            PointPx.Lerp(TopLeft, TopRight, t2),
            TopRight,
            BottomRight,
            PointPx.Lerp(BottomLeft, BottomRight, t2));

        return (left, right);
    }

    public static DocumentQuadPx CreateInsetDefault(int imageWidthPx, int imageHeightPx, double marginRatio = 0.08)
    {
        if (imageWidthPx <= 0 || imageHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidthPx), "Image dimensions must be positive.");
        }

        double m = DomainMath.Clamp(marginRatio, 0.0, 0.35);
        double left = imageWidthPx * m;
        double top = imageHeightPx * m;
        double right = imageWidthPx * (1.0 - m);
        double bottom = imageHeightPx * (1.0 - m);
        return new DocumentQuadPx(
            new PointPx(left, top),
            new PointPx(right, top),
            new PointPx(right, bottom),
            new PointPx(left, bottom));
    }

    public static DocumentQuadPx OrderClockwise(ReadOnlySpan<PointPx> points)
    {
        if (points.Length != 4)
        {
            throw new ArgumentException("Exactly 4 corner points are required.", nameof(points));
        }

        double cx = 0.0;
        double cy = 0.0;
        for (int i = 0; i < 4; i++)
        {
            if (!double.IsFinite(points[i].X) || !double.IsFinite(points[i].Y))
            {
                throw new ArgumentOutOfRangeException(nameof(points), "Corner coordinates must be finite.");
            }

            cx += points[i].X;
            cy += points[i].Y;
        }

        cx *= 0.25;
        cy *= 0.25;

        PointPx[] sorted = points.ToArray();
        Array.Sort(sorted, (a, b) =>
        {
            double angA = System.Math.Atan2(a.Y - cy, a.X - cx);
            double angB = System.Math.Atan2(b.Y - cy, b.X - cx);
            return angA.CompareTo(angB);
        });

        int topLeftIndex = 0;
        double minSum = sorted[0].X + sorted[0].Y;
        for (int i = 1; i < 4; i++)
        {
            double sum = sorted[i].X + sorted[i].Y;
            if (sum < minSum)
            {
                minSum = sum;
                topLeftIndex = i;
            }
        }

        return new DocumentQuadPx(
            sorted[topLeftIndex],
            sorted[(topLeftIndex + 1) % 4],
            sorted[(topLeftIndex + 2) % 4],
            sorted[(topLeftIndex + 3) % 4]);
    }
}

public sealed record DetectedDocumentRegion(
    Guid Id,
    string LabelAr,
    DocumentQuadPx Quad,
    double Confidence = 1.0,
    DocumentScanFilterKind Filter = DocumentScanFilterKind.MagicColor);

public static class DocumentScanFilterCatalog
{
    public static ImmutableArray<DocumentScanFilterKind> All { get; } =
    [
        DocumentScanFilterKind.None,
        DocumentScanFilterKind.MagicColor,
        DocumentScanFilterKind.OtsuBlackAndWhite,
        DocumentScanFilterKind.EnhancedGrayscale,
        DocumentScanFilterKind.RemoveYellowing,
        DocumentScanFilterKind.Sharpen,
    ];

    public static string GetTitleAr(DocumentScanFilterKind kind) => kind switch
    {
        DocumentScanFilterKind.None => "الأصل بدون فلتر",
        DocumentScanFilterKind.MagicColor => "اللون السحري",
        DocumentScanFilterKind.OtsuBlackAndWhite => "Otsu أبيض وأسود",
        DocumentScanFilterKind.EnhancedGrayscale => "رمادي محسن",
        DocumentScanFilterKind.RemoveYellowing => "إزالة الاصفرار",
        DocumentScanFilterKind.Sharpen => "الحدة",
        _ => "الأصل بدون فلتر",
    };

    public static void ValidateQuadWithinBounds(DocumentQuadPx quad, int widthPx, int heightPx)
    {
        SchemaValidator.AssertFinitePositive(widthPx, nameof(widthPx));
        SchemaValidator.AssertFinitePositive(heightPx, nameof(heightPx));
        for (int i = 0; i < 4; i++)
        {
            PointPx p = quad.GetCorner(i);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(quad), "Quad corners must be finite.");
            }
        }
    }
}
