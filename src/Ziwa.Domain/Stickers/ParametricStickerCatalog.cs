using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Stickers;

public sealed record StickerFieldSpec(string Key, string LabelAr, string DefaultValue);

public sealed record StickerTemplateDefinition(
    string Id,
    string TitleAr,
    string CategoryAr,
    double DefaultWidthMm,
    double DefaultHeightMm,
    ImmutableArray<StickerFieldSpec> Fields,
    StickerPalette DefaultPalette);

public static class ParametricStickerCatalog
{
    public static readonly StickerTemplateDefinition CommercialSeal = new(
        Id: "commercial-seal",
        TitleAr: "ختم تجاري معتمد",
        CategoryAr: "أختام",
        DefaultWidthMm: 55.0,
        DefaultHeightMm: 55.0,
        Fields:
        [
            new("company", "اسم المؤسسة", "استوديو زِيوا للتصوير"),
            new("subtitle", "الوصف", "معتمد للطباعة الرسمية"),
            new("badge", "الشارة", "أصلي"),
        ],
        DefaultPalette: OklchPaletteGenerator.GenerateHarmoniousPalette(248.0, 0.15));

    public static readonly StickerTemplateDefinition WarrantyBadge = new(
        Id: "warranty-badge",
        TitleAr: "شارة ضمان ذهبية",
        CategoryAr: "شارات",
        DefaultWidthMm: 60.0,
        DefaultHeightMm: 60.0,
        Fields:
        [
            new("headline", "العنوان", "ضمان الجودة"),
            new("years", "المدة", "3 سنوات"),
            new("footer", "التذييل", "استبدال فوري"),
        ],
        DefaultPalette: OklchPaletteGenerator.GenerateHarmoniousPalette(78.0, 0.14));

    public static readonly StickerTemplateDefinition DiscountTag = new(
        Id: "discount-tag",
        TitleAr: "بطاقة عرض وخصم",
        CategoryAr: "عروض",
        DefaultWidthMm: 75.0,
        DefaultHeightMm: 45.0,
        Fields:
        [
            new("percent", "النسبة", "خصم 30%"),
            new("title", "عنوان العرض", "عرض الاستوديو الخاص"),
            new("code", "الكود", "ZIWA30"),
        ],
        DefaultPalette: OklchPaletteGenerator.GenerateHarmoniousPalette(22.0, 0.17));

    public static readonly StickerTemplateDefinition BusinessCard = new(
        Id: "business-card",
        TitleAr: "بطاقة أعمال تنفيذية",
        CategoryAr: "بطاقات",
        DefaultWidthMm: 90.0,
        DefaultHeightMm: 55.0,
        Fields:
        [
            new("name", "الاسم / الاستوديو", "استوديو زِيوا"),
            new("role", "التخصص", "طباعة وتصوير احترافي"),
            new("phone", "الهاتف", "0770 000 0000"),
        ],
        DefaultPalette: OklchPaletteGenerator.GenerateHarmoniousPalette(210.0, 0.13));

    public static ImmutableArray<StickerTemplateDefinition> All { get; } =
    [
        CommercialSeal,
        WarrantyBadge,
        DiscountTag,
        BusinessCard,
    ];

