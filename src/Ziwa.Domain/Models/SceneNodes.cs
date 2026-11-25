using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;

namespace Ziwa.Domain.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ImageNode), "image")]
[JsonDerivedType(typeof(ShapeNode), "shape")]
[JsonDerivedType(typeof(TextNode), "text")]
[JsonDerivedType(typeof(SvgNode), "svg")]
[JsonDerivedType(typeof(StickerNode), "sticker")]
[JsonDerivedType(typeof(GroupNode), "group")]
public abstract record SceneNode(
    NodeId Id,
    string Name,
    RectMm BoundsMm,
    double RotationDeg,
    double Opacity,
    bool IsVisible,
    bool IsLocked);

public sealed record ImageNode(
    NodeId Id,
    string Name,
    RectMm BoundsMm,
    AssetId CurrentAssetId,
    AssetId OriginalAssetId,
    CropSpec Crop,
    MaskShapeKind Mask = MaskShapeKind.None,
    double CornerRadiusMm = 0.0,
    ImageFilterSpec? Filters = null,
    StrokeSpec? Border = null,
    ShadowSpec? Shadow = null,
    double RotationDeg = 0.0,
    double Opacity = 1.0,
    bool IsVisible = true,
    bool IsLocked = false) : SceneNode(Id, Name, BoundsMm, RotationDeg, Opacity, IsVisible, IsLocked)
{
    public ImageFilterSpec EffectiveFilters => Filters ?? ImageFilterSpec.None;
}

public sealed record ShapeNode(
    NodeId Id,
    string Name,
    RectMm BoundsMm,
    ShapeKind Kind,
    ShapeFill Fill,
    CornerRadiiMm Radii = default,
    StrokeSpec? Stroke = null,
    ShadowSpec? Shadow = null,
    int PolygonSides = 6,
    double InnerRadiusRatio = 0.45,
    double StartAngleDeg = 0.0,
    double SweepAngleDeg = 270.0,
    double RotationDeg = 0.0,
    double Opacity = 1.0,
    bool IsVisible = true,
    bool IsLocked = false) : SceneNode(Id, Name, BoundsMm, RotationDeg, Opacity, IsVisible, IsLocked);

public sealed record TextNode(
    NodeId Id,
    string Name,
    RectMm BoundsMm,
    string Text,
    string FontFamily,
    double FontSizePt,
    int FontWeight,
    ColorRgba Color,
    TextAlignmentKind Alignment = TextAlignmentKind.Right,
    TextDirectionMode Direction = TextDirectionMode.AutoRtl,
    double LineHeight = 1.35,
    double LetterSpacingEm = 0.0,
    TextCurveSpec? Curve = null,
    TextEffectSpec? Effects = null,
    double RotationDeg = 0.0,
    double Opacity = 1.0,
    bool IsVisible = true,
    bool IsLocked = false) : SceneNode(Id, Name, BoundsMm, RotationDeg, Opacity, IsVisible, IsLocked)
{
    public TextEffectSpec EffectiveEffects => Effects ?? TextEffectSpec.None;
}

public sealed record SvgNode(
    NodeId Id,
    string Name,
    RectMm BoundsMm,
    AssetId SvgAssetId,
    ColorRgba? TintColor = null,
    ShadowSpec? Shadow = null,
    double RotationDeg = 0.0,
    double Opacity = 1.0,
    bool IsVisible = true,
    bool IsLocked = false) : SceneNode(Id, Name, BoundsMm, RotationDeg, Opacity, IsVisible, IsLocked);

public sealed record StickerNode(
    NodeId Id,
    string Name,
    RectMm BoundsMm,
    string TemplateId,
    ImmutableDictionary<string, string> Fields,
    StickerPalette Palette,
    ColorRgba? SvgTintColor = null,
    ShadowSpec? Shadow = null,
    double RotationDeg = 0.0,
    double Opacity = 1.0,
    bool IsVisible = true,
    bool IsLocked = false) : SceneNode(Id, Name, BoundsMm, RotationDeg, Opacity, IsVisible, IsLocked);

public sealed record GroupNode(
    NodeId Id,
    string Name,
    RectMm BoundsMm,
    ImmutableArray<SceneNode> Children,
    double RotationDeg = 0.0,
    double Opacity = 1.0,
    bool IsVisible = true,
    bool IsLocked = false) : SceneNode(Id, Name, BoundsMm, RotationDeg, Opacity, IsVisible, IsLocked);
