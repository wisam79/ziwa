using System.Collections.Immutable;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Interaction;

public static class FreeformSceneOperations
{
    public static ZiwaDocument AddNode(ZiwaDocument doc, SceneNode node)
    {
        PageModel page = doc.ActivePage;
        return ReplaceActivePageNodes(doc, page.Nodes.Add(node));
    }

    public static ZiwaDocument TranslateNodes(
        ZiwaDocument doc,
        IReadOnlySet<NodeId> ids,
        double dxMm,
        double dyMm)
    {
        if (ids.Count == 0 || (Math.Abs(dxMm) < 0.00001 && Math.Abs(dyMm) < 0.00001))
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        ImmutableArray<SceneNode> updated = page.Nodes
            .Select(n => ids.Contains(n.Id) && !n.IsLocked ? TranslateNodeRecursive(n, dxMm, dyMm) : n)
            .ToImmutableArray();

        return ReplaceActivePageNodes(doc, updated);
    }

    public static ZiwaDocument ResizeSelection(
        ZiwaDocument doc,
        ImmutableArray<NodeId> selectedIds,
        TransformHandleKind handle,
        double pointerXMm,
        double pointerYMm,
        bool lockAspectRatio,
        double minSizeMm = 2.0)
    {
        if (selectedIds.IsDefaultOrEmpty || handle is TransformHandleKind.None or TransformHandleKind.Rotate)
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        if (selectedIds.Length == 1)
        {
            SceneNode? target = page.Nodes.FirstOrDefault(n => n.Id == selectedIds[0]);
            if (target is null || target.IsLocked)
            {
                return doc;
            }

            RectMm newBounds = ComputeSingleResizedBounds(
                target.BoundsMm,
                target.RotationDeg,
                handle,
                pointerXMm,
                pointerYMm,
                lockAspectRatio,
                minSizeMm);

            SceneNode updatedNode = ResizeSingleNodeRecursive(target, newBounds);
            return ReplaceNode(doc, target.Id, updatedNode);
        }

        SelectionBoundsInfo selInfo = HitTestEngine.ComputeSelectionBounds(page, selectedIds);
        if (!selInfo.HasSelection)
        {
            return doc;
        }

        RectMm oldBox = selInfo.BoundsMm;
        RectMm newBox = ComputeAxisAlignedResizedBounds(oldBox, handle, pointerXMm, pointerYMm, lockAspectRatio, minSizeMm);
        double scaleX = newBox.Width / oldBox.Width;
        double scaleY = newBox.Height / oldBox.Height;

        HashSet<NodeId> idSet = selectedIds.ToHashSet();
        ImmutableArray<SceneNode> updated = page.Nodes.Select(n =>
        {
            if (!idSet.Contains(n.Id) || n.IsLocked)
            {
                return n;
            }

            var mapped = new RectMm(
                newBox.X + ((n.BoundsMm.X - oldBox.X) * scaleX),
                newBox.Y + ((n.BoundsMm.Y - oldBox.Y) * scaleY),
                Math.Max(minSizeMm, n.BoundsMm.Width * scaleX),
                Math.Max(minSizeMm, n.BoundsMm.Height * scaleY));
            return ResizeSingleNodeRecursive(n, mapped);
        }).ToImmutableArray();

        return ReplaceActivePageNodes(doc, updated);
    }

    public static ZiwaDocument RotateSelection(
        ZiwaDocument doc,
        ImmutableArray<NodeId> selectedIds,
        double pointerXMm,
        double pointerYMm,
        bool snap15Degrees)
    {
        if (selectedIds.IsDefaultOrEmpty)
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        SelectionBoundsInfo selInfo = HitTestEngine.ComputeSelectionBounds(page, selectedIds);
        if (!selInfo.HasSelection)
        {
            return doc;
        }

        double cx = selInfo.BoundsMm.CenterX;
        double cy = selInfo.BoundsMm.CenterY;
        double angleDeg = (Math.Atan2(pointerYMm - cy, pointerXMm - cx) * 180.0 / Math.PI) + 90.0;

        if (snap15Degrees)
        {
            angleDeg = Math.Round(angleDeg / 15.0, MidpointRounding.AwayFromZero) * 15.0;
        }

        if (selectedIds.Length == 1)
        {
            SceneNode? node = page.Nodes.FirstOrDefault(n => n.Id == selectedIds[0]);
            if (node is null || node.IsLocked)
            {
                return doc;
            }

            return ReplaceNode(doc, node.Id, node with { RotationDeg = angleDeg });
        }

        HashSet<NodeId> idSet = selectedIds.ToHashSet();
        ImmutableArray<SceneNode> updated = page.Nodes.Select(n =>
        {
            if (!idSet.Contains(n.Id) || n.IsLocked)
            {
                return n;
            }

            (double nx, double ny) = HitTestEngine.RotatePoint(n.BoundsMm.CenterX, n.BoundsMm.CenterY, cx, cy, angleDeg);
            var rotatedBounds = n.BoundsMm with
            {
                X = nx - (n.BoundsMm.Width * 0.5),
                Y = ny - (n.BoundsMm.Height * 0.5),
            };
            return n with { BoundsMm = rotatedBounds, RotationDeg = n.RotationDeg + angleDeg };
        }).ToImmutableArray();

        return ReplaceActivePageNodes(doc, updated);
    }

