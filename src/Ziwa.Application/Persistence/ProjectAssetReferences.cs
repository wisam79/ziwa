using System.Collections.Immutable;
using System.IO;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Persistence;

public static class ProjectAssetReferences
{
    public static ImmutableArray<AssetId> Collect(ZiwaDocument document)
    {
        var ids = new HashSet<AssetId>();
        foreach (PageModel page in document.Pages)
        {
            foreach (SceneNode node in page.Nodes)
            {
                CollectNode(node, ids);
            }

            if (page.CollageLayout is { } layout)
            {
                foreach (var slot in layout.Slots)
                {
                    if (slot.AssignedAssetId is { } assigned) { AddRaster(assigned, ids); }
                    if (slot.OriginalAssetId is { } original) { AddRaster(original, ids); }
                }

                if (!layout.PendingAssets.IsDefault)
                {
                    foreach (AssetId pending in layout.PendingAssets) { AddRaster(pending, ids); }
                }
            }
        }

        return ids.OrderBy(id => id.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    private static void AddRaster(AssetId id, HashSet<AssetId> ids)
    {
        if (id.IsEmpty || id.Value.StartsWith("svg-", StringComparison.Ordinal)) { throw new InvalidDataException("Image reference must identify a raster asset."); }
        ids.Add(id);
    }

    private static void CollectNode(SceneNode node, HashSet<AssetId> ids)
    {
        switch (node)
        {
            case ImageNode image:
                AddRaster(image.CurrentAssetId, ids);
                AddRaster(image.OriginalAssetId, ids);
                break;
            case ShapeNode { Fill: ImageShapeFill image }:
                AddRaster(image.CurrentAssetId, ids);
                AddRaster(image.OriginalAssetId, ids);
                break;
            case SvgNode svg:
                if (!svg.SvgAssetId.Value.StartsWith("svg-", StringComparison.Ordinal)) { throw new InvalidDataException("SVG reference must identify a vector asset."); }
                ids.Add(svg.SvgAssetId);
                break;
            case GroupNode group:
                foreach (SceneNode child in group.Children) { CollectNode(child, ids); }
                break;
        }
    }
}
