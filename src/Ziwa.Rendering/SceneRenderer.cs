using SkiaSharp;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Filters;
using Ziwa.Rendering.Geometry;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering;

public enum RenderQualityMode
{
    InteractivePreview,
    PrintFullResolution,
}

public sealed record RenderOptions(
    double Dpi = 96.0,
    RenderQualityMode QualityMode = RenderQualityMode.InteractivePreview,
    bool IsPreScaledToMillimeters = false,
    bool IncludeBleed = false);

public sealed class SceneRenderer
{
    private readonly IRenderAssetProvider _assetProvider;
    private readonly HarfBuzzArabicShaper _shaper;
    private readonly TextOnCurveRenderer _curveRenderer;

    public SceneRenderer(
        IRenderAssetProvider assetProvider,
        HarfBuzzArabicShaper shaper,
        TextOnCurveRenderer curveRenderer)
    {
        _assetProvider = assetProvider;
        _shaper = shaper;
        _curveRenderer = curveRenderer;
    }

    public SceneRenderer(IRenderAssetProvider assetProvider, HarfBuzzArabicShaper shaper)
        : this(assetProvider, shaper, new TextOnCurveRenderer(new FontRegistry(), shaper))
    {
    }

    public void RenderPage(SKCanvas canvas, PageModel page, RenderOptions options)
    {
        int rootSave = canvas.Save();
        try
        {
            if (!options.IsPreScaledToMillimeters)
            {
                float pxPerMm = (float)DomainMath.PixelsPerMm(options.Dpi);
                canvas.Scale(pxPerMm, pxPerMm);
            }

            double bleed = options.IncludeBleed ? page.BleedMm : 0.0;
            if (bleed > 0.0)
            {
                canvas.Translate((float)bleed, (float)bleed);
            }

            var pageClipRect = SKRect.Create(
                (float)-bleed,
                (float)-bleed,
                (float)(page.WidthMm + (bleed * 2.0)),
                (float)(page.HeightMm + (bleed * 2.0)));
            canvas.ClipRect(pageClipRect);

            DrawBackground(canvas, page.Background, pageClipRect);

            if (page.CollageLayout is { Slots.IsDefaultOrEmpty: false } collage)
            {
                foreach (CollageSlot slot in collage.Slots)
                {
                    DrawCollageSlot(canvas, slot, options.QualityMode, options.Dpi);
                }
            }

            if (!page.Nodes.IsDefault)
            {
                foreach (SceneNode node in page.Nodes)
                {
                    if (!node.IsVisible || node.Opacity <= 0.0)
                    {
                        continue;
                    }

                    RenderNode(canvas, node, options.QualityMode, options.Dpi);
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(rootSave);
        }
    }

    private void RenderNode(SKCanvas canvas, SceneNode node, RenderQualityMode qualityMode, double dpi)
    {
        int nodeSave = canvas.Save();
        try
        {
            if (node.Opacity < 0.999)
            {
                byte alpha = (byte)Math.Clamp((int)Math.Round(node.Opacity * 255.0), 0, 255);
                using var layerPaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
                canvas.SaveLayer(layerPaint);
            }

            if (Math.Abs(node.RotationDeg) > 0.0001)
            {
                canvas.RotateDegrees(
                    (float)node.RotationDeg,
                    (float)node.BoundsMm.CenterX,
                    (float)node.BoundsMm.CenterY);
            }

            switch (node)
            {
                case ShapeNode shape:
                    DrawShapeNode(canvas, shape, qualityMode, dpi);
                    break;
                case ImageNode image:
                    DrawImageNode(canvas, image, qualityMode, dpi);
                    break;
                case TextNode text:
                    DrawTextNode(canvas, text);
                    break;
                case SvgNode svg:
                    DrawSvgPicture(canvas, _assetProvider.AcquireSvgPicture(svg.SvgAssetId), svg.BoundsMm, svg.TintColor, svg.Shadow);
                    break;
                case StickerNode sticker:
                    string svgXml = ParametricStickerCatalog.BuildSvgXml(sticker);
                    DrawSvgPicture(canvas, _assetProvider.GetOrBuildSvgPicture(svgXml), sticker.BoundsMm, sticker.SvgTintColor, sticker.Shadow);
                    break;
                case GroupNode group:
                    if (!group.Children.IsDefault)
                    {
                        foreach (SceneNode child in group.Children)
                        {
                            if (child.IsVisible && child.Opacity > 0.0)
                            {
                                RenderNode(canvas, child, qualityMode, dpi);
                            }
                        }
                    }

                    break;
            }
        }
        finally
        {
            canvas.RestoreToCount(nodeSave);
        }
    }

    private void DrawCollageSlot(SKCanvas canvas, CollageSlot slot, RenderQualityMode qualityMode, double dpi)
    {
        if (slot.AssignedAssetId is not { IsEmpty: false } assetId)
        {
            return;
        }

        SKRect destRect = ToSkRect(slot.BoundsMm);
        using SKPath maskPath = MaskPathBuilder.BuildMaskPath(slot.Mask, destRect, slot.CornerRadiusMm);
        using AssetImageLease lease = _assetProvider.AcquireImage(assetId, qualityMode);

        if (lease.Image is not null)
        {
            bool swapsSlotAxes = slot.QuarterTurns is 1 or 3;
            double logicalSlotW = swapsSlotAxes ? slot.BoundsMm.Height : slot.BoundsMm.Width;
            double logicalSlotH = swapsSlotAxes ? slot.BoundsMm.Width : slot.BoundsMm.Height;

            SKRect srcRect = CropViewportMath.ComputeCoverSourceRect(
                lease.Image.Width,
                lease.Image.Height,
                logicalSlotW,
                logicalSlotH,
                slot.EffectiveCrop);

            using ImageFilterPipeline filtered = ImageFilterPipeline.Prepare(
                lease.Image,
                srcRect,
                logicalSlotW,
                logicalSlotH,
                dpi,
                slot.EffectiveFilters);

            int clipSave = canvas.Save();
            try
            {
                canvas.ClipPath(maskPath, SKClipOperation.Intersect, antialias: true);

                float cx = destRect.MidX;
                float cy = destRect.MidY;
                canvas.Translate(cx, cy);
                if (slot.FlipHorizontal || slot.FlipVertical)
                {
                    canvas.Scale(slot.FlipHorizontal ? -1f : 1f, slot.FlipVertical ? -1f : 1f);
                }

                if (slot.QuarterTurns != 0)
                {
                    canvas.RotateDegrees(slot.QuarterTurns * 90f);
                }

                var drawRect = SKRect.Create(
                    -(float)(logicalSlotW * 0.5),
                    -(float)(logicalSlotH * 0.5),
                    (float)logicalSlotW,
                    (float)logicalSlotH);

                using var paint = new SKPaint
                {
                    IsAntialias = filtered.FilterQuality != SKFilterQuality.None,
                    FilterQuality = filtered.FilterQuality,
                    ColorFilter = filtered.ColorFilter,
                    ImageFilter = filtered.ImageFilter,
                };
                canvas.DrawImage(filtered.Image, filtered.SourceRect, drawRect, paint);
            }
            finally
            {
                canvas.RestoreToCount(clipSave);
            }
        }

        if (slot.Border is { WidthMm: > 0.0 } border && border.Color.A > 0)
        {
            using var borderPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = ToSkColor(border.Color),
                StrokeWidth = (float)border.WidthMm,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(maskPath, borderPaint);
        }
    }

    private static void DrawBackground(SKCanvas canvas, BackgroundSpec background, SKRect rect)
    {
        switch (background)
        {
            case TransparentBackground:
                break;

            case SolidBackground solid:
                {
                    using var paint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = ToSkColor(solid.Color),
                        IsAntialias = false,
                    };
                    canvas.DrawRect(rect, paint);
                    break;
                }

            case LinearGradientBackground linear:
                {
                    using SKShader shader = CreateLinearGradientShader(rect, linear.StartColor, linear.EndColor, linear.AngleDeg);
                    using var paint = new SKPaint { Style = SKPaintStyle.Fill, Shader = shader };
                    canvas.DrawRect(rect, paint);
                    break;
                }

            case RadialGradientBackground radial:
                {
                    float radius = Math.Max(rect.Width, rect.Height) * 0.5f;
                    using var shader = SKShader.CreateRadialGradient(
                        new SKPoint(rect.MidX, rect.MidY),
                        Math.Max(1f, radius),
                        [ToSkColor(radial.CenterColor), ToSkColor(radial.EdgeColor)],
                        SKShaderTileMode.Clamp);
                    using var paint = new SKPaint { Style = SKPaintStyle.Fill, Shader = shader };
                    canvas.DrawRect(rect, paint);
                    break;
                }
        }
    }

    private void DrawShapeNode(SKCanvas canvas, ShapeNode shape, RenderQualityMode qualityMode, double dpi)
    {
        SKRect rect = ToSkRect(shape.BoundsMm);
        using SKPath path = MaskPathBuilder.BuildShapePath(shape);
        bool isOpenStrokeShape = shape.Kind is ShapeKind.Line or ShapeKind.Arc;

        if (!isOpenStrokeShape && shape.Shadow is { Color.A: > 0 } shadow)
        {
            DrawOuterDropShadow(canvas, path, shadow);
        }

        if (!isOpenStrokeShape)
        {
            switch (shape.Fill)
            {
                case SolidShapeFill solid when solid.Color.A > 0:
                    {
                        using var fillPaint = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Color = ToSkColor(solid.Color),
                            IsAntialias = true,
                        };
                        canvas.DrawPath(path, fillPaint);
                        break;
                    }

                case LinearGradientShapeFill linear:
                    {
                        using SKShader shader = CreateLinearGradientShader(rect, linear.StartColor, linear.EndColor, linear.AngleDeg);
                        using var fillPaint = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Shader = shader,
                            IsAntialias = true,
                        };
                        canvas.DrawPath(path, fillPaint);
                        break;
                    }

                case ImageShapeFill imgFill:
                    {
                        using AssetImageLease lease = _assetProvider.AcquireImage(imgFill.CurrentAssetId, qualityMode);
                        if (lease.Image is not null)
                        {
                            SKRect srcRect = CropViewportMath.ComputeCoverSourceRect(
                                lease.Image.Width,
                                lease.Image.Height,
                                shape.BoundsMm.Width,
                                shape.BoundsMm.Height,
                                imgFill.Crop);

                            using ImageFilterPipeline filtered = ImageFilterPipeline.Prepare(
                                lease.Image,
                                srcRect,
                                shape.BoundsMm.Width,
                                shape.BoundsMm.Height,
                                dpi,
                                ImageFilterSpec.None);

                            int clipSave = canvas.Save();
                            try
                            {
                                canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
                                using var imgPaint = new SKPaint
                                {
                                    IsAntialias = true,
                                    FilterQuality = filtered.FilterQuality,
                                };
                                canvas.DrawImage(filtered.Image, filtered.SourceRect, rect, imgPaint);
                            }
                            finally
                            {
                                canvas.RestoreToCount(clipSave);
                            }
                        }

                        break;
                    }
            }
        }