    public static ZiwaDocument BringForward(ZiwaDocument doc, ImmutableArray<NodeId> selectedIds) =>
        ReorderNodes(doc, selectedIds, ZOrderAction.Forward);

    public static ZiwaDocument SendBackward(ZiwaDocument doc, ImmutableArray<NodeId> selectedIds) =>
        ReorderNodes(doc, selectedIds, ZOrderAction.Backward);

    public static ZiwaDocument BringToFront(ZiwaDocument doc, ImmutableArray<NodeId> selectedIds) =>
        ReorderNodes(doc, selectedIds, ZOrderAction.Front);

    public static ZiwaDocument SendToBack(ZiwaDocument doc, ImmutableArray<NodeId> selectedIds) =>
        ReorderNodes(doc, selectedIds, ZOrderAction.Back);

    public static ZiwaDocument GroupNodes(
        ZiwaDocument doc,
        ImmutableArray<NodeId> selectedIds,
        out NodeId createdGroupId)
    {
        createdGroupId = NodeId.Empty;
        if (selectedIds.IsDefaultOrEmpty || selectedIds.Length < 2)
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        HashSet<NodeId> idSet = selectedIds.ToHashSet();
        List<SceneNode> members = [];
        int insertIndex = -1;

        for (int i = 0; i < page.Nodes.Length; i++)
        {
            SceneNode node = page.Nodes[i];
            if (idSet.Contains(node.Id) && !node.IsLocked)
            {
                members.Add(node);
                insertIndex = i;
            }
        }

        if (members.Count < 2)
        {
            return doc;
        }

        SelectionBoundsInfo bounds = HitTestEngine.ComputeSelectionBounds(
            page,
            members.Select(m => m.Id).ToImmutableArray());

        createdGroupId = NodeId.New();
        var group = new GroupNode(
            Id: createdGroupId,
            Name: $"مجموعة ({members.Count})",
            BoundsMm: bounds.BoundsMm,
            Children: [.. members]);

        var next = ImmutableArray.CreateBuilder<SceneNode>();
        bool inserted = false;
        for (int i = 0; i < page.Nodes.Length; i++)
        {
            SceneNode node = page.Nodes[i];
            if (idSet.Contains(node.Id) && !node.IsLocked)
            {
                if (i == insertIndex && !inserted)
                {
                    next.Add(group);
                    inserted = true;
                }

                continue;
            }

            next.Add(node);
        }

        return ReplaceActivePageNodes(doc, next.ToImmutable());
    }

    public static ZiwaDocument UngroupNodes(
        ZiwaDocument doc,
        ImmutableArray<NodeId> selectedIds,
        out ImmutableArray<NodeId> restoredChildIds)
    {
        var restoredBuilder = ImmutableArray.CreateBuilder<NodeId>();
        if (selectedIds.IsDefaultOrEmpty)
        {
            restoredChildIds = [];
            return doc;
        }

        PageModel page = doc.ActivePage;
        HashSet<NodeId> idSet = selectedIds.ToHashSet();
        var next = ImmutableArray.CreateBuilder<SceneNode>();
        bool changed = false;

        foreach (SceneNode node in page.Nodes)
        {
            if (idSet.Contains(node.Id) && !node.IsLocked && node is GroupNode group)
            {
                changed = true;
                foreach (SceneNode child in group.Children)
                {
                    SceneNode released = ReleaseChildFromGroup(group, child);
                    next.Add(released);
                    restoredBuilder.Add(released.Id);
                }
            }
            else
            {
                next.Add(node);
            }
        }

        restoredChildIds = restoredBuilder.ToImmutable();
        return changed ? ReplaceActivePageNodes(doc, next.ToImmutable()) : doc;
    }

