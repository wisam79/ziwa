using System.Collections.Immutable;
using Xunit;
using Ziwa.Application.Interaction;
using Ziwa.Application.Session;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class FreeformTransformGroupClipboardTests
{
    private static ZiwaDocument CreateSceneDoc(out NodeId imageId, out NodeId shapeId, out NodeId lockedId)
    {
        imageId = NodeId.New();
        shapeId = NodeId.New();
        lockedId = NodeId.New();

        var asset = new AssetId("asset-photo");
        var img = new ImageNode(imageId, "صورة", new RectMm(20, 20, 40, 30), asset, asset, CropSpec.Identity, RotationDeg: 45.0);
        var shape = new ShapeNode(shapeId, "نجمة", new RectMm(80, 20, 40, 40), ShapeKind.Star, new SolidShapeFill(ColorRgba.Black));
        var locked = new ShapeNode(lockedId, "مقفل", new RectMm(20, 20, 40, 30), ShapeKind.Rectangle, new SolidShapeFill(ColorRgba.White), IsLocked: true);

        var page = new PageModel(
            PageId.New(),
            "صفحة اختبار",
            210,
            297,
            0,
            5,
            new SolidBackground(ColorRgba.White),
            [img, shape, locked]);

        return new ZiwaDocument(
            DocumentId.New(),
            1,
            "مشهد حر",
            EditorWorkflow.Freeform,
            [page],
            0,
            new PrintTicketSpec(300.0),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public void HitTestEngine_IgnoresLockedAndHitsRotatedNodeAndHandles()
    {
        ZiwaDocument doc = CreateSceneDoc(out NodeId imageId, out NodeId shapeId, out _);
        PageModel page = doc.ActivePage;

        // Topmost at (40, 35) overlaps locked rect AND rotated image -> must skip locked and return image
        SceneNode? hit = HitTestEngine.HitTestTopmostNode(page, 40, 35);
        Assert.NotNull(hit);
        Assert.Equal(imageId, hit!.Id);

        SelectionBoundsInfo sel = HitTestEngine.ComputeSelectionBounds(page, [imageId]);
        HandlePointMm[] handles = HitTestEngine.GetHandleLocationsMm(sel.BoundsMm, sel.RotationDeg, 10.0);
        HandlePointMm rotHandle = handles.First(h => h.Kind == TransformHandleKind.Rotate);

        TransformHandleKind handleHit = HitTestEngine.HitTestHandles(
            sel.BoundsMm,
            sel.RotationDeg,
            rotHandle.XMm,
            rotHandle.YMm,
            handleToleranceMm: 2.0,
            rotateHandleOffsetMm: 10.0);
        Assert.Equal(TransformHandleKind.Rotate, handleHit);

        ImmutableArray<NodeId> marqueeHits = HitTestEngine.HitTestMarquee(page, new RectMm(10, 10, 120, 80));
        Assert.Equal(2, marqueeHits.Length);
        Assert.Contains(imageId, marqueeHits);
        Assert.Contains(shapeId, marqueeHits);
    }

    [Fact]
    public void ResizeWithAspectLock_And_Rotate15Snap_WorkAccurately()
    {
        ZiwaDocument doc = CreateSceneDoc(out _, out NodeId shapeId, out _);

        ZiwaDocument resized = FreeformSceneOperations.ResizeSelection(
            doc,
            [shapeId],
            TransformHandleKind.BottomRight,
            pointerXMm: 140.0,
            pointerYMm: 65.0,
            lockAspectRatio: true);
        SceneNode resizedNode = resized.ActivePage.Nodes.First(n => n.Id == shapeId);
        Assert.Equal(resizedNode.BoundsMm.Width, resizedNode.BoundsMm.Height, precision: 4);

        // Pointer directly right of center (100, 40) -> atan2(0, 20) + 90 = 90 deg
        ZiwaDocument rotated = FreeformSceneOperations.RotateSelection(
            doc,
            [shapeId],
            pointerXMm: 120.0,
            pointerYMm: 41.5,
            snap15Degrees: true);
        SceneNode rotatedNode = rotated.ActivePage.Nodes.First(n => n.Id == shapeId);
        Assert.Equal(90.0, rotatedNode.RotationDeg, precision: 6);
    }

    [Fact]
    public void GroupUngroup_And_MaskWithShape_AreSingleAtomicUndoSteps()
    {
        var session = new DocumentSession(CreateSceneDoc(out NodeId imageId, out NodeId shapeId, out _));

        NodeId groupId = NodeId.Empty;
        Assert.True(session.ApplyCommand("تجميع", doc => FreeformSceneOperations.GroupNodes(doc, [imageId, shapeId], out groupId)));
        Assert.Single(session.CommittedDocument.ActivePage.Nodes.OfType<GroupNode>());

        ImmutableArray<NodeId> restored = [];
        Assert.True(session.ApplyCommand("فك التجميع", doc => FreeformSceneOperations.UngroupNodes(doc, [groupId], out restored)));
        Assert.Empty(session.CommittedDocument.ActivePage.Nodes.OfType<GroupNode>());
        Assert.Equal(2, restored.Length);

        Assert.True(session.Undo()); // back to grouped
        Assert.Single(session.CommittedDocument.ActivePage.Nodes.OfType<GroupNode>());
        Assert.True(session.Undo()); // back to original ungrouped

        NodeId maskedShapeId = NodeId.Empty;
        Assert.True(session.ApplyCommand("قنّع بالشكل", doc =>
            FreeformSceneOperations.MaskSelectedImageWithShape(doc, [imageId, shapeId], out maskedShapeId)));

        ShapeNode maskedShape = Assert.IsType<ShapeNode>(session.CommittedDocument.ActivePage.Nodes.First(n => n.Id == maskedShapeId));
        Assert.IsType<ImageShapeFill>(maskedShape.Fill);
        Assert.DoesNotContain(session.CommittedDocument.ActivePage.Nodes, n => n.Id == imageId);

        Assert.True(session.Undo());
        Assert.Contains(session.CommittedDocument.ActivePage.Nodes, n => n.Id == imageId);
    }

    [Fact]
    public void ClipboardPayloadSerializer_ClonesValidPayloadsAndRejectsTamperedInputs()
    {
        ZiwaDocument doc = CreateSceneDoc(out NodeId imageId, out NodeId shapeId, out _);
        string payload = ClipboardPayloadSerializer.SerializeNodes(
            doc.ActivePage.Nodes.Where(n => n.Id == imageId || n.Id == shapeId));

        Assert.True(ClipboardPayloadSerializer.TryDeserializeAndClone(payload, out ImmutableArray<SceneNode> clones));
        Assert.Equal(2, clones.Length);
        Assert.NotEqual(imageId, clones[0].Id);
        Assert.Equal(25.0, clones[0].BoundsMm.X, precision: 6);

        string tampered = payload.Replace("\"width\":40", "\"width\":-5", StringComparison.Ordinal);
        Assert.False(ClipboardPayloadSerializer.TryDeserializeAndClone(tampered, out _));
        Assert.False(ClipboardPayloadSerializer.TryDeserializeAndClone("{not-json", out _));
    }
}
