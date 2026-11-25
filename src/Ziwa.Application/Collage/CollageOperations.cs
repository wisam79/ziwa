using System.Collections.Immutable;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Collage;

public static class CollageOperations
{
    public static ZiwaDocument EnableOrUpdateCollageSpec(ZiwaDocument doc, CollageGridSpec spec)
    {
        PageModel page = doc.ActivePage;
        CollageLayoutModel nextLayout = PhysicalGridBuilder.BuildOrReflowLayout(
            page.WidthMm,
            page.HeightMm,
            spec,
            page.CollageLayout);

        PageModel nextPage = page with { CollageLayout = nextLayout };
        return UpdateActivePage(doc with { Mode = EditorWorkflow.Collage }, nextPage);
    }

    public static ZiwaDocument EnqueuePendingAssets(ZiwaDocument doc, IEnumerable<AssetId> assets)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        List<AssetId> incoming = assets.Where(a => !a.IsEmpty).ToList();
        if (incoming.Count == 0)
        {
            return doc;
        }

        if (layout.AutoRepeatMode)
        {
            return SetAutoRepeatMode(doc, enabled: true, seedAsset: incoming[0]);
        }

        ImmutableArray<AssetId> pending = layout.PendingAssets.IsDefault
            ? [.. incoming]
            : [.. layout.PendingAssets, .. incoming];

