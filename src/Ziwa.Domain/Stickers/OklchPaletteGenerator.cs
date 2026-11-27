using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Stickers;

public sealed record StickerPalette(
    ColorRgba Primary,
    ColorRgba Secondary,
    ColorRgba Accent,
    ColorRgba TextOnPrimary,
    ColorRgba SurfaceBackground,
    ColorRgba TextOnSurface);

public static class OklchPaletteGenerator
{
    private static readonly ColorRgba DarkInk = new(12, 16, 23, 255);
    private static readonly ColorRgba CrispWhite = new(255, 255, 255, 255);

    public static ColorRgba FromOklch(double lightness, double chroma, double hueDeg)
    {
        double l = System.Math.Clamp(lightness, 0.0, 1.0);
        double c = System.Math.Clamp(chroma, 0.0, 0.4);
        double hRad = (hueDeg % 360.0) * System.Math.PI / 180.0;

        double a = c * System.Math.Cos(hRad);
        double b = c * System.Math.Sin(hRad);

        double l_ = l + (0.3963377774 * a) + (0.2158037573 * b);
        double m_ = l - (0.1055613458 * a) - (0.0638541728 * b);
        double s_ = l - (0.0894841775 * a) - (1.2914855480 * b);

        double l3 = l_ * l_ * l_;
        double m3 = m_ * m_ * m_;
        double s3 = s_ * s_ * s_;

        double rLin = (+4.0767416621 * l3) - (3.3077115913 * m3) + (0.2309699292 * s3);
        double gLin = (-1.2684380046 * l3) + (2.6097574011 * m3) - (0.3413193965 * s3);
        double bLin = (-0.0041960863 * l3) - (0.7034186147 * m3) + (1.7076147010 * s3);

        return new ColorRgba(
            LinearToSrgbByte(rLin),
            LinearToSrgbByte(gLin),
            LinearToSrgbByte(bLin),
            255);
    }

    public static StickerPalette GenerateHarmoniousPalette(double baseHueDeg, double chroma = 0.14)
    {
        ColorRgba primary = FromOklch(0.50, chroma, baseHueDeg);
        ColorRgba secondary = FromOklch(0.42, chroma * 0.9, baseHueDeg + 32.0);
        ColorRgba accent = FromOklch(0.76, chroma * 1.15, baseHueDeg + 165.0);
        ColorRgba surface = FromOklch(0.96, chroma * 0.18, baseHueDeg);

        return new StickerPalette(
            Primary: primary,
            Secondary: secondary,
            Accent: accent,
            TextOnPrimary: PickHighContrastText(primary),
            SurfaceBackground: surface,
            TextOnSurface: PickHighContrastText(surface));
    }

    public static StickerPalette GenerateSmartRandomPalette(int seed)
    {
        var rng = new Random(seed);
        double hue = rng.NextDouble() * 360.0;
        double chroma = 0.11 + (rng.NextDouble() * 0.08);
        return GenerateHarmoniousPalette(hue, chroma);
    }

    public static StickerPalette GeneratePaletteFromPrimaryColor(ColorRgba primary)
    {
        static double SrgbToLinear(byte ch)
        {
            double s = ch / 255.0;
            return s <= 0.04045 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        double r = SrgbToLinear(primary.R);
        double g = SrgbToLinear(primary.G);
        double b = SrgbToLinear(primary.B);

        double l_ = System.Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        double m_ = System.Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        double s_ = System.Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));

        double a = (1.9779984951 * l_) - (2.4285922050 * m_) + (0.4505937099 * s_);
        double okb = (0.0259040371 * l_) + (0.7827717662 * m_) - (0.8086757660 * s_);

        double chroma = System.Math.Clamp(System.Math.Sqrt((a * a) + (okb * okb)), 0.06, 0.18);
        double hueDeg = ((System.Math.Atan2(okb, a) * 180.0 / System.Math.PI) % 360.0 + 360.0) % 360.0;

        ColorRgba opaquePrimary = new(primary.R, primary.G, primary.B, 255);
        StickerPalette basePalette = GenerateHarmoniousPalette(hueDeg, chroma);
        return basePalette with
        {
            Primary = opaquePrimary,
            TextOnPrimary = PickHighContrastText(opaquePrimary),
        };
    }

    public static ColorRgba PickHighContrastText(ColorRgba background)
    {
        double darkRatio = ComputeContrastRatio(DarkInk, background);
        double whiteRatio = ComputeContrastRatio(CrispWhite, background);
        return whiteRatio >= darkRatio ? CrispWhite : DarkInk;
    }

    public static double ComputeContrastRatio(ColorRgba foreground, ColorRgba background)
    {
        double l1 = RelativeLuminance(foreground);
        double l2 = RelativeLuminance(background);
        return (System.Math.Max(l1, l2) + 0.05) / (System.Math.Min(l1, l2) + 0.05);
    }

    private static double RelativeLuminance(ColorRgba c)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(c.R)) + (0.7152 * Linearize(c.G)) + (0.0722 * Linearize(c.B));
    }

    private static byte LinearToSrgbByte(double linear)
    {
        double clamped = System.Math.Clamp(linear, 0.0, 1.0);
        double srgb = clamped <= 0.0031308
            ? 12.92 * clamped
            : (1.055 * System.Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
        return (byte)System.Math.Clamp((int)System.Math.Round(srgb * 255.0), 0, 255);
    }
}
