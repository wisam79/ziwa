using Ziwa.Application.Abstractions;
using Ziwa.Application.Collage;
using Ziwa.Application.Interaction;
using Ziwa.Application.Session;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Bridge;

public sealed record PhoneBridgeInsertOutcome(
    bool InsertedIntoCollageSlot,
    string StatusMessageAr,
    NodeId? InsertedNodeId = null,
    SlotId? TargetSlotId = null);

/// <summary>
/// Inserts photos received from Phone Bridge into the active collage slot or freeform canvas
/// with full Undo support and automatic advancement to the next empty slot.
/// </summary>
public static class PhoneBridgeCanvasInserter
{
    public static PhoneBridgeInsertOutcome InsertReceivedPhoto(
        DocumentSession session,
        PhoneBridgeReceivedPhoto photo)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(photo);

        PageModel page = session.CommittedDocument.ActivePage;

        if (page.CollageLayout is { } layout && layout.Slots.Length > 0)
        {
            SlotId? selectedSlotId = session.SelectedCollageSlotId;
            CollageSlot? selectedSlot = selectedSlotId is { } sid
                ? layout.Slots.FirstOrDefault(s => s.Id == sid)
                : null;
            CollageSlot? firstEmptySlot = layout.Slots.FirstOrDefault(s => s.AssignedAssetId is null);

            CollageSlot? targetSlot = selectedSlot is { AssignedAssetId: null }
                ? selectedSlot
                : firstEmptySlot ?? selectedSlot ?? layout.Slots[0];

            if (targetSlot is not null)
            {
                session.ApplyCommand(
                    "استلام صورة من الهاتف في خانة الكولاج",
                    doc => CollageOperations.AssignAssetToSlot(doc, targetSlot.Id, photo.AssetId));

                CollageSlot? nextEmpty = session.CommittedDocument.ActivePage.CollageLayout?.Slots
                    .FirstOrDefault(s => s.Id != targetSlot.Id && s.AssignedAssetId is null);

                session.SelectCollageSlot(nextEmpty?.Id ?? targetSlot.Id);

                return new PhoneBridgeInsertOutcome(
                    InsertedIntoCollageSlot: true,
                    StatusMessageAr: "تم استلام الصورة من الهاتف وإدراجها في خانة الكولاج",
                    TargetSlotId: targetSlot.Id);
            }
        }

        double aspect = photo.WidthPx > 0 && photo.HeightPx > 0
            ? (double)photo.WidthPx / photo.HeightPx
            : 1.0;

        double targetW = Math.Clamp(page.WidthMm * 0.45, 24.0, Math.Max(24.0, page.WidthMm - 16.0));
        double targetH = Math.Clamp(targetW / Math.Max(0.2, aspect), 20.0, Math.Max(20.0, page.HeightMm - 16.0));

        string displayName = string.IsNullOrWhiteSpace(photo.DisplayName)
            ? $"صورة الهاتف {photo.ReceivedAtUtc.ToLocalTime():HH:mm:ss}"
            : photo.DisplayName.Trim();

        var node = new ImageNode(
            Id: NodeId.New(),
            Name: displayName,
            BoundsMm: new RectMm(
                Math.Max(4.0, (page.WidthMm - targetW) * 0.5),
                Math.Max(4.0, (page.HeightMm - targetH) * 0.5),
                targetW,
                targetH),
            CurrentAssetId: photo.AssetId,
            OriginalAssetId: photo.AssetId,
            Crop: CropSpec.Identity);

        session.ApplyCommand("إدراج صورة من الهاتف", doc => FreeformSceneOperations.AddNode(doc, node));
        session.SetSelection([node.Id]);

        return new PhoneBridgeInsertOutcome(
            InsertedIntoCollageSlot: false,
            StatusMessageAr: "تم استلام الصورة من الهاتف وإدراجها في الورقة",
            InsertedNodeId: node.Id);
    }
}
