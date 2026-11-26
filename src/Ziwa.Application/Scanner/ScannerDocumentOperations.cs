using System.Collections.Immutable;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Scanner;

public static class ScannerDocumentOperations
{
    public static ZiwaDocument InsertExtractedIntoFreeform(
        ZiwaDocument doc,
        IReadOnlyList<ExtractedScanDocument> extracted,
        out ImmutableArray<NodeId> insertedNodeIds,
        PageId? targetPageId = null)
    {
        if (extracted is null || extracted.Count == 0)
        {
            insertedNodeIds = [];
            return doc;
        }

        int pageIndex = ResolvePageIndex(doc, targetPageId);
        if (pageIndex < 0)
        {
            insertedNodeIds = [];
            return doc;
        }

        PageModel page = doc.Pages[pageIndex];
        double safeMargin = System.Math.Max(4.0, page.SafeMarginMm);
        double maxSpanW = System.Math.Max(20.0, page.WidthMm - (safeMargin * 2.0));
        double maxSpanH = System.Math.Max(20.0, page.HeightMm - (safeMargin * 2.0));

        var addedNodes = ImmutableArray.CreateBuilder<SceneNode>(extracted.Count);
        var addedIds = ImmutableArray.CreateBuilder<NodeId>(extracted.Count);

        int count = extracted.Count;
        int cols = count <= 1 ? 1 : 2;
        int rows = (int)System.Math.Ceiling(count / (double)cols);
        double gapMm = 5.0;
        double cellMaxW = (maxSpanW - ((cols - 1) * gapMm)) / cols;
        double cellMaxH = (maxSpanH - ((rows - 1) * gapMm)) / rows;

        for (int i = 0; i < count; i++)
        {
            ExtractedScanDocument item = extracted[i];
            double aspect = (double)item.WidthPx / System.Math.Max(1, item.HeightPx);

            // Use physical CR80 dimensions when aspect matches standard ID card and fits comfortably.
            double widthMm;
            double heightMm;
            if (aspect is >= 1.45 and <= 1.72 && cellMaxW >= 85.6 && cellMaxH >= 54.0)
            {
                widthMm = 85.6;
                heightMm = 54.0;
            }
            else if (aspect is >= 0.58 and <= 0.69 && cellMaxW >= 54.0 && cellMaxH >= 85.6)
            {
                widthMm = 54.0;
                heightMm = 85.6;
            }
            else
            {
                double targetFrac = count == 1 ? 0.68 : 0.92;
                double boxW = cellMaxW * targetFrac;
                double boxH = cellMaxH * targetFrac;
                if (boxW / System.Math.Max(0.1, aspect) <= boxH)
                {
                    widthMm = System.Math.Max(16.0, boxW);
                    heightMm = System.Math.Max(16.0, widthMm / aspect);
                }
                else
                {
                    heightMm = System.Math.Max(16.0, boxH);
                    widthMm = System.Math.Max(16.0, heightMm * aspect);
                }
            }

            int col = i % cols;
            int row = i / cols;
            double x = count == 1
                ? (page.WidthMm - widthMm) * 0.5
                : safeMargin + (col * (cellMaxW + gapMm)) + ((cellMaxW - widthMm) * 0.5);
            double y = count == 1
                ? (page.HeightMm - heightMm) * 0.5
                : safeMargin + (row * (cellMaxH + gapMm)) + ((cellMaxH - heightMm) * 0.5);

            x = System.Math.Clamp(x, 0.0, System.Math.Max(0.0, page.WidthMm - widthMm));
            y = System.Math.Clamp(y, 0.0, System.Math.Max(0.0, page.HeightMm - heightMm));

            var node = new ImageNode(
                Id: NodeId.New(),
                Name: item.LabelAr,
                BoundsMm: new RectMm(x, y, widthMm, heightMm),
                CurrentAssetId: item.FilteredAssetId,
                OriginalAssetId: item.UnfilteredAssetId,
                Crop: CropSpec.Identity);

            addedNodes.Add(node);
            addedIds.Add(node.Id);
        }

        PageModel nextPage = page with { Nodes = page.Nodes.AddRange(addedNodes) };
        ImmutableArray<PageModel> nextPages = doc.Pages.SetItem(pageIndex, nextPage);
        insertedNodeIds = addedIds.ToImmutable();
        return doc with { Pages = nextPages, UpdatedAtUtc = DateTimeOffset.UtcNow };
    }

