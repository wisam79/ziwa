using System.Text.Json.Serialization;

namespace Ziwa.Domain.Primitives;

public readonly record struct RectMm(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + (Width * 0.5);
    public double CenterY => Y + (Height * 0.5);
}

public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255)
{
    public static ColorRgba White => new(255, 255, 255, 255);
    public static ColorRgba Black => new(0, 0, 0, 255);
    public static ColorRgba Transparent => new(0, 0, 0, 0);
}

public readonly record struct CornerRadiiMm(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
{
    public static CornerRadiiMm None => new(0.0, 0.0, 0.0, 0.0);
    public static CornerRadiiMm Uniform(double radiusMm) => new(radiusMm, radiusMm, radiusMm, radiusMm);
    public bool IsZero => TopLeft <= 0.0 && TopRight <= 0.0 && BottomRight <= 0.0 && BottomLeft <= 0.0;
}

public sealed record StrokeSpec(ColorRgba Color, double WidthMm);

public sealed record ShadowSpec(ColorRgba Color, double OffsetXMm, double OffsetYMm, double BlurRadiusMm);

public sealed record CropSpec(double Zoom = 1.0, double PanX = 0.0, double PanY = 0.0)
{
    public static CropSpec Identity { get; } = new(1.0, 0.0, 0.0);
}

public enum PhotoFilterPreset
{
    None,
    WarmStudio,
    CoolCrisp,
    ClassicBw,
    HighKeyId,
    SepiaVintage,
}

public enum DocumentScanFilterKind
{
    None = 0,
    MagicColor = 1,
    OtsuBlackAndWhite = 2,
    EnhancedGrayscale = 3,
    RemoveYellowing = 4,
    Sharpen = 5,
}

public sealed record ImageFilterSpec(
    double Brightness = 0.0,
    double Contrast = 0.0,
    double Saturation = 0.0,
    double BlurPx = 0.0,
    double? LuminanceThreshold = null,
    int PixelateBlockSize = 1,
    PhotoFilterPreset Preset = PhotoFilterPreset.None,
    DocumentScanFilterKind ScanFilter = DocumentScanFilterKind.None)
{
    public static ImageFilterSpec None { get; } = new();

    public bool HasActiveAdjustments =>
        System.Math.Abs(Brightness) > 0.0001 ||
        System.Math.Abs(Contrast) > 0.0001 ||
        System.Math.Abs(Saturation) > 0.0001 ||
        BlurPx > 0.001 ||
        LuminanceThreshold is not null ||
        PixelateBlockSize > 1 ||
        Preset != PhotoFilterPreset.None ||
        ScanFilter != DocumentScanFilterKind.None;
}

public enum EditorWorkflow
{
    Freeform,
    Collage,
}

public enum MaskShapeKind
{
    None,
    Circle,
    Arch,
    Hexagon,
    Diamond,
    Heart,
    Star,
    Seal,
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Triangle,
    Polygon,
    Star,
    Line,
    Arrow,
    Ring,
    Arc,
    Wedge,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SolidShapeFill), "solid")]
[JsonDerivedType(typeof(LinearGradientShapeFill), "linearGradient")]
[JsonDerivedType(typeof(ImageShapeFill), "image")]
public abstract record ShapeFill;

public sealed record SolidShapeFill(ColorRgba Color) : ShapeFill;

public sealed record LinearGradientShapeFill(
    ColorRgba StartColor,
    ColorRgba EndColor,
    double AngleDeg = 90.0) : ShapeFill;

public sealed record ImageShapeFill(
    AssetId CurrentAssetId,
    AssetId OriginalAssetId,
    CropSpec Crop) : ShapeFill;

public enum TextAlignmentKind
{
    Right,
    Center,
    Left,
}

public enum TextDirectionMode
{
    AutoRtl,
    Rtl,
    Ltr,
}

public enum TextCurveKind
{
    None,
    ArcUp,
    ArcDown,
    FullCircle,
}

public sealed record TextCurveSpec(
    TextCurveKind Kind,
    double BendFactor = 0.65);

public sealed record TextEffectSpec(
    StrokeSpec? Outline = null,
    ColorRgba? PillBackgroundColor = null,
    double PillPaddingMm = 1.5,
    double PillCornerRadiusMm = 2.0,
    ShadowSpec? Shadow = null)
{
    public static TextEffectSpec None { get; } = new();
}
