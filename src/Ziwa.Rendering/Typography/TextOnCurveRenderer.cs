using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Rendering.Typography;

public sealed class TextOnCurveRenderer
{
    private readonly FontRegistry _fontRegistry;
    private readonly HarfBuzzArabicShaper _shaper;

    public TextOnCurveRenderer(FontRegistry fontRegistry, HarfBuzzArabicShaper shaper)
    {
        _fontRegistry = fontRegistry;
        _shaper = shaper;
    }

    public void DrawCurvedText(SKCanvas canvas, TextNode textNode, SKPaint fillPaint)
    {
        if (textNode.Curve is not { Kind: not TextCurveKind.None } curve ||
            string.IsNullOrWhiteSpace(textNode.Text))
        {
            return;
        }

        string singleLine = textNode.Text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();
        if (singleLine.Length == 0)
        {
            return;
        }

        double fontSizeMm = DomainMath.PtToMm(textNode.FontSizePt);
        ShapedLineResult shaped = _shaper.ShapeLine(
            singleLine,
            textNode.FontFamily,
            textNode.FontWeight,
            fontSizeMm,
            textNode.Direction,
            textNode.LetterSpacingEm);

        if (shaped.Glyphs.IsDefaultOrEmpty || shaped.TotalAdvanceMm <= 0f)
        {
            return;
        }

        FontRegistry.FontEntry fontEntry = _fontRegistry.ResolveFont(textNode.FontFamily, textNode.FontWeight);
        using var skFont = new SKFont(fontEntry.Typeface, (float)fontSizeMm)
        {
            LinearMetrics = true,
            Subpixel = true,
            Hinting = SKFontHinting.None,
            Edging = SKFontEdging.Antialias,
        };

        float outlineWidth = (float)(textNode.EffectiveEffects.Outline?.WidthMm ?? 0.0);
        float margin = Math.Max(1.0f, shaped.AscentMm + shaped.DescentMm + outlineWidth);

        using SKPath guidePath = BuildGuidePath(textNode.BoundsMm, curve, margin);
        using var measure = new SKPathMeasure(guidePath, forceClosed: curve.Kind == TextCurveKind.FullCircle);
        float pathLength = measure.Length;
        if (pathLength <= 0.1f)
        {
            return;
        }

        float startDistance = (pathLength - shaped.TotalAdvanceMm) * 0.5f;
        List<GlyphCluster> clusters = GroupByCluster(shaped.Glyphs);

        using var combinedPath = new SKPath();
        foreach (GlyphCluster cluster in clusters)
        {
            float dist = startDistance + cluster.AnchorXMm;
            if (curve.Kind == TextCurveKind.FullCircle)
            {
                dist = ((dist % pathLength) + pathLength) % pathLength;
            }
            else
            {
                dist = Math.Clamp(dist, 0f, pathLength);
            }

            if (!measure.GetPositionAndTangent(dist, out SKPoint pos, out SKPoint tangent))
            {
                continue;
            }

            // Rigid local-to-curve transform keeping glyphs tangent and upright
            var matrix = new SKMatrix(
                tangent.X, -tangent.Y, pos.X,
                tangent.Y, tangent.X, pos.Y,
                0f, 0f, 1f);

            foreach (ShapedGlyph glyph in cluster.Glyphs)
            {
                using SKPath? glyphPath = skFont.GetGlyphPath(glyph.GlyphId);
                if (glyphPath is null || glyphPath.IsEmpty)
                {
                    continue;
                }

                float localX = glyph.XMm - cluster.AnchorXMm;
                float localY = -glyph.YOffsetMm;
                SKMatrix offset = SKMatrix.CreateTranslation(localX, localY);
                SKMatrix finalMatrix = matrix.PreConcat(offset);

                using var transformed = new SKPath();
                glyphPath.Transform(finalMatrix, transformed);
                combinedPath.AddPath(transformed);
            }
        }

        if (combinedPath.IsEmpty)
        {
            return;
        }

        TextEffectSpec effects = textNode.EffectiveEffects;
        if (effects.Shadow is { Color.A: > 0 } shadow)
        {
            float sigma = (float)Math.Max(0.0, shadow.BlurRadiusMm * 0.5);
            using SKImageFilter? blur = sigma > 0.001f ? SKImageFilter.CreateBlur(sigma, sigma) : null;
            using var shadowPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(shadow.Color.R, shadow.Color.G, shadow.Color.B, shadow.Color.A),
                IsAntialias = true,
                ImageFilter = blur,
            };

            int save = canvas.Save();
            try
            {
                canvas.Translate((float)shadow.OffsetXMm, (float)shadow.OffsetYMm);
                canvas.DrawPath(combinedPath, shadowPaint);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }

        if (effects.Outline is { WidthMm: > 0.0 } outline && outline.Color.A > 0)
        {
            using var outlinePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(outline.Color.R, outline.Color.G, outline.Color.B, outline.Color.A),
                StrokeWidth = (float)(outline.WidthMm * 2.0),
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(combinedPath, outlinePaint);
        }

        canvas.DrawPath(combinedPath, fillPaint);
    }

