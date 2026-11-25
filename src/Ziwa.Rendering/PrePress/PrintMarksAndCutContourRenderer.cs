using SkiaSharp;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Geometry;

namespace Ziwa.Rendering.PrePress;

public static class PrintMarksAndCutContourRenderer
{
    public const double DefaultMarksMarginMm = 8.0;

    public static SKPath BuildSheetCutContourPath(PageModel page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var accumulated = new SKPath();

        if (page.CollageLayout is { Slots.IsDefaultOrEmpty: false } collage)
        {
            foreach (CollageSlot slot in collage.Slots)
            {
                if (slot.AssignedAssetId is not { IsEmpty: false } ||
                    slot.BoundsMm.Width <= 0.0 ||
                    slot.BoundsMm.Height <= 0.0)
                {
                    continue;
                }

                SKRect slotRect = ToSkRect(slot.BoundsMm);
                using SKPath slotPath = MaskPathBuilder.BuildMaskPath(slot.Mask, slotRect, slot.CornerRadiusMm);
                UnionContour(accumulated, slotPath);
            }
        }

        if (!page.Nodes.IsDefaultOrEmpty)
        {
            foreach (SceneNode node in page.Nodes)
            {
                AppendNodeContours(accumulated, node, page, SKMatrix.Identity);
            }
        }

        return accumulated;
    }

    public static RectMm? GetTightContourBoundsMm(PageModel page)
    {
        ArgumentNullException.ThrowIfNull(page);

        using SKPath path = BuildSheetCutContourPath(page);
        if (path.IsEmpty || !path.GetTightBounds(out SKRect tight))
        {
            return null;
        }

        if (tight.Width <= 0f || tight.Height <= 0f ||
            !float.IsFinite(tight.Left) || !float.IsFinite(tight.Top) ||
            !float.IsFinite(tight.Width) || !float.IsFinite(tight.Height))
        {
            return null;
        }

        return new RectMm(tight.Left, tight.Top, tight.Width, tight.Height);
    }

    public static void RenderCropAndRegistrationMarks(
        SKCanvas canvas,
        PageModel page,
        bool includeCropMarks,
        bool includeRegistrationMarks)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(page);

        if (!includeCropMarks && !includeRegistrationMarks)
        {
            return;
        }

        double offsetMm = Math.Max(1.5, page.BleedMm + 1.0);
        double markLen = Math.Min(4.0, Math.Max(1.5, DefaultMarksMarginMm - 1.5));

        float w = (float)page.WidthMm;
        float h = (float)page.HeightMm;
        float off = (float)offsetMm;
        float len = (float)markLen;

