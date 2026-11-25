using System.Collections.Immutable;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Services;

public static class SampleVerticalSliceFactory
{
    public static ZiwaDocument CreateShowcaseDocument(AssetId emblemAssetId)
    {
        if (emblemAssetId.IsEmpty)
        {
            throw new ArgumentException("Emblem asset ID cannot be empty.", nameof(emblemAssetId));
        }

        PaperPreset preset = PaperPresets.A5;
        ImmutableArray<SceneNode> nodes =
        [
            new ShapeNode(
                Id: NodeId.New(),
                Name: "إطار ترويسة البطاقة",
                BoundsMm: new RectMm(10.0, 10.0, 128.0, 44.0),
                Kind: ShapeKind.Rectangle,
                Fill: new SolidShapeFill(new ColorRgba(11, 92, 171, 255)),
                Radii: CornerRadiiMm.Uniform(4.0),
                Stroke: new StrokeSpec(new ColorRgba(8, 67, 128, 255), 0.5),
                Shadow: new ShadowSpec(new ColorRgba(15, 23, 42, 42), 0.0, 1.5, 2.0)),
            new ShapeNode(
                Id: NodeId.New(),
                Name: "شريط الحالة السفلي للبطاقة",
                BoundsMm: new RectMm(10.0, 174.0, 128.0, 22.0),
                Kind: ShapeKind.Rectangle,
                Fill: new SolidShapeFill(new ColorRgba(238, 243, 250, 255)),
                Radii: CornerRadiiMm.Uniform(3.0),
                Stroke: new StrokeSpec(new ColorRgba(184, 194, 209, 255), 0.35)),
            new ImageNode(
                Id: NodeId.New(),
                Name: "شعار استوديو زِيوا",
                BoundsMm: new RectMm(14.0, 16.0, 32.0, 32.0),
                CurrentAssetId: emblemAssetId,
                OriginalAssetId: emblemAssetId,
                Crop: CropSpec.Identity,
                Mask: MaskShapeKind.None,
                CornerRadiusMm: 4.0,
                Border: new StrokeSpec(ColorRgba.White, 0.7),
                Shadow: new ShadowSpec(new ColorRgba(0, 0, 0, 55), 0.0, 1.0, 1.8)),
            new TextNode(
                Id: NodeId.New(),
                Name: "العنوان الرئيسي العربي",
                BoundsMm: new RectMm(50.0, 16.0, 82.0, 20.0),
                Text: "استوديو زِيوا للتصميم",
                FontFamily: "Cairo",
                FontSizePt: 18.0,
                FontWeight: 700,
                Color: ColorRgba.White,
                Alignment: TextAlignmentKind.Right,
                Direction: TextDirectionMode.Rtl),
            new TextNode(
                Id: NodeId.New(),
                Name: "العنوان الفرعي",
                BoundsMm: new RectMm(50.0, 34.0, 82.0, 12.0),
                Text: "الطباعة الاحترافية بدقة 300 DPI",
                FontFamily: "Tajawal",
                FontSizePt: 11.5,
                FontWeight: 700,
                Color: new ColorRgba(226, 238, 252, 255),
                Alignment: TextAlignmentKind.Right,
                Direction: TextDirectionMode.AutoRtl),
            new TextNode(
                Id: NodeId.New(),
                Name: "نص عربي مشكّل للتجربة",
                BoundsMm: new RectMm(12.0, 74.0, 124.0, 46.0),
                Text: "تَشْكِيلٌ عَرَبِيٌّ مَوْصُولٌ\nلا تَفَاوُتَ بَيْنَ الشَّاشَةِ وَالْمَطْبَعَةِ",
                FontFamily: "IBM Plex Sans Arabic",
                FontSizePt: 15.0,
                FontWeight: 700,
                Color: new ColorRgba(20, 24, 31, 255),
                Alignment: TextAlignmentKind.Center,
                Direction: TextDirectionMode.Rtl,
                LineHeight: 1.5),
            new TextNode(
                Id: NodeId.New(),
                Name: "تفاصيل المعايرة المختلطة",
                BoundsMm: new RectMm(12.0, 130.0, 124.0, 26.0),
                Text: "محرك SkiaSharp + HarfBuzz · 148 × 210 mm",
                FontFamily: "Tajawal",
                FontSizePt: 11.0,
                FontWeight: 400,
                Color: new ColorRgba(71, 82, 99, 255),
                Alignment: TextAlignmentKind.Center,
                Direction: TextDirectionMode.AutoRtl),
            new TextNode(
                Id: NodeId.New(),
                Name: "تذييل التطابق",
                BoundsMm: new RectMm(16.0, 180.0, 116.0, 12.0),
                Text: "Preview == Export == Print · 25.4 mm/in",
                FontFamily: "IBM Plex Sans Arabic",
                FontSizePt: 9.5,
                FontWeight: 400,
                Color: new ColorRgba(11, 92, 171, 255),
                Alignment: TextAlignmentKind.Center,
                Direction: TextDirectionMode.Ltr),
        ];

        var page = new PageModel(
            Id: PageId.New(),
            Name: "الورقة التجريبية A5",
            WidthMm: preset.WidthMm,
            HeightMm: preset.HeightMm,
            BleedMm: 0.0,
            SafeMarginMm: preset.DefaultSafeMarginMm,
            Background: new SolidBackground(new ColorRgba(250, 251, 253, 255)),
            Nodes: nodes);

        return new ZiwaDocument(
            Id: DocumentId.New(),
            SchemaVersion: 1,
            Title: "بطاقة معايرة زِيوا — الشريحة العمودية",
            Mode: EditorWorkflow.Freeform,
            Pages: [page],
            ActivePageIndex: 0,
            PrintSettings: new PrintTicketSpec(ExportDpi: 300.0),
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }
}
