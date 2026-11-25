using System.Collections.Immutable;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Domain.Collage;

public static class CollagePresets
{
    public static readonly CollageGridSpec IdPassport35x45 = new(
        PresetId: "id-35x45",
        SizingMode: CollageSizingMode.FixedPhysicalCellMm,
        Rows: 3,
        Columns: 2,
        CellWidthMm: 35.0,
        CellHeightMm: 45.0,
        GapXMm: 3.0,
        GapYMm: 3.0,
        MarginMm: 4.0);

    public static readonly CollageGridSpec IdOfficial40x60 = new(
        PresetId: "id-40x60",
        SizingMode: CollageSizingMode.FixedPhysicalCellMm,
        Rows: 2,
        Columns: 2,
        CellWidthMm: 40.0,
        CellHeightMm: 60.0,
        GapXMm: 4.0,
        GapYMm: 4.0,
        MarginMm: 4.0);

    public static readonly CollageGridSpec VisaSquare50x50 = new(
        PresetId: "visa-50x50",
        SizingMode: CollageSizingMode.FixedPhysicalCellMm,
        Rows: 2,
        Columns: 1,
        CellWidthMm: 50.0,
        CellHeightMm: 50.0,
        GapXMm: 4.0,
        GapYMm: 6.0,
        MarginMm: 5.0);

    public static readonly CollageGridSpec MixedIdentitySheet = new(
        PresetId: "mixed-identity",
        SizingMode: CollageSizingMode.FixedPhysicalCellMm,
        Rows: 3,
        Columns: 4,
        CellWidthMm: 30.0,
        CellHeightMm: 40.0,
        GapXMm: 4.0,
        GapYMm: 4.0,
        MarginMm: 5.0,
        MixedBands:
        [
            new MixedRowBandSpec(Columns: 3, CellWidthMm: 40.0, CellHeightMm: 60.0, Rows: 1),
            new MixedRowBandSpec(Columns: 4, CellWidthMm: 30.0, CellHeightMm: 40.0, Rows: 2),
        ]);

    public static readonly CollageGridSpec Custom2x2Fit = new(
        PresetId: "custom-2x2-fit",
        SizingMode: CollageSizingMode.FitGridToPaper,
        Rows: 2,
        Columns: 2,
        CellWidthMm: 40.0,
        CellHeightMm: 60.0,
        GapXMm: 4.0,
        GapYMm: 4.0,
        MarginMm: 6.0);

    public static ImmutableArray<CollageGridSpec> All { get; } =
    [
        IdPassport35x45,
        IdOfficial40x60,
        VisaSquare50x50,
        MixedIdentitySheet,
        Custom2x2Fit,
    ];
}

public static class PhysicalGridBuilder
{
    public const double MinCellSizeMm = 1.0;

