using System.Collections.Immutable;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Interaction;

public enum TransformHandleKind
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Rotate,
}

public readonly record struct HandlePointMm(TransformHandleKind Kind, double XMm, double YMm);

public readonly record struct SelectionBoundsInfo(RectMm BoundsMm, double RotationDeg, bool HasSelection);

public static class HitTestEngine
{
    public static SceneNode? HitTestTopmostNode(PageModel page, double xMm, double yMm)
    {
        if (page.Nodes.IsDefaultOrEmpty)
        {
            return null;
        }

        for (int i = page.Nodes.Length - 1; i >= 0; i--)
        {
            SceneNode node = page.Nodes[i];
            if (!node.IsVisible || node.IsLocked)
            {
                continue;
            }

            if (HitTestNodeBounds(node, xMm, yMm))
            {
                return node;
            }
        }

        return null;
    }

    public static SelectionBoundsInfo ComputeSelectionBounds(PageModel page, ImmutableArray<NodeId> selectedIds)
    {
        if (selectedIds.IsDefaultOrEmpty || page.Nodes.IsDefaultOrEmpty)
        {
            return new SelectionBoundsInfo(default, 0.0, false);
        }

        HashSet<NodeId> idSet = selectedIds.ToHashSet();
        List<SceneNode> selected = page.Nodes.Where(n => idSet.Contains(n.Id) && n.IsVisible).ToList();
        if (selected.Count == 0)
        {
            return new SelectionBoundsInfo(default, 0.0, false);
        }

        if (selected.Count == 1)
        {
            SceneNode single = selected[0];
            return new SelectionBoundsInfo(single.BoundsMm, single.RotationDeg, true);
        }

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (SceneNode node in selected)
        {
            RectMm aabb = ComputeRotatedAabb(node.BoundsMm, node.RotationDeg);
            minX = Math.Min(minX, aabb.X);
            minY = Math.Min(minY, aabb.Y);
            maxX = Math.Max(maxX, aabb.Right);
            maxY = Math.Max(maxY, aabb.Bottom);
        }

        return new SelectionBoundsInfo(new RectMm(minX, minY, maxX - minX, maxY - minY), 0.0, true);
    }

    public static HandlePointMm[] GetHandleLocationsMm(
        RectMm bounds,
        double rotationDeg,
        double rotateHandleOffsetMm)
    {
        double cx = bounds.CenterX;
        double cy = bounds.CenterY;

        (TransformHandleKind Kind, double X, double Y)[] local =
        [
            (TransformHandleKind.Rotate, cx, bounds.Y - rotateHandleOffsetMm),
            (TransformHandleKind.TopLeft, bounds.X, bounds.Y),
            (TransformHandleKind.Top, cx, bounds.Y),
            (TransformHandleKind.TopRight, bounds.Right, bounds.Y),
            (TransformHandleKind.Right, bounds.Right, cy),
            (TransformHandleKind.BottomRight, bounds.Right, bounds.Bottom),
            (TransformHandleKind.Bottom, cx, bounds.Bottom),
            (TransformHandleKind.BottomLeft, bounds.X, bounds.Bottom),
            (TransformHandleKind.Left, bounds.X, cy),
        ];

        var result = new HandlePointMm[local.Length];
        for (int i = 0; i < local.Length; i++)
        {
            (double wx, double wy) = RotatePoint(local[i].X, local[i].Y, cx, cy, rotationDeg);
            result[i] = new HandlePointMm(local[i].Kind, wx, wy);
        }

        return result;
    }

    public static TransformHandleKind HitTestHandles(
        RectMm selectionBounds,
        double rotationDeg,
        double xMm,
        double yMm,
        double handleToleranceMm,
        double rotateHandleOffsetMm)
    {
        HandlePointMm[] handles = GetHandleLocationsMm(selectionBounds, rotationDeg, rotateHandleOffsetMm);
        double tolSq = handleToleranceMm * handleToleranceMm;

        foreach (HandlePointMm h in handles)
        {
            double dx = xMm - h.XMm;
            double dy = yMm - h.YMm;
            if ((dx * dx) + (dy * dy) <= tolSq)
            {
                return h.Kind;
            }
        }

        return TransformHandleKind.None;
    }

    public static ImmutableArray<NodeId> HitTestMarquee(PageModel page, RectMm marqueeMm)
    {
        if (page.Nodes.IsDefaultOrEmpty)
        {
            return [];
        }

        RectMm norm = NormalizeRect(marqueeMm);
        var hits = ImmutableArray.CreateBuilder<NodeId>();

        foreach (SceneNode node in page.Nodes)
        {
            if (!node.IsVisible || node.IsLocked)
            {
                continue;
            }

            RectMm aabb = ComputeRotatedAabb(node.BoundsMm, node.RotationDeg);
            if (Intersects(norm, aabb))
            {
                hits.Add(node.Id);
            }
        }

        return hits.ToImmutable();
    }

    public static RectMm ComputeRotatedAabb(RectMm bounds, double rotationDeg)
    {
        if (Math.Abs(rotationDeg) < 0.0001)
        {
            return bounds;
        }

        double cx = bounds.CenterX;
        double cy = bounds.CenterY;
        (double x1, double y1) = RotatePoint(bounds.X, bounds.Y, cx, cy, rotationDeg);
        (double x2, double y2) = RotatePoint(bounds.Right, bounds.Y, cx, cy, rotationDeg);
        (double x3, double y3) = RotatePoint(bounds.Right, bounds.Bottom, cx, cy, rotationDeg);
        (double x4, double y4) = RotatePoint(bounds.X, bounds.Bottom, cx, cy, rotationDeg);

        double minX = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
        double minY = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
        double maxX = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
        double maxY = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
        return new RectMm(minX, minY, maxX - minX, maxY - minY);
    }

    public static (double X, double Y) RotatePoint(double px, double py, double cx, double cy, double angleDeg)
    {
        if (Math.Abs(angleDeg) < 0.0001)
        {
            return (px, py);
        }

        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double dx = px - cx;
        double dy = py - cy;
        return (cx + (dx * cos) - (dy * sin), cy + (dx * sin) + (dy * cos));
    }

    private static bool HitTestNodeBounds(SceneNode node, double xMm, double yMm)
    {
        (double lx, double ly) = RotatePoint(xMm, yMm, node.BoundsMm.CenterX, node.BoundsMm.CenterY, -node.RotationDeg);
        double pad = node is ShapeNode { Kind: ShapeKind.Line } ? 2.0 : 0.0;
        return lx >= node.BoundsMm.X - pad &&
               lx <= node.BoundsMm.Right + pad &&
               ly >= node.BoundsMm.Y - pad &&
               ly <= node.BoundsMm.Bottom + pad;
    }

    private static RectMm NormalizeRect(RectMm r)
    {
        double x = r.Width < 0 ? r.X + r.Width : r.X;
        double y = r.Height < 0 ? r.Y + r.Height : r.Y;
        return new RectMm(x, y, Math.Abs(r.Width), Math.Abs(r.Height));
    }

    private static bool Intersects(RectMm a, RectMm b)
    {
        return a.X <= b.Right && a.Right >= b.X && a.Y <= b.Bottom && a.Bottom >= b.Y;
    }
}
