using System.Collections.Immutable;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.PrePress;

public static class PreflightChecker
{
    public const double MinimumRecommendedPrintDpi = 150.0;
    public const double MinimumTrimClearanceMm = 2.5;

    public static ImmutableArray<PreflightIssue> CheckPage(
        PageModel page,
        int pageIndex,
        double targetExportDpi,
        Func<AssetId, AssetPixelDimensions?>? assetDimensionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var issues = ImmutableArray.CreateBuilder<PreflightIssue>();
        double safeDistMm = Math.Max(
            MinimumTrimClearanceMm,
            page.SafeMarginMm > 0.0 ? Math.Min(page.SafeMarginMm, 3.0) : MinimumTrimClearanceMm);

        if (assetDimensionResolver is not null && page.CollageLayout is { Slots.IsDefaultOrEmpty: false } collage)
        {
            foreach (CollageSlot slot in collage.Slots)
            {
                if (slot.AssignedAssetId is not { IsEmpty: false } assetId)
                {
                    continue;
                }

                bool swapsAxes = slot.QuarterTurns is 1 or 3;
                double logicalW = swapsAxes ? slot.BoundsMm.Height : slot.BoundsMm.Width;
                double logicalH = swapsAxes ? slot.BoundsMm.Width : slot.BoundsMm.Height;

                CheckAssetDpi(
                    issues,
                    pageIndex,
                    nodeId: null,
                    slotId: slot.Id,
                    assetId,
                    logicalW,
                    logicalH,
                    slot.EffectiveCrop,
                    targetExportDpi,
                    assetDimensionResolver);
            }
        }

        if (!page.Nodes.IsDefaultOrEmpty)
        {
            foreach (SceneNode node in page.Nodes)
            {
                CheckNodeRecursive(issues, node, page, pageIndex, safeDistMm, targetExportDpi, assetDimensionResolver);
            }
        }

        return issues.ToImmutable();
    }

    public static ImmutableArray<PreflightIssue> CheckDocument(
        ZiwaDocument document,
        Func<AssetId, AssetPixelDimensions?>? assetDimensionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Pages.IsDefaultOrEmpty)
        {
            return [];
        }

        double targetExportDpi = document.PrintSettings.ExportDpi;
        var issues = ImmutableArray.CreateBuilder<PreflightIssue>();

        for (int i = 0; i < document.Pages.Length; i++)
        {
            issues.AddRange(CheckPage(document.Pages[i], i, targetExportDpi, assetDimensionResolver));
        }

        return issues.ToImmutable();
    }

    private static void CheckNodeRecursive(
        ImmutableArray<PreflightIssue>.Builder issues,
        SceneNode node,
        PageModel page,
        int pageIndex,
        double safeDistMm,
        double targetExportDpi,
        Func<AssetId, AssetPixelDimensions?>? assetDimensionResolver)
    {
        if (!node.IsVisible)
        {
            return;
        }

        switch (node)
        {
            case GroupNode group:
                if (!group.Children.IsDefaultOrEmpty)
                {
                    foreach (SceneNode child in group.Children)
                    {
                        CheckNodeRecursive(issues, child, page, pageIndex, safeDistMm, targetExportDpi, assetDimensionResolver);
                    }
                }

                break;

            case ImageNode img when assetDimensionResolver is not null:
                CheckAssetDpi(
                    issues,
                    pageIndex,
                    nodeId: img.Id,
                    slotId: null,
                    img.CurrentAssetId,
                    img.BoundsMm.Width,
                    img.BoundsMm.Height,
                    img.Crop,
                    targetExportDpi,
                    assetDimensionResolver);
                break;

            case ShapeNode shape when assetDimensionResolver is not null && shape.Fill is ImageShapeFill imgFill:
                CheckAssetDpi(
                    issues,
                    pageIndex,
                    nodeId: shape.Id,
                    slotId: null,
                    imgFill.CurrentAssetId,
                    shape.BoundsMm.Width,
                    shape.BoundsMm.Height,
                    imgFill.Crop,
                    targetExportDpi,
                    assetDimensionResolver);
                break;

            case TextNode text:
                RectMm b = text.BoundsMm;
                double distMm = Math.Min(
                    Math.Min(b.X, page.WidthMm - b.Right),
                    Math.Min(b.Y, page.HeightMm - b.Bottom));

                if (distMm < safeDistMm)
                {
                    issues.Add(new PreflightIssue(
                        PageIndex: pageIndex,
                        NodeId: text.Id,
                        SlotId: null,
                        Severity: PreflightSeverity.Warning,
                        Kind: PreflightIssueKind.TextNearTrimLine,
                        MessageAr: $"النص قريب جداً من خط القص ({distMm:F1} مم، الحد الآمن الموصى به {safeDistMm:F1} مم).",
                        DistanceToTrimMm: distMm));
                }

                break;
        }
    }

    private static void CheckAssetDpi(
        ImmutableArray<PreflightIssue>.Builder issues,
        int pageIndex,
        NodeId? nodeId,
        SlotId? slotId,
        AssetId assetId,
        double widthMm,
        double heightMm,
        CropSpec crop,
        double targetExportDpi,
        Func<AssetId, AssetPixelDimensions?> assetDimensionResolver)
    {
        if (assetId.IsEmpty || widthMm <= 0.0 || heightMm <= 0.0)
        {
            return;
        }

        AssetPixelDimensions? dimsOpt = assetDimensionResolver(assetId);
        if (dimsOpt is not { WidthPx: > 0, HeightPx: > 0 } dims)
        {
            return;
        }

        double effectiveZoom = Math.Max(1.0, double.IsFinite(crop.Zoom) ? crop.Zoom : 1.0);
        double dpiX = (dims.WidthPx / effectiveZoom) / (widthMm / DomainMath.MmPerInch);
        double dpiY = (dims.HeightPx / effectiveZoom) / (heightMm / DomainMath.MmPerInch);
        double effDpi = Math.Min(dpiX, dpiY);

        if (effDpi < MinimumRecommendedPrintDpi)
        {
            string targetHint = double.IsFinite(targetExportDpi) && targetExportDpi > 0.0
                ? $" (هدف التصدير {targetExportDpi:F0} DPI)"
                : string.Empty;

            issues.Add(new PreflightIssue(
                PageIndex: pageIndex,
                NodeId: nodeId,
                SlotId: slotId,
                Severity: PreflightSeverity.Warning,
                Kind: PreflightIssueKind.LowEffectiveImageDpi,
                MessageAr: $"دقة الصورة الفعّالة منخفضة ({effDpi:F0} DPI)، والحد الأدنى الموصى به للطباعة هو {MinimumRecommendedPrintDpi:F0} DPI{targetHint}.",
                EffectiveDpi: effDpi));
        }
    }
}
