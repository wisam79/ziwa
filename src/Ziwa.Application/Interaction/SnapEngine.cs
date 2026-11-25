using System.Collections.Immutable;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Interaction;

public sealed record SnapGuideLine(bool IsVertical, double PositionMm, double StartMm, double EndMm);

public sealed record SnapResult(RectMm SnappedBoundsMm, ImmutableArray<SnapGuideLine> Guides);

public static class SnapEngine
{
    public const double DefaultSnapToleranceMm = 2.0;

    public static SnapResult ComputeMoveSnap(
        RectMm candidateBounds,
        PageModel page,
        IReadOnlySet<NodeId> excludedIds,
        double toleranceMm = DefaultSnapToleranceMm,
        bool bypassSnap = false)
    {
        if (bypassSnap || toleranceMm <= 0.0)
        {
            return new SnapResult(candidateBounds, []);
        }

        List<RectMm> siblings = page.Nodes
            .Where(n => n.IsVisible && !excludedIds.Contains(n.Id))
            .Select(n => HitTestEngine.ComputeRotatedAabb(n.BoundsMm, n.RotationDeg))
            .ToList();

        List<(double Position, double Start, double End)> xTargets = BuildAxisTargets(
            page.WidthMm,
            page.HeightMm,
            siblings,
            candidateBounds.Width,
            isVertical: true);

        List<(double Position, double Start, double End)> yTargets = BuildAxisTargets(
            page.HeightMm,
            page.WidthMm,
            siblings,
            candidateBounds.Height,
            isVertical: false);

        (double dx, SnapGuideLine? xGuide) = FindBestAxisSnap(
            [candidateBounds.X, candidateBounds.CenterX, candidateBounds.Right],
            xTargets,
            toleranceMm,
            isVertical: true);

        (double dy, SnapGuideLine? yGuide) = FindBestAxisSnap(
            [candidateBounds.Y, candidateBounds.CenterY, candidateBounds.Bottom],
            yTargets,
            toleranceMm,
            isVertical: false);

        var snapped = candidateBounds with
        {
            X = candidateBounds.X + dx,
            Y = candidateBounds.Y + dy,
        };

        var guides = ImmutableArray.CreateBuilder<SnapGuideLine>();
        if (xGuide is not null)
        {
            double minY = Math.Min(xGuide.StartMm, snapped.Y);
            double maxY = Math.Max(xGuide.EndMm, snapped.Bottom);
            guides.Add(xGuide with { StartMm = minY, EndMm = maxY });
        }

        if (yGuide is not null)
        {
            double minX = Math.Min(yGuide.StartMm, snapped.X);
            double maxX = Math.Max(yGuide.EndMm, snapped.Right);
            guides.Add(yGuide with { StartMm = minX, EndMm = maxX });
        }

        return new SnapResult(snapped, guides.ToImmutable());
    }

    private static List<(double Position, double Start, double End)> BuildAxisTargets(
        double primaryPageLength,
        double crossPageLength,
        List<RectMm> siblings,
        double candidateSpan,
        bool isVertical)
    {
        var targets = new List<(double Position, double Start, double End)>
        {
            (0.0, 0.0, crossPageLength),
            (primaryPageLength * 0.5, 0.0, crossPageLength),
            (primaryPageLength, 0.0, crossPageLength),
        };

        foreach (RectMm s in siblings)
        {
            double p0 = isVertical ? s.X : s.Y;
            double pc = isVertical ? s.CenterX : s.CenterY;
            double p1 = isVertical ? s.Right : s.Bottom;
            double c0 = isVertical ? s.Y : s.X;
            double c1 = isVertical ? s.Bottom : s.Right;

            targets.Add((p0, c0, c1));
            targets.Add((pc, c0, c1));
            targets.Add((p1, c0, c1));
        }

        // Equal-spacing targets between ordered pairs of siblings
        if (siblings.Count >= 2)
        {
            List<RectMm> ordered = siblings
                .OrderBy(s => isVertical ? s.X : s.Y)
                .ToList();

            for (int i = 0; i < ordered.Count - 1; i++)
            {
                RectMm a = ordered[i];
                RectMm b = ordered[i + 1];
                double aStart = isVertical ? a.X : a.Y;
                double aEnd = isVertical ? a.Right : a.Bottom;
                double bStart = isVertical ? b.X : b.Y;
                double bEnd = isVertical ? b.Right : b.Bottom;
                double gap = bStart - aEnd;
                if (gap <= 0.5)
                {
                    continue;
                }

                double crossMin = isVertical ? Math.Min(a.Y, b.Y) : Math.Min(a.X, b.X);
                double crossMax = isVertical ? Math.Max(a.Bottom, b.Bottom) : Math.Max(a.Right, b.Right);

                // Candidate placed after B with equal gap: candidate.Start == bEnd + gap
                targets.Add((bEnd + gap, crossMin, crossMax));
                // Candidate placed before A with equal gap: candidate.End == aStart - gap
                targets.Add((aStart - gap, crossMin, crossMax));
                // Candidate centered inside gap (if wide enough)
                if (gap >= candidateSpan)
                {
                    targets.Add(((aEnd + bStart) * 0.5, crossMin, crossMax));
                }
            }
        }

        return targets;
    }

    private static (double Delta, SnapGuideLine? Guide) FindBestAxisSnap(
        ReadOnlySpan<double> candidatePoints,
        List<(double Position, double Start, double End)> targets,
        double toleranceMm,
        bool isVertical)
    {
        double bestAbs = toleranceMm + 0.0001;
        double bestDelta = 0.0;
        SnapGuideLine? bestGuide = null;

        foreach ((double targetPos, double start, double end) in targets)
        {
            foreach (double cand in candidatePoints)
            {
                double delta = targetPos - cand;
                double abs = Math.Abs(delta);
                if (abs <= toleranceMm && abs < bestAbs)
                {
                    bestAbs = abs;
                    bestDelta = delta;
                    bestGuide = new SnapGuideLine(isVertical, targetPos, start, end);
                }
            }
        }

        return (bestDelta, bestGuide);
    }
}