    public static ZiwaDocument InsertExtractedIntoCollage(
        ZiwaDocument doc,
        IReadOnlyList<ExtractedScanDocument> extracted,
        SlotId? preferredSlotId = null,
        PageId? targetPageId = null)
    {
        if (extracted is null || extracted.Count == 0)
        {
            return doc;
        }

        int pageIndex = ResolvePageIndex(doc, targetPageId);
        if (pageIndex < 0)
        {
            return doc;
        }

        PageModel page = doc.Pages[pageIndex];
        CollageLayoutModel layout = page.CollageLayout ?? CreateDefaultScannerCollage(page, extracted.Count);
        if (layout.Slots.IsDefaultOrEmpty)
        {
            return doc;
        }

        var slots = layout.Slots.ToArray();
        var pending = layout.PendingAssets.IsDefault
            ? new List<AssetId>()
            : layout.PendingAssets.ToList();

        if (extracted.Count == 1 && preferredSlotId is { } targetSid)
        {
            int idx = Array.FindIndex(slots, s => s.Id == targetSid);
            if (idx >= 0)
            {
                ExtractedScanDocument single = extracted[0];
                slots[idx] = slots[idx] with
                {
                    AssignedAssetId = single.FilteredAssetId,
                    OriginalAssetId = single.UnfilteredAssetId,
                    Crop = CropSpec.Identity,
                };
                return ApplyCollageLayout(doc, pageIndex, layout with { Slots = [.. slots], AutoRepeatMode = false });
            }
        }

        var assignedIndices = new HashSet<int>();
        int nextItem = 0;

        if (preferredSlotId is { } prefSid)
        {
            int prefIdx = Array.FindIndex(slots, s => s.Id == prefSid);
            if (prefIdx >= 0 && slots[prefIdx].AssignedAssetId is null && nextItem < extracted.Count)
            {
                ExtractedScanDocument item = extracted[nextItem++];
                slots[prefIdx] = slots[prefIdx] with
                {
                    AssignedAssetId = item.FilteredAssetId,
                    OriginalAssetId = item.UnfilteredAssetId,
                    Crop = CropSpec.Identity,
                };
                assignedIndices.Add(prefIdx);
            }
        }

        for (int i = 0; i < slots.Length && nextItem < extracted.Count; i++)
        {
            if (assignedIndices.Contains(i) || slots[i].AssignedAssetId is not null)
            {
                continue;
            }

            ExtractedScanDocument item = extracted[nextItem++];
            slots[i] = slots[i] with
            {
                AssignedAssetId = item.FilteredAssetId,
                OriginalAssetId = item.UnfilteredAssetId,
                Crop = CropSpec.Identity,
            };
            assignedIndices.Add(i);
        }

        // If all slots were already occupied, replace starting from slot 0.
        for (int i = 0; i < slots.Length && nextItem < extracted.Count; i++)
        {
            if (assignedIndices.Contains(i))
            {
                continue;
            }

            ExtractedScanDocument item = extracted[nextItem++];
            slots[i] = slots[i] with
            {
                AssignedAssetId = item.FilteredAssetId,
                OriginalAssetId = item.UnfilteredAssetId,
                Crop = CropSpec.Identity,
            };
            assignedIndices.Add(i);
        }

        while (nextItem < extracted.Count)
        {
            pending.Add(extracted[nextItem++].FilteredAssetId);
        }

        CollageLayoutModel nextLayout = layout with
        {
            Slots = [.. slots],
            PendingAssets = [.. pending],
            AutoRepeatMode = false,
        };
        return ApplyCollageLayout(doc, pageIndex, nextLayout);
    }

    private static CollageLayoutModel CreateDefaultScannerCollage(PageModel page, int count)
    {
        int cols = count <= 1 ? 1 : 2;
        int rows = System.Math.Max(1, (int)System.Math.Ceiling(count / (double)cols));
        var spec = new CollageGridSpec(
            PresetId: "scanner-grid",
            SizingMode: CollageSizingMode.FitGridToPaper,
            Rows: rows,
            Columns: cols,
            CellWidthMm: 85.6,
            CellHeightMm: 54.0,
            GapXMm: 4.0,
            GapYMm: 4.0,
            MarginMm: System.Math.Max(4.0, page.SafeMarginMm));
        return PhysicalGridBuilder.BuildOrReflowLayout(page.WidthMm, page.HeightMm, spec);
    }

    private static ZiwaDocument ApplyCollageLayout(ZiwaDocument doc, int pageIndex, CollageLayoutModel nextLayout)
    {
        PageModel nextPage = doc.Pages[pageIndex] with { CollageLayout = nextLayout };
        ImmutableArray<PageModel> nextPages = doc.Pages.SetItem(pageIndex, nextPage);
        return doc with
        {
            Mode = EditorWorkflow.Collage,
            Pages = nextPages,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
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
}