    public static ZiwaDocument DeleteNodes(ZiwaDocument doc, IReadOnlySet<NodeId> ids)
    {
        if (ids.Count == 0)
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        ImmutableArray<SceneNode> remaining = page.Nodes
            .Where(n => !ids.Contains(n.Id) || n.IsLocked)
            .ToImmutableArray();

        return remaining.Length == page.Nodes.Length ? doc : ReplaceActivePageNodes(doc, remaining);
    }

    public static ZiwaDocument UpdateImageCrop(ZiwaDocument doc, NodeId imageNodeId, CropSpec newCrop)
    {
        SceneNode? node = doc.ActivePage.Nodes.FirstOrDefault(n => n.Id == imageNodeId);
        if (node is not ImageNode img || img.IsLocked)
        {
            return doc;
        }

        var clamped = new CropSpec(
            DomainMath.Clamp(newCrop.Zoom, 1.0, 20.0),
            DomainMath.Clamp(newCrop.PanX, -1.0, 1.0),
            DomainMath.Clamp(newCrop.PanY, -1.0, 1.0));
        return ReplaceNode(doc, img.Id, img with { Crop = clamped });
    }

    public static ZiwaDocument RestoreOriginalImage(ZiwaDocument doc, NodeId imageNodeId)
    {
        SceneNode? node = doc.ActivePage.Nodes.FirstOrDefault(n => n.Id == imageNodeId);
        if (node is not ImageNode img || img.IsLocked)
        {
            return doc;
        }

        return ReplaceNode(doc, img.Id, img with
        {
            CurrentAssetId = img.OriginalAssetId,
            Crop = CropSpec.Identity,
            Mask = MaskShapeKind.None,
        });
    }

    public static ZiwaDocument SetImageMask(ZiwaDocument doc, NodeId imageNodeId, MaskShapeKind mask)
    {
        SceneNode? node = doc.ActivePage.Nodes.FirstOrDefault(n => n.Id == imageNodeId);
        if (node is not ImageNode img || img.IsLocked)
        {
            return doc;
        }

        return ReplaceNode(doc, img.Id, img with { Mask = mask });
    }

    public static ZiwaDocument ApplyImageFillToShape(ZiwaDocument doc, NodeId shapeId, AssetId imageAssetId)
    {
        if (imageAssetId.IsEmpty)
        {
            return doc;
        }

        SceneNode? node = doc.ActivePage.Nodes.FirstOrDefault(n => n.Id == shapeId);
        if (node is not ShapeNode shape || shape.IsLocked || shape.Kind is ShapeKind.Line)
        {
            return doc;
        }

        return ReplaceNode(doc, shape.Id, shape with
        {
            Fill = new ImageShapeFill(imageAssetId, imageAssetId, CropSpec.Identity),
        });
    }

    public static ZiwaDocument MaskSelectedImageWithShape(
        ZiwaDocument doc,
        ImmutableArray<NodeId> selectedIds,
        out NodeId resultingShapeId)
    {
        resultingShapeId = NodeId.Empty;
        if (selectedIds.IsDefaultOrEmpty || selectedIds.Length != 2)
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        SceneNode? n1 = page.Nodes.FirstOrDefault(n => n.Id == selectedIds[0]);
        SceneNode? n2 = page.Nodes.FirstOrDefault(n => n.Id == selectedIds[1]);

        ImageNode? image = n1 as ImageNode ?? n2 as ImageNode;
        ShapeNode? shape = n1 as ShapeNode ?? n2 as ShapeNode;

        if (image is null || shape is null || image.IsLocked || shape.IsLocked || shape.Kind is ShapeKind.Line)
        {
            return doc;
        }

        ShapeNode maskedShape = shape with
        {
            Fill = new ImageShapeFill(image.CurrentAssetId, image.OriginalAssetId, image.Crop),
        };
        resultingShapeId = maskedShape.Id;

        ImmutableArray<SceneNode> nextNodes = page.Nodes
            .Where(n => n.Id != image.Id)
            .Select(n => n.Id == shape.Id ? (SceneNode)maskedShape : n)
            .ToImmutableArray();

        return ReplaceActivePageNodes(doc, nextNodes);
    }

