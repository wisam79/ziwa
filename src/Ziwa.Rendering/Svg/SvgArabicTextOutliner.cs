using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Svg;

/// <summary>
/// Converts SVG <c>&lt;text&gt;</c> elements into HarfBuzz-shaped vector <c>&lt;path&gt;</c>
/// outlines before passing SVG markup to <c>Svg.Skia</c>, preserving Arabic ligatures and BiDi order.
/// </summary>
public static class SvgArabicTextOutliner
{
    private static readonly HashSet<string> CopiedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "class",
        "style",
        "fill",
        "fill-opacity",
        "fill-rule",
        "stroke",
        "stroke-width",
        "stroke-opacity",
        "stroke-linecap",
        "stroke-linejoin",
        "opacity",
        "transform",
        "clip-path",
    };

    public static string OutlineTextElements(
        string sanitizedSvgXml,
        FontRegistry fontRegistry,
        HarfBuzzArabicShaper shaper)
    {
        if (string.IsNullOrWhiteSpace(sanitizedSvgXml) ||
            !sanitizedSvgXml.Contains("<text", StringComparison.OrdinalIgnoreCase))
        {
            return sanitizedSvgXml;
        }

        XDocument doc = XDocument.Parse(sanitizedSvgXml, LoadOptions.None);
        if (doc.Root is null)
        {
            return sanitizedSvgXml;
        }

        List<XElement> textElements = doc.Descendants()
            .Where(static e => e.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (textElements.Count == 0)
        {
            return sanitizedSvgXml;
        }

        bool modified = false;
        foreach (XElement textEl in textElements)
        {
            if (textEl.Descendants().Any(static d => d.Name.LocalName.Equals("textPath", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string rawText = textEl.Value;
            if (string.IsNullOrWhiteSpace(rawText))
            {
                textEl.Remove();
                modified = true;
                continue;
            }

            string line = rawText
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\n', ' ')
                .Replace('\r', ' ');

            double x = ParseNumber(textEl.Attribute("x")?.Value, 0.0);
            double y = ParseNumber(textEl.Attribute("y")?.Value, 0.0);
            double fontSize = Math.Max(0.5, ParseNumber(GetProperty(textEl, "font-size"), 16.0));
            string fontFamily = ExtractFirstFontFamily(GetProperty(textEl, "font-family"));
            int fontWeight = ParseFontWeight(GetProperty(textEl, "font-weight"));
            string textAnchor = (GetProperty(textEl, "text-anchor") ?? "start").Trim().ToLowerInvariant();
            string directionAttr = (GetProperty(textEl, "direction") ?? string.Empty).Trim().ToLowerInvariant();

            TextDirectionMode directionMode = directionAttr switch
            {
                "rtl" => TextDirectionMode.Rtl,
                "ltr" => TextDirectionMode.Ltr,
                _ => TextDirectionMode.AutoRtl,
            };

            ShapedLineResult shaped = shaper.ShapeLine(line, fontFamily, fontWeight, fontSize, directionMode);
            if (shaped.Glyphs.IsDefaultOrEmpty)
            {
                textEl.Remove();
                modified = true;
                continue;
            }

            bool baseIsRtl = HarfBuzzArabicShaper.ResolveBaseDirectionIsRtl(line, directionMode);
            float originX = textAnchor switch
            {
                "middle" => (float)x - (shaped.TotalAdvanceMm * 0.5f),
                "end" => baseIsRtl ? (float)x : (float)x - shaped.TotalAdvanceMm,
                _ => baseIsRtl ? (float)x - shaped.TotalAdvanceMm : (float)x,
            };
            float baselineY = (float)y;

            FontRegistry.FontEntry fontEntry = fontRegistry.ResolveFont(fontFamily, fontWeight);
            using var skFont = new SKFont(fontEntry.Typeface, (float)fontSize)
            {
                LinearMetrics = true,
                Subpixel = true,
                Hinting = SKFontHinting.None,
                Edging = SKFontEdging.Antialias,
            };

            using var combinedPath = new SKPath();
            foreach (ShapedGlyph glyph in shaped.Glyphs)
            {
                using SKPath? glyphPath = skFont.GetGlyphPath(glyph.GlyphId);
                if (glyphPath is null || glyphPath.IsEmpty)
                {
                    continue;
                }

                // HarfBuzz +Y points upward; SVG/Skia +Y points downward.
                SKMatrix offset = SKMatrix.CreateTranslation(originX + glyph.XMm, baselineY - glyph.YOffsetMm);
                using var transformed = new SKPath();
                glyphPath.Transform(offset, transformed);
                combinedPath.AddPath(transformed);
            }

            if (combinedPath.IsEmpty)
            {
                textEl.Remove();
                modified = true;
                continue;
            }

            string pathData = combinedPath.ToSvgPathData();
            if (string.IsNullOrWhiteSpace(pathData))
            {
                continue;
            }

            var pathEl = new XElement(textEl.Name.Namespace + "path", new XAttribute("d", pathData));
            foreach (XAttribute attr in textEl.Attributes())
            {
                if (CopiedAttributes.Contains(attr.Name.LocalName))
                {
                    pathEl.SetAttributeValue(attr.Name, attr.Value);
                }
            }

            textEl.ReplaceWith(pathEl);
            modified = true;
        }

        return modified ? doc.ToString(SaveOptions.DisableFormatting) : sanitizedSvgXml;
    }

    private static string? GetProperty(XElement element, string name)
    {
        XAttribute? direct = element.Attribute(name);
        if (direct is not null)
        {
            return direct.Value;
        }

        string? style = element.Attribute("style")?.Value;
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        foreach (string declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string propName = declaration[..colon].Trim();
            if (propName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static double ParseNumber(string? raw, double fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        string trimmed = raw.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2].Trim();
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
               double.IsFinite(value)
            ? value
            : fallback;
    }

    private static string ExtractFirstFontFamily(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return FontRegistry.DefaultFontFamily;
        }

        string first = raw.Split(',')[0].Trim().Trim('\'', '"');
        return string.IsNullOrWhiteSpace(first) ? FontRegistry.DefaultFontFamily : first;
    }

    private static int ParseFontWeight(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 400;
        }

        string trimmed = raw.Trim();
        if (trimmed.Equals("bold", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("bolder", StringComparison.OrdinalIgnoreCase))
        {
            return 700;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric) &&
               numeric >= 600
            ? numeric
            : 400;
    }
}
