using System.Collections.Immutable;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Validation;

public sealed record ValidationResult(bool IsValid, ImmutableArray<string> Errors);

public static class SchemaValidator
{
    public const double MaxPageDimensionMm = 5000.0;
    public const double MaxBleedMm = 25.0;
    public const double MinDpi = 72.0;
    public const double MaxDpi = 2400.0;
    public const int MaxGroupDepth = 16;
    public const int MaxPages = 256;
    public const int MaxNodes = 100000;

    public static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0.0;

    public static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0.0;

    public static void AssertFinitePositive(double value, string paramName)
    {
        if (!IsFinitePositive(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be a finite number greater than zero.");
        }
    }

    public static ValidationResult ValidateDocument(ZiwaDocument? document)
    {
        if (document is null)
        {
            return new ValidationResult(false, ["Document cannot be null."]);
        }

        var errors = ImmutableArray.CreateBuilder<string>();

        if (document.Id.IsEmpty)
        {
            errors.Add("Document.Id cannot be empty.");
        }

        if (document.SchemaVersion < 1)
        {
            errors.Add("Document.SchemaVersion must be >= 1.");
        }

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            errors.Add("Document.Title cannot be empty.");
        }

        if (document.Pages.IsDefaultOrEmpty)
        {
            errors.Add("Document must contain at least one page.");
        }
        else if (document.ActivePageIndex < 0 || document.ActivePageIndex >= document.Pages.Length)
        {
            errors.Add("Document.ActivePageIndex is out of range.");
        }

        ValidatePrintSettings(document.PrintSettings, errors);
        if (!Enum.IsDefined(document.Mode)) { errors.Add("Document.Mode is unsupported."); }
        if (!document.Pages.IsDefault && document.Pages.Length > MaxPages) { errors.Add("Document contains too many pages."); }

        if (!document.Pages.IsDefault)
        {
            for (int i = 0; i < document.Pages.Length; i++)
            {
                ValidatePage(document.Pages[i], $"Pages[{i}]", errors);
            }
        }

        return new ValidationResult(errors.Count == 0, errors.ToImmutable());
    }

    public static ValidationResult ValidateNodes(ImmutableArray<SceneNode> nodes)
    {
        if (nodes.IsDefaultOrEmpty)
        {
            return new ValidationResult(false, ["Node collection cannot be empty."]);
        }

        var errors = ImmutableArray.CreateBuilder<string>();
        for (int i = 0; i < nodes.Length; i++)
        {
            ValidateNode(nodes[i], $"Nodes[{i}]", errors, depth: 0);
        }

        return new ValidationResult(errors.Count == 0, errors.ToImmutable());
    }

    private static void ValidatePrintSettings(PrintTicketSpec? settings, ImmutableArray<string>.Builder errors)
    {
        if (settings is null)
        {
            errors.Add("PrintSettings cannot be null.");
            return;
        }

        if (!Enum.IsDefined(settings.CmykProfile)) { errors.Add("PrintSettings.CmykProfile is unsupported."); }
        if (!IsFinitePositive(settings.ExportDpi) || settings.ExportDpi < MinDpi || settings.ExportDpi > MaxDpi)
        {
            errors.Add($"PrintSettings.ExportDpi must be finite within [{MinDpi}, {MaxDpi}].");
        }
    }