    public static ZiwaDocument SetSvgTint(ZiwaDocument doc, NodeId svgNodeId, ColorRgba? tintColor)
    {
        SceneNode? node = doc.ActivePage.Nodes.FirstOrDefault(n => n.Id == svgNodeId);
        if (node is not SvgNode svg || svg.IsLocked)
        {
            return doc;
        }

        return ReplaceNode(doc, svg.Id, svg with { TintColor = tintColor });
    }

    public static ZiwaDocument UpdateTextNodeContent(ZiwaDocument doc, NodeId textNodeId, string newText)
    {
        SceneNode? node = doc.ActivePage.Nodes.FirstOrDefault(n => n.Id == textNodeId);
        if (node is not TextNode text || text.IsLocked)
        {
            return doc;
        }

        return ReplaceNode(doc, text.Id, text with { Text = newText });
    }

    public static ZiwaDocument ApplyImageFilters(ZiwaDocument doc, NodeId imageNodeId, ImageFilterSpec filters)
    {
        SceneNode? node = doc.ActivePage.Nodes.FirstOrDefault(n => n.Id == imageNodeId);
        if (node is not ImageNode img || img.IsLocked)
        {
            return doc;
        }

        return ReplaceNode(doc, img.Id, img with { Filters = filters });
    }

    public static ZiwaDocument ResetImageFilters(ZiwaDocument doc, NodeId imageNodeId) =>
        ApplyImageFilters(doc, imageNodeId, ImageFilterSpec.None);

    public static ZiwaDocument ApplyProcessedImageAsset(
        ZiwaDocument doc,
        NodeId imageNodeId,
        AssetId processedAssetId,
        PageId? targetPageId = null)
    {
        if (processedAssetId.IsEmpty)
        {
            return doc;
        }

        int pageIndex = ResolvePageIndex(doc, targetPageId);
        if (pageIndex < 0)
        {
            return doc;
        }

        PageModel page = doc.Pages[pageIndex];
        SceneNode? node = page.Nodes.FirstOrDefault(n => n.Id == imageNodeId);
        if (node is not ImageNode img || img.IsLocked)
        {
            return doc;
        }

        // Preserve OriginalAssetId so non-destructive restoration always works.
        ImageNode updated = img with { CurrentAssetId = processedAssetId };
        return ReplaceNodeOnPage(doc, pageIndex, img.Id, updated);
    }

