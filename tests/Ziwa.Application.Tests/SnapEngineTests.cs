using Xunit;
using Ziwa.Application.Interaction;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class SnapEngineTests
{
    private static PageModel CreatePageWithNodes(params SceneNode[] nodes)
    {
        return new PageModel(
            PageId.New(),
            "صفحة محاذاة",
            200.0,
            200.0,
            0.0,
            5.0,
            new SolidBackground(ColorRgba.White),
            [.. nodes]);
    }

    [Fact]
    public void ComputeMoveSnap_SnapsToPageCenterAndSiblingEqualSpacing()
    {
        NodeId n1 = NodeId.New();
        NodeId n2 = NodeId.New();
        NodeId moving = NodeId.New();

        // n1 at [10..30], n2 at [50..70] -> gap is 20mm. Equal gap after n2 places moving.X at 90mm!
        PageModel page = CreatePageWithNodes(
            new ShapeNode(n1, "A", new RectMm(10, 20, 20, 20), ShapeKind.Rectangle, new SolidShapeFill(ColorRgba.Black)),
            new ShapeNode(n2, "B", new RectMm(50, 20, 20, 20), ShapeKind.Rectangle, new SolidShapeFill(ColorRgba.Black)),
            new ShapeNode(moving, "C", new RectMm(88.8, 20.6, 20, 20), ShapeKind.Rectangle, new SolidShapeFill(ColorRgba.Black)));

        SnapResult snap = SnapEngine.ComputeMoveSnap(
            new RectMm(88.8, 20.6, 20, 20),
            page,
            new HashSet<NodeId> { moving },
            toleranceMm: 2.0,
            bypassSnap: false);

        Assert.Equal(90.0, snap.SnappedBoundsMm.X, precision: 6);
        Assert.Equal(20.0, snap.SnappedBoundsMm.Y, precision: 6);
        Assert.Equal(2, snap.Guides.Length);
    }

    [Fact]
    public void ComputeMoveSnap_WhenBypassedWithAlt_DoesNotModifyBounds()
    {
        NodeId moving = NodeId.New();
        PageModel page = CreatePageWithNodes(
            new ShapeNode(moving, "M", new RectMm(90.5, 90.5, 20, 20), ShapeKind.Rectangle, new SolidShapeFill(ColorRgba.Black)));

        var candidate = new RectMm(90.5, 90.5, 20, 20);
        SnapResult bypassed = SnapEngine.ComputeMoveSnap(candidate, page, new HashSet<NodeId> { moving }, 2.0, bypassSnap: true);

        Assert.Equal(candidate, bypassed.SnappedBoundsMm);
        Assert.Empty(bypassed.Guides);
    }
}
