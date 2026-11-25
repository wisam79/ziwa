using System.Collections.Immutable;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Stickers;

namespace Ziwa.Application.Stickers;

public static class StickerOperations
{
    public static StickerNode CreateStickerFromTemplate(
        string templateId,
        double centerXMm,
        double centerYMm,
        int? randomPaletteSeed = null)
    {
        StickerTemplateDefinition def = ParametricStickerCatalog.ResolveTemplate(templateId);
        ImmutableDictionary<string, string> fields = def.Fields.ToImmutableDictionary(f => f.Key, f => f.DefaultValue);
        StickerPalette palette = randomPaletteSeed is int seed
            ? OklchPaletteGenerator.GenerateSmartRandomPalette(seed)
            : def.DefaultPalette;

        var bounds = new RectMm(
            centerXMm - (def.DefaultWidthMm * 0.5),
            centerYMm - (def.DefaultHeightMm * 0.5),
            def.DefaultWidthMm,
            def.DefaultHeightMm);

        return new StickerNode(
            Id: NodeId.New(),
            Name: def.TitleAr,
            BoundsMm: bounds,
            TemplateId: def.Id,
            Fields: fields,
            Palette: palette);
    }

    public static ZiwaDocument UpdateStickerInPlace(
        ZiwaDocument doc,
        NodeId stickerId,
        string templateId,
        ImmutableDictionary<string, string> fields,
        StickerPalette palette)
    {
        PageModel page = doc.ActivePage;
        int idx = -1;
        StickerNode? existing = null;

        for (int i = 0; i < page.Nodes.Length; i++)
        {
            if (page.Nodes[i].Id == stickerId && page.Nodes[i] is StickerNode s)
            {
                idx = i;
                existing = s;
                break;
            }
        }

        if (existing is null || existing.IsLocked)
        {
            return doc;
        }

        StickerTemplateDefinition def = ParametricStickerCatalog.ResolveTemplate(templateId);
        StickerNode updated = existing with
        {
            Name = def.TitleAr,
            TemplateId = def.Id,
            Fields = fields,
            Palette = palette,
        };

        PageModel nextPage = page with { Nodes = page.Nodes.SetItem(idx, updated) };
        return doc with
        {
            Pages = doc.Pages.SetItem(doc.ActivePageIndex, nextPage),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