    public static StickerTemplateDefinition ResolveTemplate(string? templateId) =>
        All.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase))
        ?? CommercialSeal;

    public static string BuildSvgXml(StickerNode node)
    {
        XNamespace svgNs = "http://www.w3.org/2000/svg";
        StickerTemplateDefinition def = ResolveTemplate(node.TemplateId);
        StickerPalette p = node.Palette;

        string primary = ToHex(p.Primary);
        string secondary = ToHex(p.Secondary);
        string accent = ToHex(p.Accent);
        string onPrimary = ToHex(p.TextOnPrimary);
        string surface = ToHex(p.SurfaceBackground);
        string onSurface = ToHex(p.TextOnSurface);

        string Field(string key, string fallback) =>
            node.Fields.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        XElement root = new(
            svgNs + "svg",
            new XAttribute("xmlns", svgNs.NamespaceName),
            new XAttribute("viewBox", "0 0 200 200"));

        switch (def.Id)
        {
            case "warranty-badge":
                root = new XElement(
                    svgNs + "svg",
                    new XAttribute("xmlns", svgNs.NamespaceName),
                    new XAttribute("viewBox", "0 0 200 200"),
                    new XElement(svgNs + "circle", new XAttribute("cx", "100"), new XAttribute("cy", "100"), new XAttribute("r", "92"), new XAttribute("fill", primary)),
                    new XElement(svgNs + "circle", new XAttribute("cx", "100"), new XAttribute("cy", "100"), new XAttribute("r", "80"), new XAttribute("fill", "none"), new XAttribute("stroke", accent), new XAttribute("stroke-width", "4")),
                    new XElement(svgNs + "rect", new XAttribute("x", "22"), new XAttribute("y", "82"), new XAttribute("width", "156"), new XAttribute("height", "36"), new XAttribute("rx", "10"), new XAttribute("fill", surface)),
                    CreateText(svgNs, 100, 62, 18, onPrimary, Field("headline", "ضمان الجودة"), bold: true),
                    CreateText(svgNs, 100, 107, 22, onSurface, Field("years", "3 سنوات"), bold: true),
                    CreateText(svgNs, 100, 150, 14, onPrimary, Field("footer", "استبدال فوري")));
                break;

            case "discount-tag":
                root = new XElement(
                    svgNs + "svg",
                    new XAttribute("xmlns", svgNs.NamespaceName),
                    new XAttribute("viewBox", "0 0 300 180"),
                    new XElement(svgNs + "rect", new XAttribute("x", "6"), new XAttribute("y", "6"), new XAttribute("width", "288"), new XAttribute("height", "168"), new XAttribute("rx", "16"), new XAttribute("fill", primary)),
                    new XElement(svgNs + "rect", new XAttribute("x", "14"), new XAttribute("y", "14"), new XAttribute("width", "272"), new XAttribute("height", "152"), new XAttribute("rx", "12"), new XAttribute("fill", "none"), new XAttribute("stroke", accent), new XAttribute("stroke-width", "3"), new XAttribute("stroke-dasharray", "8 6")),
                    new XElement(svgNs + "rect", new XAttribute("x", "70"), new XAttribute("y", "118"), new XAttribute("width", "160"), new XAttribute("height", "34"), new XAttribute("rx", "8"), new XAttribute("fill", surface)),
                    CreateText(svgNs, 150, 62, 34, onPrimary, Field("percent", "خصم 30%"), bold: true),
                    CreateText(svgNs, 150, 96, 17, onPrimary, Field("title", "عرض الاستوديو الخاص")),
                    CreateText(svgNs, 150, 141, 16, onSurface, Field("code", "ZIWA30"), bold: true));
                break;

            case "business-card":
                root = new XElement(
                    svgNs + "svg",
                    new XAttribute("xmlns", svgNs.NamespaceName),
                    new XAttribute("viewBox", "0 0 360 220"),
                    new XElement(svgNs + "rect", new XAttribute("x", "0"), new XAttribute("y", "0"), new XAttribute("width", "360"), new XAttribute("height", "220"), new XAttribute("rx", "12"), new XAttribute("fill", surface)),
                    new XElement(svgNs + "rect", new XAttribute("x", "0"), new XAttribute("y", "0"), new XAttribute("width", "360"), new XAttribute("height", "64"), new XAttribute("fill", primary)),
                    new XElement(svgNs + "rect", new XAttribute("x", "0"), new XAttribute("y", "64"), new XAttribute("width", "360"), new XAttribute("height", "6"), new XAttribute("fill", accent)),
                    new XElement(svgNs + "circle", new XAttribute("cx", "48"), new XAttribute("cy", "32"), new XAttribute("r", "16"), new XAttribute("fill", secondary)),
                    CreateText(svgNs, 210, 42, 22, onPrimary, Field("name", "استوديو زِيوا"), bold: true),
                    CreateText(svgNs, 180, 122, 18, onSurface, Field("role", "طباعة وتصوير احترافي"), bold: true),
                    CreateText(svgNs, 180, 166, 16, onSurface, Field("phone", "0770 000 0000")));
                break;

            default: // commercial-seal
                root.Add(
                    new XElement(svgNs + "circle", new XAttribute("cx", "100"), new XAttribute("cy", "100"), new XAttribute("r", "94"), new XAttribute("fill", surface), new XAttribute("stroke", primary), new XAttribute("stroke-width", "6")),
                    new XElement(svgNs + "circle", new XAttribute("cx", "100"), new XAttribute("cy", "100"), new XAttribute("r", "80"), new XAttribute("fill", "none"), new XAttribute("stroke", secondary), new XAttribute("stroke-width", "2.5"), new XAttribute("stroke-dasharray", "6 4")),
                    new XElement(svgNs + "rect", new XAttribute("x", "20"), new XAttribute("y", "78"), new XAttribute("width", "160"), new XAttribute("height", "44"), new XAttribute("rx", "8"), new XAttribute("fill", primary)),
                    CreateText(svgNs, 100, 54, 15, onSurface, Field("badge", "أصلي"), bold: true),
                    CreateText(svgNs, 100, 107, 17, onPrimary, Field("company", "استوديو زِيوا"), bold: true),
                    CreateText(svgNs, 100, 154, 13, onSurface, Field("subtitle", "معتمد للطباعة الرسمية")));
                break;
        }

        var doc = new XDocument(root);
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement CreateText(
        XNamespace ns,
        double x,
        double y,
        double fontSize,
        string fillHex,
        string text,
        bool bold = false)
    {
        var el = new XElement(
            ns + "text",
            new XAttribute("x", x.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("y", y.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("fill", fillHex),
            new XAttribute("font-family", "Cairo, sans-serif"),
            new XAttribute("font-size", fontSize.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("text-anchor", "middle"),
            text);
        if (bold)
        {
            el.SetAttributeValue("font-weight", "bold");
        }

        return el;
    }

    private static string ToHex(ColorRgba c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
