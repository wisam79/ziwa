using System.Collections.Immutable;
using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Collage;

public enum GridAlignment9
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum CollageSizingMode
{
    FixedPhysicalCellMm,
    FitGridToPaper,
}

public sealed record MixedRowBandSpec(
    int Columns,
    double CellWidthMm,
    double CellHeightMm,
    int Rows = 1);

public sealed record CollageGridSpec(
    string PresetId,
    CollageSizingMode SizingMode,
    int Rows,
    int Columns,
    double CellWidthMm,
    double CellHeightMm,
    double GapXMm,
    double GapYMm,
    double MarginMm,
    GridAlignment9 Alignment = GridAlignment9.MiddleCenter,
    ImmutableArray<MixedRowBandSpec> MixedBands = default);

public sealed record CollageSlot(
    SlotId Id,
    int Index,
    RectMm BoundsMm,
    AssetId? AssignedAssetId = null,
    AssetId? OriginalAssetId = null,
    CropSpec? Crop = null,
    int QuarterTurns = 0,
    bool FlipHorizontal = false,
    bool FlipVertical = false,
    MaskShapeKind Mask = MaskShapeKind.None,
    double CornerRadiusMm = 0.0,
    ImageFilterSpec? Filters = null,
    StrokeSpec? Border = null)
{
    public CropSpec EffectiveCrop => Crop ?? CropSpec.Identity;
    public ImageFilterSpec EffectiveFilters => Filters ?? ImageFilterSpec.None;
}

public sealed record CollageLayoutModel(
    CollageGridSpec Spec,
    ImmutableArray<CollageSlot> Slots,
    ImmutableArray<AssetId> PendingAssets,
    bool AutoRepeatMode = false);
