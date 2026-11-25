using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Typography;

public static class TypographyPresets
{
    public static TextNode CreateHeading(double xMm = 20.0, double yMm = 25.0) =>
        new(
            Id: NodeId.New(),
            Name: "عنوان رئيسي",
            BoundsMm: new RectMm(xMm, yMm, 108.0, 20.0),
            Text: "عنوان تصميم احترافي",
            FontFamily: "Cairo",
            FontSizePt: 24.0,
            FontWeight: 700,
            Color: new ColorRgba(20, 24, 31, 255),
            Alignment: TextAlignmentKind.Right,
            Direction: TextDirectionMode.Rtl);

    public static TextNode CreateSubheading(double xMm = 20.0, double yMm = 50.0) =>
        new(
            Id: NodeId.New(),
            Name: "نص فرعي",
            BoundsMm: new RectMm(xMm, yMm, 108.0, 14.0),
            Text: "تفاصيل دقيقة للطباعة السريعة",
            FontFamily: "Tajawal",
            FontSizePt: 13.0,
            FontWeight: 400,
            Color: new ColorRgba(71, 82, 99, 255),
            Alignment: TextAlignmentKind.Right,
            Direction: TextDirectionMode.AutoRtl);

    public static TextNode CreateCapsuleBadge(double xMm = 35.0, double yMm = 70.0) =>
        new(
            Id: NodeId.New(),
            Name: "شارة كبسولة",
            BoundsMm: new RectMm(xMm, yMm, 78.0, 12.0),
            Text: "جودة أصلية 100%",
            FontFamily: "IBM Plex Sans Arabic",
            FontSizePt: 11.5,
            FontWeight: 700,
            Color: ColorRgba.White,
            Alignment: TextAlignmentKind.Center,
            Direction: TextDirectionMode.AutoRtl,
            Effects: new TextEffectSpec(
                PillBackgroundColor: new ColorRgba(17, 107, 54, 255),
                PillPaddingMm: 2.5,
                PillCornerRadiusMm: 5.0));

    public static TextNode CreateCurvedSeal(double xMm = 34.0, double yMm = 92.0) =>
        new(
            Id: NodeId.New(),
            Name: "ختم عربي منحنٍ",
            BoundsMm: new RectMm(xMm, yMm, 80.0, 80.0),
            Text: "ختم استوديو زِيوا الاحترافي",
            FontFamily: "Cairo",
            FontSizePt: 14.0,
            FontWeight: 700,
            Color: new ColorRgba(11, 92, 171, 255),
            Alignment: TextAlignmentKind.Center,
            Direction: TextDirectionMode.Rtl,
            Curve: new TextCurveSpec(TextCurveKind.FullCircle, 1.0),
            Effects: new TextEffectSpec(
                Outline: new StrokeSpec(ColorRgba.White, 0.35),
                Shadow: new ShadowSpec(new ColorRgba(15, 23, 42, 40), 0.0, 0.8, 1.2)));

    public static TextNode CreateWatermark(double xMm = 24.0, double yMm = 95.0) =>
        new(
            Id: NodeId.New(),
            Name: "علامة مائية",
            BoundsMm: new RectMm(xMm, yMm, 100.0, 20.0),
            Text: "زِيوا استوديو",
            FontFamily: "Tajawal",
            FontSizePt: 22.0,
            FontWeight: 700,
            Color: new ColorRgba(11, 92, 171, 255),
            Alignment: TextAlignmentKind.Center,
            Direction: TextDirectionMode.Rtl,
            RotationDeg: -22.0,
            Opacity: 0.22);
}
