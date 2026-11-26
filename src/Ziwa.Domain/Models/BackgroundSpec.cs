using System.Text.Json.Serialization;
using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SolidBackground), "solid")]
[JsonDerivedType(typeof(LinearGradientBackground), "linearGradient")]
[JsonDerivedType(typeof(RadialGradientBackground), "radialGradient")]
[JsonDerivedType(typeof(TransparentBackground), "transparent")]
public abstract record BackgroundSpec;

public sealed record SolidBackground(ColorRgba Color) : BackgroundSpec;

public sealed record LinearGradientBackground(
    ColorRgba StartColor,
    ColorRgba EndColor,
    double AngleDeg = 90.0) : BackgroundSpec;

public sealed record RadialGradientBackground(
    ColorRgba CenterColor,
    ColorRgba EdgeColor) : BackgroundSpec;

public sealed record TransparentBackground : BackgroundSpec;
