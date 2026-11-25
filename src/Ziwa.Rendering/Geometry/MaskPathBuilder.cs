using SkiaSharp;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Rendering.Geometry;

public static class MaskPathBuilder
{
    public static SKPath BuildMaskPath(MaskShapeKind mask, SKRect bounds, double cornerRadiusMm = 0.0)
    {
        var path = new SKPath();
        switch (mask)
        {
            case MaskShapeKind.Circle:
                path.AddOval(bounds);
                return path;

            case MaskShapeKind.Arch:
                {
                    float domeHeight = Math.Min(bounds.Height, bounds.Width);
                    var arcRect = SKRect.Create(bounds.Left, bounds.Top, bounds.Width, domeHeight);
                    path.ArcTo(arcRect, 180f, 180f, forceMoveTo: true);
                    path.LineTo(bounds.Right, bounds.Bottom);
                    path.LineTo(bounds.Left, bounds.Bottom);
                    path.Close();
                    return path;
                }

            case MaskShapeKind.Hexagon:
                AddRegularPolygon(path, bounds, sides: 6, startAngleDeg: -90.0);
                return path;

            case MaskShapeKind.Diamond:
                path.MoveTo(bounds.MidX, bounds.Top);
                path.LineTo(bounds.Right, bounds.MidY);
                path.LineTo(bounds.MidX, bounds.Bottom);
                path.LineTo(bounds.Left, bounds.MidY);
                path.Close();
                return path;

            case MaskShapeKind.Heart:
                AddHeart(path, bounds);
                return path;

            case MaskShapeKind.Star:
                AddStar(path, bounds, points: 5, innerRatio: 0.42);
                return path;

            case MaskShapeKind.Seal:
                AddStar(path, bounds, points: 24, innerRatio: 0.88);
                return path;

            default:
                if (cornerRadiusMm > 0.0)
                {
                    float r = (float)cornerRadiusMm;
                    using var rr = new SKRoundRect(bounds, r, r);
                    path.AddRoundRect(rr);
                }
                else
                {
                    path.AddRect(bounds);
                }

                return path;
        }
    }

    public static SKPath BuildShapePath(ShapeNode shape)
    {
        var rect = SKRect.Create(
            (float)shape.BoundsMm.X,
            (float)shape.BoundsMm.Y,
            (float)shape.BoundsMm.Width,
            (float)shape.BoundsMm.Height);

        var path = new SKPath();
        switch (shape.Kind)
        {
            case ShapeKind.Ellipse:
                path.AddOval(rect);
                return path;

            case ShapeKind.Triangle:
                path.MoveTo(rect.MidX, rect.Top);
                path.LineTo(rect.Right, rect.Bottom);
                path.LineTo(rect.Left, rect.Bottom);
                path.Close();
                return path;

            case ShapeKind.Polygon:
                AddRegularPolygon(path, rect, Math.Clamp(shape.PolygonSides, 3, 64), startAngleDeg: -90.0);
                return path;

            case ShapeKind.Star:
                AddStar(path, rect, Math.Clamp(shape.PolygonSides, 3, 64), Math.Clamp(shape.InnerRadiusRatio, 0.1, 0.9));
                return path;

            case ShapeKind.Line:
                path.MoveTo(rect.Left, rect.MidY);
                path.LineTo(rect.Right, rect.MidY);
                return path;

            case ShapeKind.Arrow:
                AddArrow(path, rect);
                return path;

            case ShapeKind.Ring:
                {
                    path.FillType = SKPathFillType.EvenOdd;
                    path.AddOval(rect);
                    double ratio = Math.Clamp(shape.InnerRadiusRatio, 0.1, 0.9);
                    float insetX = (float)(rect.Width * (1.0 - ratio) * 0.5);
                    float insetY = (float)(rect.Height * (1.0 - ratio) * 0.5);
                    var inner = SKRect.Create(
                        rect.Left + insetX,
                        rect.Top + insetY,
                        Math.Max(0.5f, rect.Width - (insetX * 2f)),
                        Math.Max(0.5f, rect.Height - (insetY * 2f)));
                    path.AddOval(inner);
                    return path;
                }

            case ShapeKind.Arc:
                path.AddArc(rect, (float)shape.StartAngleDeg, (float)shape.SweepAngleDeg);
                return path;

            case ShapeKind.Wedge:
                path.MoveTo(rect.MidX, rect.MidY);
                path.ArcTo(rect, (float)shape.StartAngleDeg, (float)shape.SweepAngleDeg, forceMoveTo: false);
                path.Close();
                return path;

            default:
                if (shape.Radii.IsZero)
                {
                    path.AddRect(rect);
                }
                else
                {
                    using var rr = new SKRoundRect();
                    rr.SetRectRadii(
                        rect,
                        [
                            new SKPoint((float)shape.Radii.TopLeft, (float)shape.Radii.TopLeft),
                            new SKPoint((float)shape.Radii.TopRight, (float)shape.Radii.TopRight),
                            new SKPoint((float)shape.Radii.BottomRight, (float)shape.Radii.BottomRight),
                            new SKPoint((float)shape.Radii.BottomLeft, (float)shape.Radii.BottomLeft),
                        ]);
                    path.AddRoundRect(rr);
                }

                return path;
        }
    }

