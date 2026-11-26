using Ziwa.Application.Session;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Ai;

public readonly record struct AiPhotoTarget(
    PageId PageId,
    NodeId? NodeId,
    SlotId? SlotId,
    AssetId CurrentAssetId,
    AssetId OriginalAssetId,
    double FrameWidthMm,
    double FrameHeightMm,
    int QuarterTurns,
    bool FlipHorizontal,
    bool FlipVertical,
    string DisplayName);

internal static class AiPhotoTargetResolver
{
    public static bool TryResolveSelectedPhotoTarget(DocumentSession session, out AiPhotoTarget target)
    {
        PageModel page = session.CommittedDocument.ActivePage;

        if (session.SelectedCollageSlotId is { } slotId &&
            page.CollageLayout?.Slots.FirstOrDefault(s => s.Id == slotId) is { AssignedAssetId: { IsEmpty: false } slotAsset } slot)
        {
            bool swapsAxes = slot.QuarterTurns is 1 or 3;
            double logicalW = swapsAxes ? slot.BoundsMm.Height : slot.BoundsMm.Width;
            double logicalH = swapsAxes ? slot.BoundsMm.Width : slot.BoundsMm.Height;
            target = new AiPhotoTarget(
                PageId: page.Id,
                NodeId: null,
                SlotId: slot.Id,
                CurrentAssetId: slotAsset,
                OriginalAssetId: slot.OriginalAssetId ?? slotAsset,
                FrameWidthMm: logicalW,
                FrameHeightMm: logicalH,
                QuarterTurns: slot.QuarterTurns,
                FlipHorizontal: slot.FlipHorizontal,
                FlipVertical: slot.FlipVertical,
                DisplayName: $"خانة كولاج #{slot.Index + 1}");
            return true;
        }

        if (session.SelectedNodeIds.Length == 1)
        {
            NodeId id = session.SelectedNodeIds[0];
            if (page.Nodes.FirstOrDefault(n => n.Id == id) is ImageNode img && !img.IsLocked)
            {
                target = new AiPhotoTarget(
                    PageId: page.Id,
                    NodeId: img.Id,
                    SlotId: null,
                    CurrentAssetId: img.CurrentAssetId,
                    OriginalAssetId: img.OriginalAssetId,
                    FrameWidthMm: img.BoundsMm.Width,
                    FrameHeightMm: img.BoundsMm.Height,
                    QuarterTurns: 0,
                    FlipHorizontal: false,
                    FlipVertical: false,
                    DisplayName: img.Name);
                return true;
            }
        }

        target = default;
        return false;
    }
}