        if (shape.Stroke is { WidthMm: > 0.0 } stroke && stroke.Color.A > 0)
        {
            using var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = ToSkColor(stroke.Color),
                StrokeWidth = (float)stroke.WidthMm,
                StrokeCap = isOpenStrokeShape ? SKStrokeCap.Round : SKStrokeCap.Butt,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(path, strokePaint);
        }
    }

    private void DrawImageNode(SKCanvas canvas, ImageNode imageNode, RenderQualityMode qualityMode, double dpi)
    {
        SKRect destRect = ToSkRect(imageNode.BoundsMm);

        // Sacred pipeline order:
        // 1) Pixel & Color filters -> 2) Geometric mask clip + CropViewportMath ->
        // 3) Border stroke -> 4) Single unclipped outer DropShadow
        using SKPath maskPath = MaskPathBuilder.BuildMaskPath(imageNode.Mask, destRect, imageNode.CornerRadiusMm);

        using AssetImageLease lease = _assetProvider.AcquireImage(imageNode.CurrentAssetId, qualityMode);
        if (lease.Image is not null)
        {
            SKRect srcRect = CropViewportMath.ComputeCoverSourceRect(
                lease.Image.Width,
                lease.Image.Height,
                imageNode.BoundsMm.Width,
                imageNode.BoundsMm.Height,
                imageNode.Crop);

            using ImageFilterPipeline filtered = ImageFilterPipeline.Prepare(
                lease.Image,
                srcRect,
                imageNode.BoundsMm.Width,
                imageNode.BoundsMm.Height,
                dpi,
                imageNode.EffectiveFilters);

            int clipSave = canvas.Save();
            try
            {
                canvas.ClipPath(maskPath, SKClipOperation.Intersect, antialias: true);
                using var imagePaint = new SKPaint
                {
                    IsAntialias = filtered.FilterQuality != SKFilterQuality.None,
                    FilterQuality = filtered.FilterQuality,
                    ColorFilter = filtered.ColorFilter,
                    ImageFilter = filtered.ImageFilter,
                };
                canvas.DrawImage(filtered.Image, filtered.SourceRect, destRect, imagePaint);
            }
            finally
            {
                canvas.RestoreToCount(clipSave);
            }
        }

        if (imageNode.Border is { WidthMm: > 0.0 } border && border.Color.A > 0)
        {
            using var borderPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = ToSkColor(border.Color),
                StrokeWidth = (float)border.WidthMm,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(maskPath, borderPaint);
        }

        if (imageNode.Shadow is { Color.A: > 0 } shadow)
        {
            DrawOuterDropShadow(canvas, maskPath, shadow);
        }
    }

    private static void DrawSvgPicture(
        SKCanvas canvas,
        SKPicture? picture,
        RectMm boundsMm,
        ColorRgba? tintColor,
        ShadowSpec? shadow)
    {
        if (picture is null)
        {
            return;
        }

        SKRect cull = picture.CullRect;
        if (cull.Width <= 0f || cull.Height <= 0f)
        {
            return;
        }

        SKRect dest = ToSkRect(boundsMm);
        if (shadow is { Color.A: > 0 } s)
        {
            using var shadowPath = new SKPath();
            shadowPath.AddRect(dest);
            DrawOuterDropShadow(canvas, shadowPath, s);
        }

        int save = canvas.Save();
        try
        {
            if (tintColor is ColorRgba tint)
            {
                using SKColorFilter tintFilter = SKColorFilter.CreateBlendMode(ToSkColor(tint), SKBlendMode.SrcIn);
                using var layerPaint = new SKPaint { ColorFilter = tintFilter };
                canvas.SaveLayer(dest, layerPaint);
            }

            float scaleX = dest.Width / cull.Width;
            float scaleY = dest.Height / cull.Height;
            canvas.Translate(dest.Left, dest.Top);
            canvas.Scale(scaleX, scaleY);
            canvas.Translate(-cull.Left, -cull.Top);
            canvas.DrawPicture(picture);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    private void DrawTextNode(SKCanvas canvas, TextNode textNode)
    {
        if (string.IsNullOrWhiteSpace(textNode.Text))
        {
            return;
        }

        using var textPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = ToSkColor(textNode.Color),
            IsAntialias = true,
        };

        if (textNode.Curve is { Kind: not TextCurveKind.None })
        {
            _curveRenderer.DrawCurvedText(canvas, textNode, textPaint);
            return;
        }

        double fontSizeMm = DomainMath.PtToMm(textNode.FontSizePt);
        string[] lines = textNode.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        float lineStepMm = (float)(fontSizeMm * textNode.LineHeight);

        var lineBlobs = new List<(SKTextBlob Blob, float WidthMm, float AscentMm, float DescentMm)>(lines.Length);
        float maxLineWidth = 0f;
        float firstAscent = 0f;
        float lastDescent = 0f;

        float currentTop = (float)textNode.BoundsMm.Y;
        foreach (string line in lines)
        {
            ShapedLineResult shaped = _shaper.ShapeLine(
                line,
                textNode.FontFamily,
                textNode.FontWeight,
                fontSizeMm,
                textNode.Direction,
                textNode.LetterSpacingEm);

            if (lineBlobs.Count == 0)
            {
                firstAscent = shaped.AscentMm;
            }

            lastDescent = shaped.DescentMm;
            maxLineWidth = Math.Max(maxLineWidth, shaped.TotalAdvanceMm);

            float baselineY = currentTop + shaped.AscentMm;
            float originX = textNode.Alignment switch
            {
                TextAlignmentKind.Right => (float)textNode.BoundsMm.Right - shaped.TotalAdvanceMm,
                TextAlignmentKind.Center => (float)textNode.BoundsMm.X + (((float)textNode.BoundsMm.Width - shaped.TotalAdvanceMm) * 0.5f),
                _ => (float)textNode.BoundsMm.X,
            };

            SKTextBlob? blob = _shaper.BuildTextBlob(
                shaped,
                textNode.FontFamily,
                textNode.FontWeight,
                fontSizeMm,
                originX,
                baselineY);

            if (blob is not null)
            {
                lineBlobs.Add((blob, shaped.TotalAdvanceMm, shaped.AscentMm, shaped.DescentMm));
            }

            currentTop += lineStepMm;
        }

        try
        {
            TextEffectSpec effects = textNode.EffectiveEffects;

            if (effects.PillBackgroundColor is { A: > 0 } pillBg && lineBlobs.Count > 0)
            {
                float pad = (float)effects.PillPaddingMm;
                float pillWidth = maxLineWidth + (pad * 2f);
                float pillX = textNode.Alignment switch
                {
                    TextAlignmentKind.Right => (float)textNode.BoundsMm.Right - maxLineWidth - pad,
                    TextAlignmentKind.Center => (float)textNode.BoundsMm.X + (((float)textNode.BoundsMm.Width - maxLineWidth) * 0.5f) - pad,
                    _ => (float)textNode.BoundsMm.X - pad,
                };
                float totalTextHeight = ((lines.Length - 1) * lineStepMm) + firstAscent + lastDescent;
                var pillRect = SKRect.Create(pillX, (float)textNode.BoundsMm.Y - pad, pillWidth, totalTextHeight + (pad * 2f));
                float radius = (float)effects.PillCornerRadiusMm;
                using var pillPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = ToSkColor(pillBg),
                    IsAntialias = true,
                };
                canvas.DrawRoundRect(pillRect, radius, radius, pillPaint);
            }

            if (effects.Shadow is { Color.A: > 0 } shadow)
            {
                float sigma = (float)Math.Max(0.0, shadow.BlurRadiusMm * 0.5);
                using SKImageFilter? blur = sigma > 0.001f ? SKImageFilter.CreateBlur(sigma, sigma) : null;
                using var shadowPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = ToSkColor(shadow.Color),
                    IsAntialias = true,
                    ImageFilter = blur,
                };
                foreach ((SKTextBlob blob, _, _, _) in lineBlobs)
                {
                    canvas.DrawText(blob, (float)shadow.OffsetXMm, (float)shadow.OffsetYMm, shadowPaint);
                }
            }

            if (effects.Outline is { WidthMm: > 0.0 } outline && outline.Color.A > 0)
            {
                using var outlinePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = ToSkColor(outline.Color),
                    StrokeWidth = (float)(outline.WidthMm * 2.0),
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true,
                };
                foreach ((SKTextBlob blob, _, _, _) in lineBlobs)
                {
                    canvas.DrawText(blob, 0f, 0f, outlinePaint);
                }
            }

            foreach ((SKTextBlob blob, _, _, _) in lineBlobs)
            {
                canvas.DrawText(blob, 0f, 0f, textPaint);
            }
        }
        finally
        {
            foreach ((SKTextBlob blob, _, _, _) in lineBlobs)
            {
                blob.Dispose();
            }
        }
    }

    private static SKShader CreateLinearGradientShader(SKRect rect, ColorRgba start, ColorRgba end, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        float cx = rect.MidX;
        float cy = rect.MidY;
        float dx = (float)(Math.Cos(rad) * rect.Width * 0.5);
        float dy = (float)(Math.Sin(rad) * rect.Height * 0.5);
        return SKShader.CreateLinearGradient(
            new SKPoint(cx - dx, cy - dy),
            new SKPoint(cx + dx, cy + dy),
            [ToSkColor(start), ToSkColor(end)],
            SKShaderTileMode.Clamp);
    }

    private static void DrawOuterDropShadow(SKCanvas canvas, SKPath subjectPath, ShadowSpec shadow)
    {
        if (shadow.Color.A == 0)
        {
            return;
        }

        float sigma = (float)Math.Max(0.0, shadow.BlurRadiusMm * 0.5);
        using SKImageFilter? blur = sigma > 0.001f ? SKImageFilter.CreateBlur(sigma, sigma) : null;
        using var shadowPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = ToSkColor(shadow.Color),
            IsAntialias = true,
            ImageFilter = blur,
        };

        int save = canvas.Save();
        try
        {
            canvas.ClipPath(subjectPath, SKClipOperation.Difference, antialias: true);
            canvas.Translate((float)shadow.OffsetXMm, (float)shadow.OffsetYMm);
            canvas.DrawPath(subjectPath, shadowPaint);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    private static SKRect ToSkRect(RectMm r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    private static SKColor ToSkColor(ColorRgba c) => new(c.R, c.G, c.B, c.A);
}