    private static void ValidatePage(PageModel? page, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (page is null)
        {
            errors.Add($"{prefix}: page cannot be null.");
            return;
        }

        if (page.Id.IsEmpty)
        {
            errors.Add($"{prefix}.Id cannot be empty.");
        }

        if (!IsFinitePositive(page.WidthMm) || page.WidthMm > MaxPageDimensionMm)
        {
            errors.Add($"{prefix}.WidthMm must be finite within (0, {MaxPageDimensionMm}].");
        }

        if (!IsFinitePositive(page.HeightMm) || page.HeightMm > MaxPageDimensionMm)
        {
            errors.Add($"{prefix}.HeightMm must be finite within (0, {MaxPageDimensionMm}].");
        }

        if (!IsFiniteNonNegative(page.BleedMm) || page.BleedMm > MaxBleedMm)
        {
            errors.Add($"{prefix}.BleedMm must be finite within [0, {MaxBleedMm}].");
        }

        if (!IsFiniteNonNegative(page.SafeMarginMm))
        {
            errors.Add($"{prefix}.SafeMarginMm must be finite and >= 0.");
        }

        ValidateBackground(page.Background, $"{prefix}.Background", errors);
        ValidateCollageLayout(page.CollageLayout, page.WidthMm, page.HeightMm, $"{prefix}.CollageLayout", errors);

        if (page.Nodes.IsDefault)
        {
            errors.Add($"{prefix}.Nodes cannot be default.");
        }
        else if (page.Nodes.Length > MaxNodes)
        {
            errors.Add($"{prefix}.Nodes contains too many nodes.");
        }
        else
        {
            for (int i = 0; i < page.Nodes.Length; i++)
            {
                ValidateNode(page.Nodes[i], $"{prefix}.Nodes[{i}]", errors, depth: 0);
            }
        }
    }

    private static void ValidateBackground(BackgroundSpec? background, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (background is null)
        {
            errors.Add($"{prefix} cannot be null.");
            return;
        }

        if (background is not (SolidBackground or LinearGradientBackground or RadialGradientBackground or TransparentBackground))
        {
            errors.Add($"{prefix}: unsupported background type.");
        }

        if (background is LinearGradientBackground linear && !double.IsFinite(linear.AngleDeg))
        {
            errors.Add($"{prefix}.AngleDeg must be finite.");
        }
    }

    private static void ValidateNode(SceneNode? node, string prefix, ImmutableArray<string>.Builder errors, int depth)
    {
        if (depth > MaxGroupDepth)
        {
            errors.Add($"{prefix}: group nesting exceeds maximum depth ({MaxGroupDepth}).");
            return;
        }

        if (node is null)
        {
            errors.Add($"{prefix}: node cannot be null.");
            return;
        }

        if (node.Id.IsEmpty)
        {
            errors.Add($"{prefix}.Id cannot be empty.");
        }

        if (!double.IsFinite(node.BoundsMm.X) || !double.IsFinite(node.BoundsMm.Y))
        {
            errors.Add($"{prefix}.BoundsMm origin (X, Y) must be finite.");
        }

        if (!IsFinitePositive(node.BoundsMm.Width) || !IsFinitePositive(node.BoundsMm.Height))
        {
            errors.Add($"{prefix}.BoundsMm Width and Height must be finite and > 0.");
        }

        if (!double.IsFinite(node.RotationDeg))
        {
            errors.Add($"{prefix}.RotationDeg must be finite.");
        }

        if (!double.IsFinite(node.Opacity) || node.Opacity < 0.0 || node.Opacity > 1.0)
        {
            errors.Add($"{prefix}.Opacity must be finite within [0, 1].");
        }

        switch (node)
        {
            case ImageNode image:
                ValidateImageNode(image, prefix, errors);
                break;
            case ShapeNode shape:
                ValidateShapeNode(shape, prefix, errors);
                break;
            case TextNode text:
                ValidateTextNode(text, prefix, errors);
                break;
            case SvgNode svg:
                ValidateSvgNode(svg, prefix, errors);
                break;
            case StickerNode sticker:
                ValidateStickerNode(sticker, prefix, errors);
                break;
            case GroupNode group:
                ValidateGroupNode(group, prefix, errors, depth);
                break;
            default:
                errors.Add($"{prefix}: unsupported SceneNode subtype '{node.GetType().Name}'.");
                break;
        }
    }