    public static ZiwaDocument ApplyAutoFaceFraming(
        ZiwaDocument doc,
        NodeId imageNodeId,
        CropSpec framingCrop,
        PageId? targetPageId = null)
    {
        int pageIndex = ResolvePageIndex(doc, targetPageId);
        if (pageIndex < 0)
        {
            return doc;
        }

        PageModel page = doc.Pages[pageIndex];
        SceneNode? node = page.Nodes.FirstOrDefault(n => n.Id == imageNodeId);
        if (node is not ImageNode img || img.IsLocked)
        {
            return doc;
        }

        var clamped = new CropSpec(
            DomainMath.Clamp(framingCrop.Zoom, 1.0, 20.0),
            DomainMath.Clamp(framingCrop.PanX, -1.0, 1.0),
            DomainMath.Clamp(framingCrop.PanY, -1.0, 1.0));
        return ReplaceNodeOnPage(doc, pageIndex, img.Id, img with { Crop = clamped });
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

    private static ZiwaDocument ReplaceNodeOnPage(ZiwaDocument doc, int pageIndex, NodeId nodeId, SceneNode replacement)
    {
        PageModel page = doc.Pages[pageIndex];
        ImmutableArray<SceneNode> nextNodes = page.Nodes
            .Select(n => n.Id == nodeId ? replacement : n)
            .ToImmutableArray();
        PageModel nextPage = page with { Nodes = nextNodes };
        ImmutableArray<PageModel> nextPages = doc.Pages.SetItem(pageIndex, nextPage);
        return doc with { Pages = nextPages, UpdatedAtUtc = DateTimeOffset.UtcNow };
    }

    private static RectMm ComputeSingleResizedBounds(
        RectMm oldBounds,
        double rotationDeg,
        TransformHandleKind handle,
        double pointerXMm,
        double pointerYMm,
        bool lockAspectRatio,
        double minSizeMm)
    {
        double oldCx = oldBounds.CenterX;
        double oldCy = oldBounds.CenterY;
        (double localPx, double localPy) = HitTestEngine.RotatePoint(pointerXMm, pointerYMm, oldCx, oldCy, -rotationDeg);

        RectMm localNew = ComputeAxisAlignedResizedBounds(
            oldBounds,
            handle,
            localPx,
            localPy,
            lockAspectRatio,
            minSizeMm);

        if (Math.Abs(rotationDeg) < 0.0001)
        {
            return localNew;
        }

        // Keep opposite anchor point stationary in world space when resizing a rotated node
        (double anchorLocalX, double anchorLocalY) = GetOppositeAnchor(oldBounds, handle);
        (double newAnchorLocalX, double newAnchorLocalY) = GetOppositeAnchor(localNew, handle);

        (double oldAnchorWorldX, double oldAnchorWorldY) = HitTestEngine.RotatePoint(anchorLocalX, anchorLocalY, oldCx, oldCy, rotationDeg);
        (double rotatedNewAnchorX, double rotatedNewAnchorY) = HitTestEngine.RotatePoint(newAnchorLocalX, newAnchorLocalY, localNew.CenterX, localNew.CenterY, rotationDeg);

        double shiftX = oldAnchorWorldX - rotatedNewAnchorX;
        double shiftY = oldAnchorWorldY - rotatedNewAnchorY;
        return localNew with { X = localNew.X + shiftX, Y = localNew.Y + shiftY };
    }

    private static (double X, double Y) GetOppositeAnchor(RectMm r, TransformHandleKind handle)
    {
        return handle switch
        {
            TransformHandleKind.TopLeft => (r.Right, r.Bottom),
            TransformHandleKind.Top => (r.CenterX, r.Bottom),
            TransformHandleKind.TopRight => (r.X, r.Bottom),
            TransformHandleKind.Right => (r.X, r.CenterY),
            TransformHandleKind.BottomRight => (r.X, r.Y),
            TransformHandleKind.Bottom => (r.CenterX, r.Y),
            TransformHandleKind.BottomLeft => (r.Right, r.Y),
            TransformHandleKind.Left => (r.Right, r.CenterY),
            _ => (r.CenterX, r.CenterY),
        };
    }

    private static RectMm ComputeAxisAlignedResizedBounds(
        RectMm oldBounds,
        TransformHandleKind handle,
        double px,
        double py,
        bool lockAspectRatio,
        double minSizeMm)
    {
        double left = oldBounds.X;
        double top = oldBounds.Y;
        double right = oldBounds.Right;
        double bottom = oldBounds.Bottom;

        bool movesLeft = handle is TransformHandleKind.TopLeft or TransformHandleKind.Left or TransformHandleKind.BottomLeft;
        bool movesRight = handle is TransformHandleKind.TopRight or TransformHandleKind.Right or TransformHandleKind.BottomRight;
        bool movesTop = handle is TransformHandleKind.TopLeft or TransformHandleKind.Top or TransformHandleKind.TopRight;
        bool movesBottom = handle is TransformHandleKind.BottomLeft or TransformHandleKind.Bottom or TransformHandleKind.BottomRight;

        if (movesLeft)
        {
            left = Math.Min(px, right - minSizeMm);
        }

        if (movesRight)
        {
            right = Math.Max(px, left + minSizeMm);
        }

        if (movesTop)
        {
            top = Math.Min(py, bottom - minSizeMm);
        }

        if (movesBottom)
        {
            bottom = Math.Max(py, top + minSizeMm);
        }

        double w = right - left;
        double h = bottom - top;

        if (lockAspectRatio && (movesLeft || movesRight) && (movesTop || movesBottom))
        {
            double aspect = oldBounds.Width / oldBounds.Height;
            double candidateWFromH = h * aspect;
            double candidateHFromW = w / aspect;
            if (Math.Abs(w - oldBounds.Width) >= Math.Abs(h - oldBounds.Height))
            {
                h = Math.Max(minSizeMm, candidateHFromW);
                if (movesTop)
                {
                    top = bottom - h;
                }
            }
            else
            {
                w = Math.Max(minSizeMm, candidateWFromH);
                if (movesLeft)
                {
                    left = right - w;
                }
            }
        }

        return new RectMm(left, top, w, h);
    }

    private static SceneNode TranslateNodeRecursive(SceneNode node, double dxMm, double dyMm)
    {
        RectMm moved = node.BoundsMm with { X = node.BoundsMm.X + dxMm, Y = node.BoundsMm.Y + dyMm };
        if (node is GroupNode group)
        {
            ImmutableArray<SceneNode> movedChildren = group.Children
                .Select(c => TranslateNodeRecursive(c, dxMm, dyMm))
                .ToImmutableArray();
            return group with { BoundsMm = moved, Children = movedChildren };
        }

        return node with { BoundsMm = moved };
    }

    private static SceneNode ResizeSingleNodeRecursive(SceneNode node, RectMm newBounds)
    {
        if (node is GroupNode group && group.BoundsMm.Width > 0 && group.BoundsMm.Height > 0)
        {
            double sx = newBounds.Width / group.BoundsMm.Width;
            double sy = newBounds.Height / group.BoundsMm.Height;
            ImmutableArray<SceneNode> scaledChildren = group.Children.Select(c =>
            {
                var childBounds = new RectMm(
                    newBounds.X + ((c.BoundsMm.X - group.BoundsMm.X) * sx),
                    newBounds.Y + ((c.BoundsMm.Y - group.BoundsMm.Y) * sy),
                    Math.Max(1.0, c.BoundsMm.Width * sx),
                    Math.Max(1.0, c.BoundsMm.Height * sy));
                return ResizeSingleNodeRecursive(c, childBounds);
            }).ToImmutableArray();
            return group with { BoundsMm = newBounds, Children = scaledChildren };
        }

        return node with { BoundsMm = newBounds };
    }

    private static SceneNode ReleaseChildFromGroup(GroupNode group, SceneNode child)
    {
        if (Math.Abs(group.RotationDeg) < 0.0001)
        {
            return child;
        }

        (double cx, double cy) = HitTestEngine.RotatePoint(
            child.BoundsMm.CenterX,
            child.BoundsMm.CenterY,
            group.BoundsMm.CenterX,
            group.BoundsMm.CenterY,
            group.RotationDeg);

        return child with
        {
            BoundsMm = child.BoundsMm with
            {
                X = cx - (child.BoundsMm.Width * 0.5),
                Y = cy - (child.BoundsMm.Height * 0.5),
            },
            RotationDeg = child.RotationDeg + group.RotationDeg,
        };
    }

    private static ZiwaDocument ReorderNodes(ZiwaDocument doc, ImmutableArray<NodeId> selectedIds, ZOrderAction action)
    {
        if (selectedIds.IsDefaultOrEmpty)
        {
            return doc;
        }

        PageModel page = doc.ActivePage;
        List<SceneNode> list = page.Nodes.ToList();
        HashSet<NodeId> idSet = selectedIds.ToHashSet();

        switch (action)
        {
            case ZOrderAction.Forward:
                for (int i = list.Count - 2; i >= 0; i--)
                {
                    if (idSet.Contains(list[i].Id) && !idSet.Contains(list[i + 1].Id))
                    {
                        (list[i], list[i + 1]) = (list[i + 1], list[i]);
                    }
                }

                break;

            case ZOrderAction.Backward:
                for (int i = 1; i < list.Count; i++)
                {
                    if (idSet.Contains(list[i].Id) && !idSet.Contains(list[i - 1].Id))
                    {
                        (list[i], list[i - 1]) = (list[i - 1], list[i]);
                    }
                }

                break;

            case ZOrderAction.Front:
                {
                    List<SceneNode> unselected = list.Where(n => !idSet.Contains(n.Id)).ToList();
                    List<SceneNode> selected = list.Where(n => idSet.Contains(n.Id)).ToList();
                    list = [.. unselected, .. selected];
                    break;
                }

            case ZOrderAction.Back:
                {
                    List<SceneNode> selected = list.Where(n => idSet.Contains(n.Id)).ToList();
                    List<SceneNode> unselected = list.Where(n => !idSet.Contains(n.Id)).ToList();
                    list = [.. selected, .. unselected];
                    break;
                }
        }

        return ReplaceActivePageNodes(doc, [.. list]);
    }

    private static ZiwaDocument ReplaceNode(ZiwaDocument doc, NodeId nodeId, SceneNode replacement)
    {
        ImmutableArray<SceneNode> next = doc.ActivePage.Nodes
            .Select(n => n.Id == nodeId ? replacement : n)
            .ToImmutableArray();
        return ReplaceActivePageNodes(doc, next);
    }

    private static ZiwaDocument ReplaceActivePageNodes(ZiwaDocument doc, ImmutableArray<SceneNode> nextNodes)
    {
        PageModel nextPage = doc.ActivePage with { Nodes = nextNodes };
        ImmutableArray<PageModel> nextPages = doc.Pages.SetItem(doc.ActivePageIndex, nextPage);
        return doc with { Pages = nextPages, UpdatedAtUtc = DateTimeOffset.UtcNow };
    }

    private enum ZOrderAction
    {
        Forward,
        Backward,
        Front,
        Back,
    }
}
