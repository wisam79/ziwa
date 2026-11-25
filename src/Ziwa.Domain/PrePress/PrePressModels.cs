using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.PrePress;

public enum CmykProfileKind
{
    CoatedFogra39,
    USWebCoatedSwop,
    CustomIcc,
}

public enum ExportPageRangeKind
{
    ActivePageOnly,
    AllPages,
    SpecificPages,
}

public enum PreflightSeverity
{
    Info,
    Warning,
}

public enum PreflightIssueKind
{
    LowEffectiveImageDpi,
    TextNearTrimLine,
}

public readonly record struct AssetPixelDimensions(int WidthPx, int HeightPx);

public sealed record PreflightIssue(
    int PageIndex,
    NodeId? NodeId,
    SlotId? SlotId,
    PreflightSeverity Severity,
    PreflightIssueKind Kind,
    string MessageAr,
    double? EffectiveDpi = null,
    double? DistanceToTrimMm = null);