        return ReplaceCollageLayout(doc, layout with { PendingAssets = pending });
    }

    public static ZiwaDocument AutoDistributePendingToSlots(ZiwaDocument doc)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        if (layout.AutoRepeatMode)
        {
            AssetId? seed = layout.Slots.FirstOrDefault(s => s.AssignedAssetId is { IsEmpty: false })?.AssignedAssetId
                ?? layout.PendingAssets.FirstOrDefault(a => !a.IsEmpty);
            return seed is { IsEmpty: false } s ? SetAutoRepeatMode(doc, true, s) : doc;
        }

        if (layout.PendingAssets.IsDefaultOrEmpty)
        {
            return doc;
        }

        var queue = new Queue<AssetId>(layout.PendingAssets);
        var nextSlots = ImmutableArray.CreateBuilder<CollageSlot>(layout.Slots.Length);
        foreach (CollageSlot slot in layout.Slots)
        {
            if (slot.AssignedAssetId is null && queue.Count > 0)
            {
                AssetId nextAsset = queue.Dequeue();
                nextSlots.Add(slot with
                {
                    AssignedAssetId = nextAsset,
                    OriginalAssetId = nextAsset,
                    Crop = CropSpec.Identity,
                });
            }
            else
            {
                nextSlots.Add(slot);
            }
        }

        return ReplaceCollageLayout(doc, layout with
        {
            Slots = nextSlots.ToImmutable(),
            PendingAssets = [.. queue],
        });
    }

    public static ZiwaDocument SetAutoRepeatMode(ZiwaDocument doc, bool enabled, AssetId? seedAsset = null)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        if (!enabled)
        {
            return ReplaceCollageLayout(doc, layout with { AutoRepeatMode = false });
        }

        AssetId? source = seedAsset is { IsEmpty: false } s
            ? s
            : layout.Slots.FirstOrDefault(sl => sl.AssignedAssetId is { IsEmpty: false })?.AssignedAssetId
              ?? layout.PendingAssets.FirstOrDefault(a => !a.IsEmpty);

        if (source is not { IsEmpty: false } repeatAsset)
        {
            return ReplaceCollageLayout(doc, layout with { AutoRepeatMode = true });
        }

        ImmutableArray<CollageSlot> filled = layout.Slots
            .Select(sl => sl with
            {
                AssignedAssetId = repeatAsset,
                OriginalAssetId = repeatAsset,
                Crop = CropSpec.Identity,
            })
            .ToImmutableArray();

        return ReplaceCollageLayout(doc, layout with
        {
            Slots = filled,
            AutoRepeatMode = true,
        });
    }

    public static ZiwaDocument AssignAssetToSlot(ZiwaDocument doc, SlotId slotId, AssetId assetId)
    {
        if (doc.ActivePage.CollageLayout is not { } layout || assetId.IsEmpty)
        {
            return doc;
        }

        ImmutableArray<CollageSlot> updated = layout.Slots
            .Select(s => s.Id == slotId
                ? s with { AssignedAssetId = assetId, OriginalAssetId = assetId, Crop = CropSpec.Identity }
                : s)
            .ToImmutableArray();

        ImmutableArray<AssetId> pending = layout.PendingAssets.IsDefault
            ? []
            : layout.PendingAssets.Remove(assetId);

        return ReplaceCollageLayout(doc, layout with { Slots = updated, PendingAssets = pending });
    }

    public static ZiwaDocument SwapSlots(ZiwaDocument doc, SlotId firstSlotId, SlotId secondSlotId)
    {
        if (doc.ActivePage.CollageLayout is not { } layout || firstSlotId == secondSlotId)
        {
            return doc;
        }

        int i1 = -1;
        int i2 = -1;
        for (int i = 0; i < layout.Slots.Length; i++)
        {
            if (layout.Slots[i].Id == firstSlotId)
            {
                i1 = i;
            }
            else if (layout.Slots[i].Id == secondSlotId)
            {
                i2 = i;
            }
        }

        if (i1 < 0 || i2 < 0)
        {
            return doc;
        }

        CollageSlot s1 = layout.Slots[i1];
        CollageSlot s2 = layout.Slots[i2];

        CollageSlot swapped1 = s1 with
        {
            AssignedAssetId = s2.AssignedAssetId,
            OriginalAssetId = s2.OriginalAssetId,
            Crop = s2.Crop,
            QuarterTurns = s2.QuarterTurns,
            FlipHorizontal = s2.FlipHorizontal,
            FlipVertical = s2.FlipVertical,
            Filters = s2.Filters,
        };

        CollageSlot swapped2 = s2 with
        {
            AssignedAssetId = s1.AssignedAssetId,
            OriginalAssetId = s1.OriginalAssetId,
            Crop = s1.Crop,
            QuarterTurns = s1.QuarterTurns,
            FlipHorizontal = s1.FlipHorizontal,
            FlipVertical = s1.FlipVertical,
            Filters = s1.Filters,
        };

        ImmutableArray<CollageSlot> nextSlots = layout.Slots.SetItem(i1, swapped1).SetItem(i2, swapped2);
        return ReplaceCollageLayout(doc, layout with { Slots = nextSlots });
    }

    public static ZiwaDocument TransformSlotContent(
        ZiwaDocument doc,
        SlotId slotId,
        CropSpec? crop = null,
        int? quarterTurnDelta = null,
        bool toggleFlipH = false,
        bool toggleFlipV = false)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        ImmutableArray<CollageSlot> next = layout.Slots.Select(s =>
        {
            if (s.Id != slotId)
            {
                return s;
            }

            CropSpec nextCrop = crop is null
                ? s.EffectiveCrop
                : new CropSpec(
                    DomainMath.Clamp(crop.Zoom, 1.0, 20.0),
                    DomainMath.Clamp(crop.PanX, -1.0, 1.0),
                    DomainMath.Clamp(crop.PanY, -1.0, 1.0));

            int nextTurns = quarterTurnDelta is int delta
                ? (((s.QuarterTurns + delta) % 4) + 4) % 4
                : s.QuarterTurns;

            return s with
            {
                Crop = nextCrop,
                QuarterTurns = nextTurns,
                FlipHorizontal = toggleFlipH ? !s.FlipHorizontal : s.FlipHorizontal,
                FlipVertical = toggleFlipV ? !s.FlipVertical : s.FlipVertical,
            };
        }).ToImmutableArray();

        return ReplaceCollageLayout(doc, layout with { Slots = next });
    }

    public static ZiwaDocument ClearSlot(ZiwaDocument doc, SlotId slotId)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        ImmutableArray<CollageSlot> next = layout.Slots.Select(s =>
            s.Id == slotId
                ? s with
                {
                    AssignedAssetId = null,
                    OriginalAssetId = null,
                    Crop = CropSpec.Identity,
                    QuarterTurns = 0,
                    FlipHorizontal = false,
                    FlipVertical = false,
                    Filters = null,
                }
                : s).ToImmutableArray();

        return ReplaceCollageLayout(doc, layout with { Slots = next });
    }

    public static ZiwaDocument ClearAllSlots(ZiwaDocument doc)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        ImmutableArray<CollageSlot> next = layout.Slots.Select(s => s with
        {
            AssignedAssetId = null,
            OriginalAssetId = null,
            Crop = CropSpec.Identity,
            QuarterTurns = 0,
            FlipHorizontal = false,
            FlipVertical = false,
            Filters = null,
        }).ToImmutableArray();

        return ReplaceCollageLayout(doc, layout with { Slots = next });
    }

    public static ZiwaDocument ApplyFiltersToSlot(ZiwaDocument doc, SlotId slotId, ImageFilterSpec filters)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        ImmutableArray<CollageSlot> next = layout.Slots
            .Select(s => s.Id == slotId ? s with { Filters = filters } : s)
            .ToImmutableArray();

        return ReplaceCollageLayout(doc, layout with { Slots = next });
    }

    public static ZiwaDocument ApplyFiltersToAllSlots(ZiwaDocument doc, ImageFilterSpec filters)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        ImmutableArray<CollageSlot> next = layout.Slots
            .Select(s => s with { Filters = filters })
            .ToImmutableArray();

        return ReplaceCollageLayout(doc, layout with { Slots = next });
    }

    public static ZiwaDocument ResetSlotFilters(ZiwaDocument doc, SlotId slotId) =>
        ApplyFiltersToSlot(doc, slotId, ImageFilterSpec.None);

    private static ZiwaDocument ReplaceCollageLayout(ZiwaDocument doc, CollageLayoutModel nextLayout)
    {
        PageModel nextPage = doc.ActivePage with { CollageLayout = nextLayout };
        return UpdateActivePage(doc, nextPage);
    }

    private static ZiwaDocument UpdateActivePage(ZiwaDocument doc, PageModel nextPage)
    {
        ImmutableArray<PageModel> nextPages = doc.Pages.SetItem(doc.ActivePageIndex, nextPage);
        return doc with { Pages = nextPages, UpdatedAtUtc = DateTimeOffset.UtcNow };
    }
}