    private static void AddRegularPolygon(SKPath path, SKRect bounds, int sides, double startAngleDeg)
    {
        float cx = bounds.MidX;
        float cy = bounds.MidY;
        float rx = bounds.Width * 0.5f;
        float ry = bounds.Height * 0.5f;

        for (int i = 0; i < sides; i++)
        {
            double angle = (startAngleDeg + ((360.0 / sides) * i)) * Math.PI / 180.0;
            float x = cx + (float)(rx * Math.Cos(angle));
            float y = cy + (float)(ry * Math.Sin(angle));
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }

        path.Close();
    }

    private static void AddStar(SKPath path, SKRect bounds, int points, double innerRatio)
    {
        float cx = bounds.MidX;
        float cy = bounds.MidY;
        float rx = bounds.Width * 0.5f;
        float ry = bounds.Height * 0.5f;
        int totalVertices = points * 2;

        for (int i = 0; i < totalVertices; i++)
        {
            double angle = (-90.0 + ((360.0 / totalVertices) * i)) * Math.PI / 180.0;
            double factor = (i % 2 == 0) ? 1.0 : innerRatio;
            float x = cx + (float)(rx * factor * Math.Cos(angle));
            float y = cy + (float)(ry * factor * Math.Sin(angle));
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }

        path.Close();
    }

    private static void AddHeart(SKPath path, SKRect bounds)
    {
        float l = bounds.Left;
        float t = bounds.Top;
        float w = bounds.Width;
        float h = bounds.Height;

        path.MoveTo(l + (w * 0.50f), t + (h * 0.88f));
        path.CubicTo(
            l + (w * 0.10f), t + (h * 0.62f),
            l + (w * 0.02f), t + (h * 0.25f),
            l + (w * 0.28f), t + (h * 0.14f));
        path.CubicTo(
            l + (w * 0.42f), t + (h * 0.08f),
            l + (w * 0.50f), t + (h * 0.24f),
            l + (w * 0.50f), t + (h * 0.28f));
        path.CubicTo(
            l + (w * 0.50f), t + (h * 0.24f),
            l + (w * 0.58f), t + (h * 0.08f),
            l + (w * 0.72f), t + (h * 0.14f));
        path.CubicTo(
            l + (w * 0.98f), t + (h * 0.25f),
            l + (w * 0.90f), t + (h * 0.62f),
            l + (w * 0.50f), t + (h * 0.88f));
        path.Close();
    }

    private static void AddArrow(SKPath path, SKRect bounds)
    {
        float shaftHalfH = bounds.Height * 0.18f;
        float headLength = Math.Min(bounds.Width * 0.40f, bounds.Height * 0.90f);
        float shaftRight = bounds.Right - headLength;

        path.MoveTo(bounds.Left, bounds.MidY - shaftHalfH);
        path.LineTo(shaftRight, bounds.MidY - shaftHalfH);
        path.LineTo(shaftRight, bounds.Top);
        path.LineTo(bounds.Right, bounds.MidY);
        path.LineTo(shaftRight, bounds.Bottom);
        path.LineTo(shaftRight, bounds.MidY + shaftHalfH);
        path.LineTo(bounds.Left, bounds.MidY + shaftHalfH);
        path.Close();
    }
}