    private static void ValidateImageNode(ImageNode image, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (image.CurrentAssetId.IsEmpty)
        {
            errors.Add($"{prefix}.CurrentAssetId cannot be empty.");
        }

        if (image.OriginalAssetId.IsEmpty)
        {
            errors.Add($"{prefix}.OriginalAssetId cannot be empty.");
        }

        if (!Enum.IsDefined(image.Mask)) { errors.Add($"{prefix}.Mask is unsupported."); }
        ValidateCrop(image.Crop, $"{prefix}.Crop", errors);

        if (!IsFiniteNonNegative(image.CornerRadiusMm))
        {
            errors.Add($"{prefix}.CornerRadiusMm must be finite and >= 0.");
        }

        ValidateFilters(image.Filters, $"{prefix}.Filters", errors);
        ValidateStroke(image.Border, $"{prefix}.Border", errors);
        ValidateShadow(image.Shadow, $"{prefix}.Shadow", errors);
    }

    private static void ValidateShapeNode(ShapeNode shape, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (!Enum.IsDefined(shape.Kind)) { errors.Add($"{prefix}.Kind is unsupported."); }
        if (shape.Fill is null)
        {
            errors.Add($"{prefix}.Fill cannot be null.");
        }
        else
        {
            switch (shape.Fill)
            {
                case LinearGradientShapeFill linear when !double.IsFinite(linear.AngleDeg):
                    errors.Add($"{prefix}.Fill.AngleDeg must be finite.");
                    break;
                case ImageShapeFill imgFill:
                    if (imgFill.CurrentAssetId.IsEmpty)
                    {
                        errors.Add($"{prefix}.Fill.CurrentAssetId cannot be empty.");
                    }

                    if (imgFill.OriginalAssetId.IsEmpty)
                    {
                        errors.Add($"{prefix}.Fill.OriginalAssetId cannot be empty.");
                    }

                    ValidateCrop(imgFill.Crop, $"{prefix}.Fill.Crop", errors);
                    break;
            }
        }

        if (!IsFiniteNonNegative(shape.Radii.TopLeft) ||
            !IsFiniteNonNegative(shape.Radii.TopRight) ||
            !IsFiniteNonNegative(shape.Radii.BottomRight) ||
            !IsFiniteNonNegative(shape.Radii.BottomLeft))
        {
            errors.Add($"{prefix}.Radii must contain finite values >= 0.");
        }

        if (shape.PolygonSides < 3 || shape.PolygonSides > 64)
        {
            errors.Add($"{prefix}.PolygonSides must be within [3, 64].");
        }

        if (!double.IsFinite(shape.InnerRadiusRatio) || shape.InnerRadiusRatio <= 0.05 || shape.InnerRadiusRatio >= 0.95)
        {
            errors.Add($"{prefix}.InnerRadiusRatio must be finite within (0.05, 0.95).");
        }

        if (!double.IsFinite(shape.StartAngleDeg) || !double.IsFinite(shape.SweepAngleDeg))
        {
            errors.Add($"{prefix} arc angles must be finite.");
        }

        ValidateStroke(shape.Stroke, $"{prefix}.Stroke", errors);
        ValidateShadow(shape.Shadow, $"{prefix}.Shadow", errors);
    }