    public static ImmutableArray<RectMm> BuildSlotRects(double pageWidthMm, double pageHeightMm, CollageGridSpec spec)
    {
        SchemaValidator.AssertFinitePositive(pageWidthMm, nameof(pageWidthMm));
        SchemaValidator.AssertFinitePositive(pageHeightMm, nameof(pageHeightMm));

        if (pageWidthMm < MinCellSizeMm)
        {
            throw new ArgumentOutOfRangeException(nameof(pageWidthMm), pageWidthMm, "Page width must be >= 1.0 mm.");
        }

        if (pageHeightMm < MinCellSizeMm)
        {
            throw new ArgumentOutOfRangeException(nameof(pageHeightMm), pageHeightMm, "Page height must be >= 1.0 mm.");
        }

        if (!spec.MixedBands.IsDefaultOrEmpty)
        {
            return BuildMixedBandSlotRects(pageWidthMm, pageHeightMm, spec);
        }

        int maxCols = System.Math.Max(1, (int)System.Math.Floor(pageWidthMm / MinCellSizeMm));
        int maxRows = System.Math.Max(1, (int)System.Math.Floor(pageHeightMm / MinCellSizeMm));
        int cols = System.Math.Clamp(spec.Columns, 1, maxCols);
        int rows = System.Math.Clamp(spec.Rows, 1, maxRows);

        double maxMargin = System.Math.Max(
            0.0,
            System.Math.Min((pageWidthMm - (cols * MinCellSizeMm)) * 0.5, (pageHeightMm - (rows * MinCellSizeMm)) * 0.5));
        double margin = System.Math.Clamp(SanitizeNonNegative(spec.MarginMm), 0.0, maxMargin);

        double availW = System.Math.Max(cols * MinCellSizeMm, pageWidthMm - (2.0 * margin));
        double availH = System.Math.Max(rows * MinCellSizeMm, pageHeightMm - (2.0 * margin));

        double maxGapX = cols > 1 ? System.Math.Max(0.0, (availW - (cols * MinCellSizeMm)) / (cols - 1)) : 0.0;
        double maxGapY = rows > 1 ? System.Math.Max(0.0, (availH - (rows * MinCellSizeMm)) / (rows - 1)) : 0.0;
        double gapX = cols > 1 ? System.Math.Clamp(SanitizeNonNegative(spec.GapXMm), 0.0, maxGapX) : 0.0;
        double gapY = rows > 1 ? System.Math.Clamp(SanitizeNonNegative(spec.GapYMm), 0.0, maxGapY) : 0.0;

        double maxCellsW = System.Math.Max(cols * MinCellSizeMm, availW - ((cols - 1) * gapX));
        double maxCellsH = System.Math.Max(rows * MinCellSizeMm, availH - ((rows - 1) * gapY));

        double cellW;
        double cellH;
        if (spec.SizingMode == CollageSizingMode.FitGridToPaper)
        {
            cellW = System.Math.Max(MinCellSizeMm, maxCellsW / cols);
            cellH = System.Math.Max(MinCellSizeMm, maxCellsH / rows);
        }
        else
        {
            double rawCellW = SanitizePositiveOrDefault(spec.CellWidthMm, 35.0);
            double rawCellH = SanitizePositiveOrDefault(spec.CellHeightMm, 45.0);
            double scale = System.Math.Min(
                1.0,
                System.Math.Min(maxCellsW / (cols * rawCellW), maxCellsH / (rows * rawCellH)));

            cellW = System.Math.Max(MinCellSizeMm, rawCellW * scale);
            cellH = System.Math.Max(MinCellSizeMm, rawCellH * scale);
        }

        (cellW, gapX) = FitAxisToAvailable(availW, cols, cellW, gapX);
        (cellH, gapY) = FitAxisToAvailable(availH, rows, cellH, gapY);

        double totalW = (cols * cellW) + ((cols - 1) * gapX);
        double totalH = (rows * cellH) + ((rows - 1) * gapY);

        (double startX, double startY) = ComputeAlignedOrigin(
            pageWidthMm,
            pageHeightMm,
            margin,
            totalW,
            totalH,
            spec.Alignment);

        var rects = ImmutableArray.CreateBuilder<RectMm>(rows * cols);
        for (int r = 0; r < rows; r++)
        {
            double y = startY + (r * (cellH + gapY));
            double safeH = System.Math.Clamp(cellH, MinCellSizeMm, System.Math.Max(MinCellSizeMm, pageHeightMm - y));
            for (int c = 0; c < cols; c++)
            {
                double x = startX + (c * (cellW + gapX));
                double safeW = System.Math.Clamp(cellW, MinCellSizeMm, System.Math.Max(MinCellSizeMm, pageWidthMm - x));
                rects.Add(new RectMm(x, y, safeW, safeH));
            }
        }

        return rects.ToImmutable();
    }

    public static CollageLayoutModel BuildOrReflowLayout(
        double pageWidthMm,
        double pageHeightMm,
        CollageGridSpec spec,
        CollageLayoutModel? previous = null)
    {
        ImmutableArray<RectMm> rects = BuildSlotRects(pageWidthMm, pageHeightMm, spec);
        var slots = ImmutableArray.CreateBuilder<CollageSlot>(rects.Length);
        var spilledAssets = ImmutableArray.CreateBuilder<AssetId>();

        if (previous is not null && !previous.PendingAssets.IsDefaultOrEmpty)
        {
            spilledAssets.AddRange(previous.PendingAssets);
        }

        for (int i = 0; i < rects.Length; i++)
        {
            if (previous is not null && i < previous.Slots.Length)
            {
                CollageSlot prev = previous.Slots[i];
                slots.Add(prev with { Index = i, BoundsMm = rects[i] });
            }
            else
            {
                slots.Add(new CollageSlot(SlotId.New(), i, rects[i]));
            }
        }

        if (previous is not null && previous.Slots.Length > rects.Length)
        {
            for (int i = rects.Length; i < previous.Slots.Length; i++)
            {
                if (previous.Slots[i].AssignedAssetId is { } asset && !asset.IsEmpty)
                {
                    spilledAssets.Add(asset);
                }
            }
        }

        return new CollageLayoutModel(
            spec,
            slots.ToImmutable(),
            spilledAssets.Distinct().ToImmutableArray(),
            previous?.AutoRepeatMode ?? false);
    }

