using System.Collections.Immutable;
using Ziwa.Application.Interaction;
using Ziwa.Application.Stickers;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Workspace;

public enum QuickStartPresetKind
{
    IdPhotoSheet4x6,
    BusinessCardFront,
    StickerSheetA5,
    BlankFreeformA4,
}

public sealed record StudioBrandKit(
    string StudioNameAr,
    AssetId LogoSvgAssetId,
    ColorRgba PrimaryColor,
    ColorRgba AccentColor,
    ImmutableArray<string> SavedPhrases);

public static class QuickStartAndBrandService
{
    public const string DefaultStudioLogoSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
          <rect x="4" y="4" width="56" height="56" rx="12" fill="#0B5CAB" />
          <polygon points="32,12 38,26 52,26 41,35 45,50 32,41 19,50 23,35 12,26 26,26" fill="#F2CC60" />
        </svg>
        """;

    public static StudioBrandKit CreateDefaultBrandKit(AssetId logoSvgAssetId)
    {
        return new StudioBrandKit(
            StudioNameAr: "استوديو زِيوا للتصوير والطباعة",
            LogoSvgAssetId: logoSvgAssetId,
            PrimaryColor: new ColorRgba(11, 92, 171, 255),
            AccentColor: new ColorRgba(242, 204, 96, 255),
            SavedPhrases:
            [
                "استوديو زِيوا — جودة طباعة احترافية",
                "معتمد للوثائق والجوازات الرسمية",
                "خدمة فورية خلال 5 دقائق",
            ]);
    }

    public static ZiwaDocument CreateQuickStartDocument(
        QuickStartPresetKind kind,
        AssetId? sampleAssetId = null)
    {
        return kind switch
        {
            QuickStartPresetKind.IdPhotoSheet4x6 => CreateIdPhotoSheet(sampleAssetId),
            QuickStartPresetKind.BusinessCardFront => CreateBusinessCardDoc(),
            QuickStartPresetKind.StickerSheetA5 => CreateStickerSheetDoc(),
            _ => CreateBlankFreeformA4Doc(),
        };
    }

    public static ZiwaDocument InsertBrandLogo(ZiwaDocument doc, StudioBrandKit kit)
    {
        if (kit.LogoSvgAssetId.IsEmpty)
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        var svgNode = new SvgNode(
            Id: NodeId.New(),
            Name: "شعار الاستوديو",
            BoundsMm: new RectMm((page.WidthMm * 0.5) - 12.0, 10.0, 24.0, 24.0),
            SvgAssetId: kit.LogoSvgAssetId);
        return FreeformSceneOperations.AddNode(doc, svgNode);
    }

    public static ZiwaDocument InsertBrandPhrase(ZiwaDocument doc, StudioBrandKit kit, int phraseIndex = 0)
    {
        if (kit.SavedPhrases.IsDefaultOrEmpty)
        {
            return doc;
        }

        string text = kit.SavedPhrases[Math.Clamp(phraseIndex, 0, kit.SavedPhrases.Length - 1)];
        PageModel page = doc.ActivePage;
        var node = new TextNode(
            Id: NodeId.New(),
            Name: "عبارة الهوية",
            BoundsMm: new RectMm(10.0, page.HeightMm * 0.45, Math.Max(40.0, page.WidthMm - 20.0), 16.0),
            Text: text,
            FontFamily: "Cairo",
            FontSizePt: 14.0,
            FontWeight: 700,
            Color: kit.PrimaryColor,
            Alignment: TextAlignmentKind.Center,
            Direction: TextDirectionMode.Rtl);

        return FreeformSceneOperations.AddNode(doc, node);
    }

    private static ZiwaDocument CreateIdPhotoSheet(AssetId? sampleAssetId)
    {
        PaperPreset paper = PaperPresets.Photo4x6In;
        CollageLayoutModel layout = PhysicalGridBuilder.BuildOrReflowLayout(
            paper.WidthMm,
            paper.HeightMm,
            CollagePresets.IdOfficial40x60);

        if (sampleAssetId is { IsEmpty: false } asset)
        {
            layout = layout with
            {
                AutoRepeatMode = true,
                Slots = layout.Slots
                    .Select(s => s with { AssignedAssetId = asset, OriginalAssetId = asset, Crop = CropSpec.Identity })
                    .ToImmutableArray(),
            };
        }

        var page = new PageModel(
            PageId.New(),
            "شيت صور هوية 4×6",
            paper.WidthMm,
            paper.HeightMm,
            0.0,
            paper.DefaultSafeMarginMm,
            new SolidBackground(ColorRgba.White),
            [],
            layout);

        return new ZiwaDocument(
            DocumentId.New(),
            1,
            "شيت صور هوية 4×6 بوصة",
            EditorWorkflow.Collage,
            [page],
            0,
            new PrintTicketSpec(300.0),
            DateTimeOffset.UtcNow);
    }

    private static ZiwaDocument CreateBusinessCardDoc()
    {
        PaperPreset paper = PaperPresets.BusinessCard;
        StickerNode card = StickerOperations.CreateStickerFromTemplate("business-card", paper.WidthMm * 0.5, paper.HeightMm * 0.5);
        card = card with { BoundsMm = new RectMm(0.0, 0.0, paper.WidthMm, paper.HeightMm) };

        var page = new PageModel(
            PageId.New(),
            "الوجه الأمامي",
            paper.WidthMm,
            paper.HeightMm,
            paper.DefaultBleedMm,
            paper.DefaultSafeMarginMm,
            new SolidBackground(ColorRgba.White),
            [card]);

        return new ZiwaDocument(
            DocumentId.New(),
            1,
            "بطاقة عمل احترافية",
            EditorWorkflow.Freeform,
            [page],
            0,
            new PrintTicketSpec(300.0),
            DateTimeOffset.UtcNow);
    }

    private static ZiwaDocument CreateStickerSheetDoc()
    {
        PaperPreset paper = PaperPresets.A5;
        StickerNode seal = StickerOperations.CreateStickerFromTemplate("commercial-seal", 45.0, 50.0);
        StickerNode badge = StickerOperations.CreateStickerFromTemplate("warranty-badge", 105.0, 50.0);
        StickerNode promo = StickerOperations.CreateStickerFromTemplate("discount-tag", 74.0, 125.0);

        var page = new PageModel(
            PageId.New(),
            "شيت ملصقات A5",
            paper.WidthMm,
            paper.HeightMm,
            0.0,
            paper.DefaultSafeMarginMm,
            new SolidBackground(ColorRgba.White),
            [seal, badge, promo]);

        return new ZiwaDocument(
            DocumentId.New(),
            1,
            "شيت ملصقات وأختام جاهز",
            EditorWorkflow.Freeform,
            [page],
            0,
            new PrintTicketSpec(300.0),
            DateTimeOffset.UtcNow);
    }

    private static ZiwaDocument CreateBlankFreeformA4Doc()
    {
        PaperPreset paper = PaperPresets.A4;
        var page = new PageModel(
            PageId.New(),
            "ورقة حرة A4",
            paper.WidthMm,
            paper.HeightMm,
            0.0,
            paper.DefaultSafeMarginMm,
            new SolidBackground(ColorRgba.White),
            []);

        return new ZiwaDocument(
            DocumentId.New(),
            1,
            "تصميم حر جديد A4",
            EditorWorkflow.Freeform,
            [page],
            0,
            new PrintTicketSpec(300.0),
            DateTimeOffset.UtcNow);
    }
}