    private static void ValidateTextNode(TextNode text, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (text.Text is null || !Enum.IsDefined(text.Alignment) || !Enum.IsDefined(text.Direction) ||
            (text.Curve is not null && !Enum.IsDefined(text.Curve.Kind)))
        {
            errors.Add($"{prefix}: invalid text content, alignment, direction or curve kind.");
        }

        if (string.IsNullOrWhiteSpace(text.FontFamily))
        {
            errors.Add($"{prefix}.FontFamily cannot be empty.");
        }

        if (!IsFinitePositive(text.FontSizePt))
        {
            errors.Add($"{prefix}.FontSizePt must be finite and > 0.");
        }

        if (text.FontWeight < 100 || text.FontWeight > 1000)
        {
            errors.Add($"{prefix}.FontWeight must be within [100, 1000].");
        }

        if (!IsFinitePositive(text.LineHeight))
        {
            errors.Add($"{prefix}.LineHeight must be finite and > 0.");
        }

        if (!double.IsFinite(text.LetterSpacingEm) || text.LetterSpacingEm < -0.2 || text.LetterSpacingEm > 2.0)
        {
            errors.Add($"{prefix}.LetterSpacingEm must be finite within [-0.2, 2.0].");
        }

        if (text.Curve is not null &&
            (!IsFinitePositive(text.Curve.BendFactor) || text.Curve.BendFactor < 0.05 || text.Curve.BendFactor > 2.0))
        {
            errors.Add($"{prefix}.Curve.BendFactor must be finite within [0.05, 2.0].");
        }

        if (text.Effects is not null)
        {
            ValidateStroke(text.Effects.Outline, $"{prefix}.Effects.Outline", errors);
            if (!IsFiniteNonNegative(text.Effects.PillPaddingMm))
            {
                errors.Add($"{prefix}.Effects.PillPaddingMm must be finite and >= 0.");
            }

            if (!IsFiniteNonNegative(text.Effects.PillCornerRadiusMm))
            {
                errors.Add($"{prefix}.Effects.PillCornerRadiusMm must be finite and >= 0.");
            }

            ValidateShadow(text.Effects.Shadow, $"{prefix}.Effects.Shadow", errors);
        }
    }

    private static void ValidateSvgNode(SvgNode svg, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (svg.SvgAssetId.IsEmpty)
        {
            errors.Add($"{prefix}.SvgAssetId cannot be empty.");
        }

        ValidateShadow(svg.Shadow, $"{prefix}.Shadow", errors);
    }

    private static void ValidateStickerNode(StickerNode sticker, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (string.IsNullOrWhiteSpace(sticker.TemplateId))
        {
            errors.Add($"{prefix}.TemplateId cannot be empty.");
        }

        if (sticker.Fields is null || sticker.Fields.Any(pair => pair.Value is null))
        {
            errors.Add($"{prefix}.Fields cannot be null.");
        }

        if (sticker.Palette is null)
        {
            errors.Add($"{prefix}.Palette cannot be null.");
        }

        ValidateShadow(sticker.Shadow, $"{prefix}.Shadow", errors);
    }

    private static void ValidateCollageLayout(
        CollageLayoutModel? layout,
        double pageWidthMm,
        double pageHeightMm,
        string prefix,
        ImmutableArray<string>.Builder errors)
    {
        if (layout is null)
        {
            return;
        }

        if (layout.Spec is null) { errors.Add($"{prefix}.Spec cannot be null."); }
        if (layout.Slots.IsDefault)
        {
            errors.Add($"{prefix}.Slots cannot be default.");
            return;
        }

        for (int i = 0; i < layout.Slots.Length; i++)
        {
            CollageSlot? slot = layout.Slots[i];
            string slotPrefix = $"{prefix}.Slots[{i}]";
            if (slot is null) { errors.Add($"{slotPrefix}: slot cannot be null."); continue; }
            if (!Enum.IsDefined(slot.Mask)) { errors.Add($"{slotPrefix}.Mask is unsupported."); }
            if (slot.Id.IsEmpty)
            {
                errors.Add($"{slotPrefix}.Id cannot be empty.");
            }

            if (!double.IsFinite(slot.BoundsMm.X) || !double.IsFinite(slot.BoundsMm.Y) ||
                !IsFinitePositive(slot.BoundsMm.Width) || !IsFinitePositive(slot.BoundsMm.Height) ||
                slot.BoundsMm.Width < PhysicalGridBuilder.MinCellSizeMm - 1e-6 ||
                slot.BoundsMm.Height < PhysicalGridBuilder.MinCellSizeMm - 1e-6)
            {
                errors.Add($"{slotPrefix}.BoundsMm must be finite and >= {PhysicalGridBuilder.MinCellSizeMm}mm.");
            }
            else if (double.IsFinite(pageWidthMm) && double.IsFinite(pageHeightMm) &&
                     (slot.BoundsMm.X < -1e-4 || slot.BoundsMm.Y < -1e-4 ||
                      slot.BoundsMm.Right > pageWidthMm + 1e-4 || slot.BoundsMm.Bottom > pageHeightMm + 1e-4))
            {
                errors.Add($"{slotPrefix}.BoundsMm must fit within page bounds.");
            }

            if (slot.Crop is not null)
            {
                ValidateCrop(slot.Crop, $"{slotPrefix}.Crop", errors);
            }

            if (slot.QuarterTurns < 0 || slot.QuarterTurns > 3)
            {
                errors.Add($"{slotPrefix}.QuarterTurns must be within [0, 3].");
            }

            if (!IsFiniteNonNegative(slot.CornerRadiusMm))
            {
                errors.Add($"{slotPrefix}.CornerRadiusMm must be finite and >= 0.");
            }

            ValidateFilters(slot.Filters, $"{slotPrefix}.Filters", errors);
            ValidateStroke(slot.Border, $"{slotPrefix}.Border", errors);
        }
    }