    private static ImmutableArray<RectMm> BuildMixedBandSlotRects(double pageWidthMm, double pageHeightMm, CollageGridSpec spec)
    {
        int maxTotalRows = System.Math.Max(1, (int)System.Math.Floor(pageHeightMm / MinCellSizeMm));
        int maxCols = System.Math.Max(1, (int)System.Math.Floor(pageWidthMm / MinCellSizeMm));

        var sanitizedBands = new List<(int Cols, int Rows, double CellW, double CellH)>();
        int remainingRows = maxTotalRows;

        foreach (MixedRowBandSpec band in spec.MixedBands)
        {
            if (remainingRows <= 0)
            {
                break;
            }

            int r = System.Math.Clamp(band.Rows, 1, remainingRows);
            int c = System.Math.Clamp(band.Columns, 1, maxCols);
            double cw = SanitizePositiveOrDefault(band.CellWidthMm, 30.0);
            double ch = SanitizePositiveOrDefault(band.CellHeightMm, 40.0);
            sanitizedBands.Add((c, r, cw, ch));
            remainingRows -= r;
        }

        if (sanitizedBands.Count == 0)
        {
            return BuildSlotRects(pageWidthMm, pageHeightMm, spec with { MixedBands = default });
        }

        int totalRows = sanitizedBands.Sum(b => b.Rows);
        int maxBandCols = sanitizedBands.Max(b => b.Cols);
        double maxMargin = System.Math.Max(
            0.0,
            System.Math.Min((pageWidthMm - (maxBandCols * MinCellSizeMm)) * 0.5, (pageHeightMm - (totalRows * MinCellSizeMm)) * 0.5));
        double margin = System.Math.Clamp(SanitizeNonNegative(spec.MarginMm), 0.0, maxMargin);

        double availW = System.Math.Max(maxBandCols * MinCellSizeMm, pageWidthMm - (2.0 * margin));
        double availH = System.Math.Max(totalRows * MinCellSizeMm, pageHeightMm - (2.0 * margin));

        double maxGapX = maxBandCols > 1 ? System.Math.Max(0.0, (availW - (maxBandCols * MinCellSizeMm)) / (maxBandCols - 1)) : 0.0;
        double maxGapY = totalRows > 1 ? System.Math.Max(0.0, (availH - (totalRows * MinCellSizeMm)) / (totalRows - 1)) : 0.0;
        double gapX = System.Math.Clamp(SanitizeNonNegative(spec.GapXMm), 0.0, maxGapX);
        double gapY = totalRows > 1 ? System.Math.Clamp(SanitizeNonNegative(spec.GapYMm), 0.0, maxGapY) : 0.0;

        double rawBandsHeight = sanitizedBands.Sum(b => b.Rows * b.CellH);
        double maxCellsH = System.Math.Max(totalRows * MinCellSizeMm, availH - ((totalRows - 1) * gapY));
        double yScale = System.Math.Min(1.0, maxCellsH / rawBandsHeight);

        double maxBandRawWidth = sanitizedBands.Max(b => (b.Cols * b.CellW) + (System.Math.Max(0, b.Cols - 1) * gapX));
        double xScale = maxBandRawWidth > availW
            ? System.Math.Min(1.0, availW / maxBandRawWidth)
            : 1.0;
        double uniformScale = System.Math.Min(xScale, yScale);

        var rowHeights = new List<double>(totalRows);
        var bandMeta = new List<(int Cols, int Rows, double CellW, double CellH)>(sanitizedBands.Count);
        foreach ((int cols, int rows, double rawW, double rawH) in sanitizedBands)
        {
            double maxRowCellW = (availW - (System.Math.Max(0, cols - 1) * gapX)) / cols;
            double cellW = System.Math.Clamp(rawW * uniformScale, MinCellSizeMm, System.Math.Max(MinCellSizeMm, maxRowCellW));
            double cellH = System.Math.Max(MinCellSizeMm, rawH * uniformScale);
            bandMeta.Add((cols, rows, cellW, cellH));
            for (int r = 0; r < rows; r++)
            {
                rowHeights.Add(cellH);
            }
        }

        double totalCellsH = rowHeights.Sum();
        if (totalCellsH + ((totalRows - 1) * gapY) > availH)
        {
            gapY = totalRows > 1 ? System.Math.Max(0.0, (availH - totalCellsH) / (totalRows - 1)) : 0.0;
            if (totalCellsH + ((totalRows - 1) * gapY) > availH)
            {
                double shrink = availH / System.Math.Max(MinCellSizeMm, totalCellsH);
                for (int i = 0; i < bandMeta.Count; i++)
                {
                    bandMeta[i] = bandMeta[i] with { CellH = System.Math.Max(MinCellSizeMm, bandMeta[i].CellH * shrink) };
                }

                totalCellsH = bandMeta.Sum(b => b.Rows * b.CellH);
            }
        }

        double totalH = totalCellsH + (System.Math.Max(0, totalRows - 1) * gapY);
        double maxTotalW = bandMeta.Max(b => (b.Cols * b.CellW) + (System.Math.Max(0, b.Cols - 1) * gapX));
        (_, double startY) = ComputeAlignedOrigin(pageWidthMm, pageHeightMm, margin, maxTotalW, totalH, spec.Alignment);

        var rects = ImmutableArray.CreateBuilder<RectMm>();
        double curY = startY;
        foreach ((int cols, int rows, double cellW, double cellH) in bandMeta)
        {
            (double bandW, double bandGapX) = FitAxisToAvailable(availW, cols, cellW, gapX);
            double bandTotalW = (cols * bandW) + (System.Math.Max(0, cols - 1) * bandGapX);
            (double startX, _) = ComputeAlignedOrigin(pageWidthMm, pageHeightMm, margin, bandTotalW, totalH, spec.Alignment);

            for (int r = 0; r < rows; r++)
            {
                double safeH = System.Math.Clamp(cellH, MinCellSizeMm, System.Math.Max(MinCellSizeMm, pageHeightMm - curY));
                for (int c = 0; c < cols; c++)
                {
                    double x = startX + (c * (bandW + bandGapX));
                    double safeW = System.Math.Clamp(bandW, MinCellSizeMm, System.Math.Max(MinCellSizeMm, pageWidthMm - x));
                    rects.Add(new RectMm(x, curY, safeW, safeH));
                }

                curY += safeH + gapY;
            }
        }

        return rects.ToImmutable();
    }

