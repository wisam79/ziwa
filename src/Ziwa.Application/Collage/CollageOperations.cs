using System.Collections.Immutable;
using Ziwa.Domain.Ai;
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

    public static ZiwaDocument ApplyProcessedSlotAsset(
        ZiwaDocument doc,
        SlotId slotId,
        AssetId processedAssetId,
        bool syncAutoRepeat = true,
        PageId? targetPageId = null)
    {
        if (processedAssetId.IsEmpty)
        {
            return doc;
        }

        int pageIndex = ResolvePageIndex(doc, targetPageId);
        if (pageIndex < 0 || doc.Pages[pageIndex].CollageLayout is not { } layout)
        {
            return doc;
        }

        CollageSlot? target = layout.Slots.FirstOrDefault(s => s.Id == slotId);
        if (target is null || target.AssignedAssetId is not { IsEmpty: false } currentAsset)
        {
            return doc;
        }

        AssetId sourceOriginal = target.OriginalAssetId ?? currentAsset;
        bool syncAll = syncAutoRepeat && layout.AutoRepeatMode;

        ImmutableArray<CollageSlot> next = layout.Slots.Select(s =>
        {
            if (s.Id == slotId)
            {
                return s with { AssignedAssetId = processedAssetId, OriginalAssetId = sourceOriginal };
            }

            if (syncAll && (s.OriginalAssetId ?? s.AssignedAssetId) == sourceOriginal && s.AssignedAssetId is { IsEmpty: false })
            {
                return s with { AssignedAssetId = processedAssetId, OriginalAssetId = sourceOriginal };
            }

            return s;
        }).ToImmutableArray();

        return ReplaceCollageLayoutOnPage(doc, pageIndex, layout with { Slots = next });
    }

    public static ZiwaDocument ApplyUpscaledSlotAsset(
        ZiwaDocument doc,
        SlotId slotId,
        AssetId upscaledAssetId,
        int sourceWidthPx,
        int sourceHeightPx,
        int outputWidthPx,
        int outputHeightPx,
        bool syncAutoRepeat = true,
        PageId? targetPageId = null)
    {
        if (upscaledAssetId.IsEmpty)
        {
            return doc;
        }

        int pageIndex = ResolvePageIndex(doc, targetPageId);
        if (pageIndex < 0 || doc.Pages[pageIndex].CollageLayout is not { } layout)
        {
            return doc;
        }

        CollageSlot? target = layout.Slots.FirstOrDefault(s => s.Id == slotId);
        if (target is null || target.AssignedAssetId is not { IsEmpty: false } currentAsset)
        {
            return doc;
        }

        AssetId sourceOriginal = target.OriginalAssetId ?? currentAsset;
        bool syncAll = syncAutoRepeat && layout.AutoRepeatMode;

        static CropSpec RecalibrateSlotCrop(CollageSlot slot, int srcW, int srcH, int outW, int outH)
        {
            bool swapsAxes = slot.QuarterTurns is 1 or 3;
            double logicalW = swapsAxes ? slot.BoundsMm.Height : slot.BoundsMm.Width;
            double logicalH = swapsAxes ? slot.BoundsMm.Width : slot.BoundsMm.Height;
            return CropCalibrationMath.RecalibrateForResizedAsset(
                slot.EffectiveCrop,
                srcW,
                srcH,
                outW,
                outH,
                logicalW,
                logicalH);
        }

        ImmutableArray<CollageSlot> next = layout.Slots.Select(s =>
        {
            if (s.Id == slotId)
            {
                CropSpec nextCrop = RecalibrateSlotCrop(s, sourceWidthPx, sourceHeightPx, outputWidthPx, outputHeightPx);
                return s with
                {
                    AssignedAssetId = upscaledAssetId,
                    OriginalAssetId = sourceOriginal,
                    Crop = nextCrop,
                };
            }

            if (syncAll && (s.OriginalAssetId ?? s.AssignedAssetId) == sourceOriginal && s.AssignedAssetId is { IsEmpty: false })
            {
                CropSpec siblingCrop = RecalibrateSlotCrop(s, sourceWidthPx, sourceHeightPx, outputWidthPx, outputHeightPx);
                return s with
                {
                    AssignedAssetId = upscaledAssetId,
                    OriginalAssetId = sourceOriginal,
                    Crop = siblingCrop,
                };
            }

            return s;
        }).ToImmutableArray();

        return ReplaceCollageLayoutOnPage(doc, pageIndex, layout with { Slots = next });
    }

    public static ZiwaDocument ApplyFaceCropToSlot(
        ZiwaDocument doc,
        SlotId slotId,
        CropSpec framingCrop,
        bool syncAutoRepeat = true,
        PageId? targetPageId = null)
    {
        int pageIndex = ResolvePageIndex(doc, targetPageId);
        if (pageIndex < 0 || doc.Pages[pageIndex].CollageLayout is not { } layout)
        {
            return doc;
        }

        CollageSlot? target = layout.Slots.FirstOrDefault(s => s.Id == slotId);
        if (target is null || target.AssignedAssetId is not { IsEmpty: false })
        {
            return doc;
        }

        var clamped = new CropSpec(
            DomainMath.Clamp(framingCrop.Zoom, 1.0, 20.0),
            DomainMath.Clamp(framingCrop.PanX, -1.0, 1.0),
            DomainMath.Clamp(framingCrop.PanY, -1.0, 1.0));

        AssetId sourceOriginal = target.OriginalAssetId ?? target.AssignedAssetId.Value;
        double targetAspect = target.BoundsMm.Width / target.BoundsMm.Height;
        bool syncAll = syncAutoRepeat && layout.AutoRepeatMode;

        ImmutableArray<CollageSlot> next = layout.Slots.Select(s =>
        {
            if (s.Id == slotId)
            {
                return s with { Crop = clamped };
            }

            if (syncAll &&
                s.AssignedAssetId is { IsEmpty: false } &&
                (s.OriginalAssetId ?? s.AssignedAssetId) == sourceOriginal &&
                s.QuarterTurns == target.QuarterTurns &&
                s.FlipHorizontal == target.FlipHorizontal &&
                s.FlipVertical == target.FlipVertical &&
                Math.Abs((s.BoundsMm.Width / s.BoundsMm.Height) - targetAspect) <= 0.02)
            {
                return s with { Crop = clamped };
            }

            return s;
        }).ToImmutableArray();

        return ReplaceCollageLayoutOnPage(doc, pageIndex, layout with { Slots = next });
    }

    public static ZiwaDocument RestoreOriginalSlotImage(ZiwaDocument doc, SlotId slotId)
    {
        if (doc.ActivePage.CollageLayout is not { } layout)
        {
            return doc;
        }

        ImmutableArray<CollageSlot> next = layout.Slots.Select(s =>
        {
            if (s.Id != slotId || s.AssignedAssetId is null)
            {
                return s;
            }

            AssetId restored = s.OriginalAssetId ?? s.AssignedAssetId.Value;
            return s with
            {
                AssignedAssetId = restored,
                OriginalAssetId = restored,
                Crop = CropSpec.Identity,
                Mask = MaskShapeKind.None,
            };
        }).ToImmutableArray();

        return ReplaceCollageLayout(doc, layout with { Slots = next });
    }

    private static int ResolvePageIndex(ZiwaDocument doc, PageId? targetPageId)
    {
        if (targetPageId is not { IsEmpty: false } pid)
        {
            return doc.ActivePageIndex;
        }

        for (int i = 0; i < doc.Pages.Length; i++)
        {
            if (doc.Pages[i].Id == pid)
            {
                return i;
            }
        }

        return -1;
    }

    private static ZiwaDocument ReplaceCollageLayoutOnPage(ZiwaDocument doc, int pageIndex, CollageLayoutModel nextLayout)
    {
        PageModel nextPage = doc.Pages[pageIndex] with { CollageLayout = nextLayout };
        ImmutableArray<PageModel> nextPages = doc.Pages.SetItem(pageIndex, nextPage);
        return doc with { Pages = nextPages, UpdatedAtUtc = DateTimeOffset.UtcNow };
    }

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