        using var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Black,
            StrokeWidth = 0.12f,
            StrokeCap = SKStrokeCap.Butt,
            IsAntialias = true,
        };

        if (includeCropMarks)
        {
            // Top-left corner
            canvas.DrawLine(-off - len, 0f, -off, 0f, strokePaint);
            canvas.DrawLine(0f, -off - len, 0f, -off, strokePaint);

            // Top-right corner
            canvas.DrawLine(w + off, 0f, w + off + len, 0f, strokePaint);
            canvas.DrawLine(w, -off - len, w, -off, strokePaint);

            // Bottom-left corner
            canvas.DrawLine(-off - len, h, -off, h, strokePaint);
            canvas.DrawLine(0f, h + off, 0f, h + off + len, strokePaint);

            // Bottom-right corner
            canvas.DrawLine(w + off, h, w + off + len, h, strokePaint);
            canvas.DrawLine(w, h + off, w, h + off + len, strokePaint);
        }

        if (includeRegistrationMarks)
        {
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = SKColors.Black,
                IsAntialias = true,
            };

            float regOffset = off + 2.5f;
            DrawRegistrationTarget(canvas, -regOffset, -regOffset, strokePaint, fillPaint);
            DrawRegistrationTarget(canvas, w + regOffset, -regOffset, strokePaint, fillPaint);
            DrawRegistrationTarget(canvas, -regOffset, h + regOffset, strokePaint, fillPaint);
            DrawRegistrationTarget(canvas, w + regOffset, h + regOffset, strokePaint, fillPaint);
        }
    }

    public static void RenderCutContourOverlay(SKCanvas canvas, PageModel page)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(page);

        using SKPath contour = BuildSheetCutContourPath(page);
        if (contour.IsEmpty)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = new SKColor(236, 0, 140, 255),
            StrokeWidth = 0.15f,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        canvas.DrawPath(contour, paint);
    }

    private static void DrawRegistrationTarget(
        SKCanvas canvas,
        float cx,
        float cy,
        SKPaint strokePaint,
        SKPaint fillPaint)
    {
        const float outerRadius = 1.6f;
        const float halfCrosshair = 2.2f;
        const float centerDotRadius = 0.45f;

        canvas.DrawCircle(cx, cy, outerRadius, strokePaint);
        canvas.DrawLine(cx - halfCrosshair, cy, cx + halfCrosshair, cy, strokePaint);
        canvas.DrawLine(cx, cy - halfCrosshair, cx, cy + halfCrosshair, strokePaint);
        canvas.DrawCircle(cx, cy, centerDotRadius, fillPaint);
    }

    private static void AppendNodeContours(
        SKPath accumulated,
        SceneNode node,
        PageModel page,
        SKMatrix parentTransform)
    {
        if (!node.IsVisible || node.Opacity <= 0.0)
        {
            return;
        }

        if (node is GroupNode group)
        {
            if (group.Children.IsDefaultOrEmpty)
            {
                return;
            }

            SKMatrix nextTransform = parentTransform;
            if (Math.Abs(group.RotationDeg) > 0.0001)
            {
                SKMatrix groupRot = SKMatrix.CreateRotationDegrees(
                    (float)group.RotationDeg,
                    (float)group.BoundsMm.CenterX,
                    (float)group.BoundsMm.CenterY);
                nextTransform = SKMatrix.Concat(parentTransform, groupRot);
            }

            foreach (SceneNode child in group.Children)
            {
                AppendNodeContours(accumulated, child, page, nextTransform);
            }

            return;
        }

        if (node.BoundsMm.Width <= 0.0 || node.BoundsMm.Height <= 0.0)
        {
            return;
        }

        using SKPath? contour = BuildLeafContourPath(node, page);
        if (contour is null || contour.IsEmpty)
        {
            return;
        }

        if (Math.Abs(node.RotationDeg) > 0.0001)
        {
            SKMatrix localRot = SKMatrix.CreateRotationDegrees(
                (float)node.RotationDeg,
                (float)node.BoundsMm.CenterX,
                (float)node.BoundsMm.CenterY);
            contour.Transform(localRot);
        }

        if (!parentTransform.IsIdentity)
        {
            contour.Transform(parentTransform);
        }

        UnionContour(accumulated, contour);
    }

    private static SKPath? BuildLeafContourPath(SceneNode node, PageModel page)
    {
        switch (node)
        {
            case StickerNode sticker:
                {
                    SKRect rect = ToSkRect(sticker.BoundsMm);
                    var path = new SKPath();
                    if (string.Equals(sticker.TemplateId, "commercial-seal", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sticker.TemplateId, "warranty-badge", StringComparison.OrdinalIgnoreCase))
                    {
                        path.AddOval(rect);
                    }
                    else
                    {
                        float radius = Math.Min(rect.Width, rect.Height) * 0.08f;
                        using var rr = new SKRoundRect(rect, radius, radius);
                        path.AddRoundRect(rr);
                    }

                    return path;
                }

            case ShapeNode shape:
                return shape.Kind is ShapeKind.Line or ShapeKind.Arc
                    ? null
                    : MaskPathBuilder.BuildShapePath(shape);

            case ImageNode image:
                {
                    RectMm b = image.BoundsMm;
                    if (b.X <= 0.0 && b.Y <= 0.0 && b.Right >= page.WidthMm && b.Bottom >= page.HeightMm)
                    {
                        return null;
                    }

                    return MaskPathBuilder.BuildMaskPath(image.Mask, ToSkRect(b), image.CornerRadiusMm);
                }

            case SvgNode svg:
                {
                    var path = new SKPath();
                    path.AddRect(ToSkRect(svg.BoundsMm));
                    return path;
                }

            default:
                return null;
        }
    }

    private static void UnionContour(SKPath accumulated, SKPath contour)
    {
        if (contour.IsEmpty)
        {
            return;
        }

        if (accumulated.IsEmpty)
        {
            accumulated.AddPath(contour);
            return;
        }

        using SKPath? unioned = accumulated.Op(contour, SKPathOp.Union);
        if (unioned is not null)
        {
            accumulated.Reset();
            accumulated.AddPath(unioned);
        }
        else
        {
            accumulated.AddPath(contour);
        }
    }

    private static SKRect ToSkRect(RectMm rect) =>
        SKRect.Create((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
}