    private static (double CellSize, double Gap) FitAxisToAvailable(double availSpan, int count, double cellSize, double gap)
    {
        if (count <= 1)
        {
            return (System.Math.Clamp(cellSize, MinCellSizeMm, availSpan), 0.0);
        }

        double total = (count * cellSize) + ((count - 1) * gap);
        if (total <= availSpan)
        {
            return (cellSize, gap);
        }

        double shrunkGap = System.Math.Max(0.0, (availSpan - (count * cellSize)) / (count - 1));
        if ((count * cellSize) + ((count - 1) * shrunkGap) <= availSpan + 1e-9)
        {
            return (cellSize, shrunkGap);
        }

        double shrunkCell = System.Math.Max(MinCellSizeMm, availSpan / count);
        return (shrunkCell, 0.0);
    }

    private static (double StartX, double StartY) ComputeAlignedOrigin(
        double pageW,
        double pageH,
        double margin,
        double totalW,
        double totalH,
        GridAlignment9 alignment)
    {
        double leftX = margin;
        double centerX = (pageW - totalW) * 0.5;
        double rightX = pageW - margin - totalW;

        double topY = margin;
        double middleY = (pageH - totalH) * 0.5;
        double bottomY = pageH - margin - totalH;

        double x = alignment switch
        {
            GridAlignment9.TopLeft or GridAlignment9.MiddleLeft or GridAlignment9.BottomLeft => leftX,
            GridAlignment9.TopRight or GridAlignment9.MiddleRight or GridAlignment9.BottomRight => rightX,
            _ => centerX,
        };

        double y = alignment switch
        {
            GridAlignment9.TopLeft or GridAlignment9.TopCenter or GridAlignment9.TopRight => topY,
            GridAlignment9.BottomLeft or GridAlignment9.BottomCenter or GridAlignment9.BottomRight => bottomY,
            _ => middleY,
        };

        double maxStartX = System.Math.Max(0.0, pageW - totalW);
        double maxStartY = System.Math.Max(0.0, pageH - totalH);
        return (System.Math.Clamp(x, 0.0, maxStartX), System.Math.Clamp(y, 0.0, maxStartY));
    }

    private static double SanitizeNonNegative(double v) => double.IsFinite(v) && v >= 0.0 ? v : 0.0;

    private static double SanitizePositiveOrDefault(double v, double fallback) =>
        double.IsFinite(v) && v > 0.0 ? v : fallback;
}