    private static void ValidateFilters(ImageFilterSpec? filters, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (filters is null)
        {
            return;
        }

        if (!Enum.IsDefined(filters.Preset)) { errors.Add($"{prefix}.Preset is unsupported."); }
        if (!Enum.IsDefined(filters.ScanFilter)) { errors.Add($"{prefix}.ScanFilter is unsupported."); }
        if (!double.IsFinite(filters.Brightness) || !double.IsFinite(filters.Contrast) ||
            !double.IsFinite(filters.Saturation) || !IsFiniteNonNegative(filters.BlurPx))
        {
            errors.Add($"{prefix} adjustment values must be finite.");
        }

        if (filters.LuminanceThreshold is double thr &&
            (!double.IsFinite(thr) || thr < 0.0 || thr > 1.0))
        {
            errors.Add($"{prefix}.LuminanceThreshold must be finite within [0, 1].");
        }

        if (filters.PixelateBlockSize < 1 || filters.PixelateBlockSize > 128)
        {
            errors.Add($"{prefix}.PixelateBlockSize must be within [1, 128].");
        }
    }

    private static void ValidateGroupNode(GroupNode group, string prefix, ImmutableArray<string>.Builder errors, int depth)
    {
        if (group.Children.IsDefaultOrEmpty)
        {
            errors.Add($"{prefix}.Children cannot be empty.");
            return;
        }

        for (int i = 0; i < group.Children.Length; i++)
        {
            ValidateNode(group.Children[i], $"{prefix}.Children[{i}]", errors, depth + 1);
        }
    }

    private static void ValidateCrop(CropSpec? crop, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (crop is null)
        {
            errors.Add($"{prefix} cannot be null.");
            return;
        }

        if (!IsFinitePositive(crop.Zoom) || crop.Zoom < 1.0)
        {
            errors.Add($"{prefix}.Zoom must be finite and >= 1.0.");
        }

        if (!double.IsFinite(crop.PanX) || !double.IsFinite(crop.PanY))
        {
            errors.Add($"{prefix} pan offsets must be finite.");
        }
    }

    private static void ValidateStroke(StrokeSpec? stroke, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (stroke is not null && !IsFiniteNonNegative(stroke.WidthMm))
        {
            errors.Add($"{prefix}.WidthMm must be finite and >= 0.");
        }
    }

    private static void ValidateShadow(ShadowSpec? shadow, string prefix, ImmutableArray<string>.Builder errors)
    {
        if (shadow is null)
        {
            return;
        }

        if (!double.IsFinite(shadow.OffsetXMm) || !double.IsFinite(shadow.OffsetYMm))
        {
            errors.Add($"{prefix} offsets must be finite.");
        }

        if (!IsFiniteNonNegative(shadow.BlurRadiusMm))
        {
            errors.Add($"{prefix}.BlurRadiusMm must be finite and >= 0.");
        }
    }
}