    private static SKPath BuildGuidePath(RectMm bounds, TextCurveSpec curve, float insetMm)
    {
        var path = new SKPath();
        float x = (float)bounds.X + insetMm;
        float y = (float)bounds.Y + insetMm;
        float w = Math.Max(4f, (float)bounds.Width - (insetMm * 2f));
        float h = Math.Max(4f, (float)bounds.Height - (insetMm * 2f));
        var rect = SKRect.Create(x, y, w, h);

        float bend = (float)DomainMath.Clamp(curve.BendFactor, 0.2, 1.0);
        float spanDeg = 170f * bend;

        switch (curve.Kind)
        {
            case TextCurveKind.ArcUp:
                {
                    float startDeg = 270f - (spanDeg * 0.5f);
                    path.AddArc(rect, startDeg, spanDeg);
                    return path;
                }

            case TextCurveKind.ArcDown:
                {
                    // Sweep counter-clockwise along the bottom (180 -> 90 -> 0) so tangent points +X (upright)
                    float startDeg = 90f + (spanDeg * 0.5f);
                    path.AddArc(rect, startDeg, -spanDeg);
                    return path;
                }

            case TextCurveKind.FullCircle:
                {
                    float diameter = Math.Min(w, h);
                    float cx = rect.MidX;
                    float cy = rect.MidY;
                    var circleRect = SKRect.Create(cx - (diameter * 0.5f), cy - (diameter * 0.5f), diameter, diameter);
                    // Start at bottom (90 deg) so midpoint (50% length) sits squarely at top (270 deg)
                    path.AddArc(circleRect, 90f, 359.8f);
                    return path;
                }

            default:
                path.MoveTo(rect.Left, rect.MidY);
                path.LineTo(rect.Right, rect.MidY);
                return path;
        }
    }

    private static List<GlyphCluster> GroupByCluster(IReadOnlyList<ShapedGlyph> glyphs)
    {
        var result = new List<GlyphCluster>();
        if (glyphs.Count == 0)
        {
            return result;
        }

        var current = new List<ShapedGlyph> { glyphs[0] };
        uint currentCluster = glyphs[0].Cluster;

        for (int i = 1; i < glyphs.Count; i++)
        {
            if (glyphs[i].Cluster == currentCluster)
            {
                current.Add(glyphs[i]);
            }
            else
            {
                result.Add(CreateCluster(current));
                current = [glyphs[i]];
                currentCluster = glyphs[i].Cluster;
            }
        }

        result.Add(CreateCluster(current));
        return result;
    }

    private static GlyphCluster CreateCluster(List<ShapedGlyph> glyphs)
    {
        float anchorX = glyphs[0].XMm;
        foreach (ShapedGlyph g in glyphs)
        {
            if (g.AdvanceMm > 0f)
            {
                anchorX = g.XMm + (g.AdvanceMm * 0.5f);
                break;
            }
        }

        return new GlyphCluster(anchorX, glyphs);
    }

    private sealed record GlyphCluster(float AnchorXMm, List<ShapedGlyph> Glyphs);
}
