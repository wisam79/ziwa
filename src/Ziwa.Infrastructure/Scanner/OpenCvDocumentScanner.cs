using System.Collections.Immutable;
using System.Runtime.InteropServices;
using OpenCvSharp;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Scanner;
using Ziwa.Rendering.Ai;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Filters;

namespace Ziwa.Infrastructure.Scanner;

public sealed class OpenCvDocumentScanner : IDocumentScannerService
{
    private readonly AssetStore _assetStore;
    private readonly DocumentScannerOptions _options;

    public OpenCvDocumentScanner(AssetStore assetStore, DocumentScannerOptions? options = null)
    {
        _assetStore = assetStore;
        _options = options ?? new DocumentScannerOptions();
    }

    public async Task<ScanDetectionResult> DetectDocumentsAsync(
        AssetId sourceAssetId,
        ScanDetectionMode mode = ScanDetectionMode.AutoSingleOrMulti,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_assetStore.TryGetEncodedBytes(sourceAssetId, out byte[]? encodedBytes) || encodedBytes is null)
        {
            throw new InvalidDataException($"Source asset '{sourceAssetId.Value}' was not found in AssetStore.");
        }

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.ProcessingStepDelayMs > 0)
            {
                await Task.Delay(_options.ProcessingStepDelayMs, cancellationToken).ConfigureAwait(false);
            }

            using SKBitmap fullBitmap = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(encodedBytes);
            int fullW = fullBitmap.Width;
            int fullH = fullBitmap.Height;

            int maxDetEdge = Math.Max(128, _options.MaxDetectionEdgePx);
            double scale = Math.Max(fullW, fullH) > maxDetEdge
                ? (double)maxDetEdge / Math.Max(fullW, fullH)
                : 1.0;
            int detW = Math.Max(32, (int)Math.Round(fullW * scale));
            int detH = Math.Max(32, (int)Math.Round(fullH * scale));

            using SKBitmap detBitmap = scale < 0.999
                ? fullBitmap.Resize(new SKImageInfo(detW, detH, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.Medium)
                  ?? throw new InvalidOperationException("Failed to resize scan image for detection.")
                : fullBitmap.Copy() ?? throw new InvalidOperationException("Failed to copy scan image.");

            byte[] detRgba = new byte[checked(detW * detH * 4)];
            AlphaMaskCompositor.ReadRgbaPixels(detBitmap, detRgba);

            using var rgbaMat = new Mat(detH, detW, MatType.CV_8UC4);
            Marshal.Copy(detRgba, 0, rgbaMat.Data, detRgba.Length);

            using var gray = new Mat();
            using var blurred = new Mat();
            Cv2.CvtColor(rgbaMat, gray, ColorConversionCodes.RGBA2GRAY);
            Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0.0);

            var candidates = new List<CandidateQuad>();
            double invScaleX = (double)fullW / detW;
            double invScaleY = (double)fullH / detH;
            double detArea = detW * (double)detH;

            // Map 1: Canny edges + dilation
            using (var canny = new Mat())
            using (var dilated = new Mat())
            using (var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
            {
                Cv2.Canny(blurred, canny, 40.0, 130.0);
                Cv2.Dilate(canny, dilated, dilateKernel, iterations: 1);
                CollectQuadCandidates(dilated, detArea, invScaleX, invScaleY, baseWeight: 1.05, candidates, cancellationToken);
            }

            // Map 2 & 3: Otsu bright-on-dark and dark-on-bright contours
            using (var otsu = new Mat())
            using (var invOtsu = new Mat())
            using (var closed = new Mat())
            using (var invClosed = new Mat())
            using (var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            {
                Cv2.Threshold(blurred, otsu, 0.0, 255.0, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Cv2.MorphologyEx(otsu, closed, MorphTypes.Close, closeKernel);
                CollectQuadCandidates(closed, detArea, invScaleX, invScaleY, baseWeight: 1.0, candidates, cancellationToken);

                Cv2.BitwiseNot(otsu, invOtsu);
                Cv2.MorphologyEx(invOtsu, invClosed, MorphTypes.Close, closeKernel);
                CollectQuadCandidates(invClosed, detArea, invScaleX, invScaleY, baseWeight: 0.92, candidates, cancellationToken);
            }

            ImmutableArray<DetectedDocumentRegion> resolved = SelectFinalRegions(candidates, mode, fullW, fullH);
            return new ScanDetectionResult(fullW, fullH, resolved);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<ExtractedScanDocument>> ExtractDocumentsAsync(
        AssetId sourceAssetId,
        IReadOnlyList<ScanExtractionRequestItem> items,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items is null || items.Count == 0)
        {
            return [];
        }

        if (!_assetStore.TryGetEncodedBytes(sourceAssetId, out byte[]? encodedBytes) || encodedBytes is null)
        {
            throw new InvalidDataException($"Source asset '{sourceAssetId.Value}' was not found in AssetStore.");
        }

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.ProcessingStepDelayMs > 0)
            {
                await Task.Delay(_options.ProcessingStepDelayMs, cancellationToken).ConfigureAwait(false);
            }

            using SKBitmap fullBitmap = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(encodedBytes);
            int fullW = fullBitmap.Width;
            int fullH = fullBitmap.Height;
            byte[] fullRgba = new byte[checked(fullW * fullH * 4)];
            AlphaMaskCompositor.ReadRgbaPixels(fullBitmap, fullRgba);

            using var srcMat = new Mat(fullH, fullW, MatType.CV_8UC4);
            Marshal.Copy(fullRgba, 0, srcMat.Data, fullRgba.Length);

            var results = ImmutableArray.CreateBuilder<ExtractedScanDocument>(items.Count);
            foreach (ScanExtractionRequestItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DocumentScanFilterCatalog.ValidateQuadWithinBounds(item.Quad, fullW, fullH);

                (int outW, int outH) = item.Quad.ComputeOutputDimensionsPx(_options.MaxOutputEdgePx);
                using SKBitmap unfilteredBmp = WarpQuadToSkBitmap(srcMat, item.Quad, outW, outH);
                byte[] unfilteredPng = EncodeBitmapPng(unfilteredBmp);
                AssetId unfilteredId = _assetStore.RegisterAsset(unfilteredPng);

                AssetId filteredId = unfilteredId;
                if (item.Filter != DocumentScanFilterKind.None)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using SKBitmap filteredBmp = unfilteredBmp.Copy()
                        ?? throw new InvalidOperationException("Failed to clone warped scan bitmap.");
                    DocumentScanFilterEngine.ApplyInPlace(filteredBmp, item.Filter, cancellationToken);
                    byte[] filteredPng = EncodeBitmapPng(filteredBmp);
                    filteredId = _assetStore.RegisterAsset(filteredPng);
                }

                results.Add(new ExtractedScanDocument(
                    RegionId: item.RegionId,
                    LabelAr: item.LabelAr,
                    FilteredAssetId: filteredId,
                    UnfilteredAssetId: unfilteredId,
                    WidthPx: outW,
                    HeightPx: outH,
                    AppliedFilter: item.Filter));
            }

            return results.ToImmutable();
        }, cancellationToken).ConfigureAwait(false);
    }

    public static SKBitmap RenderLiveRectifiedPreviewBitmap(
        SKBitmap sourcePreviewBitmap,
        DocumentQuadPx fullResQuad,
        int fullWidthPx,
        int fullHeightPx,
        DocumentScanFilterKind filterKind,
        int maxPreviewEdgePx = 640)
    {
        ArgumentNullException.ThrowIfNull(sourcePreviewBitmap);
        int srcW = sourcePreviewBitmap.Width;
        int srcH = sourcePreviewBitmap.Height;
        double sx = (double)srcW / Math.Max(1, fullWidthPx);
        double sy = (double)srcH / Math.Max(1, fullHeightPx);

        var previewQuad = new DocumentQuadPx(
            new PointPx(fullResQuad.TopLeft.X * sx, fullResQuad.TopLeft.Y * sy),
            new PointPx(fullResQuad.TopRight.X * sx, fullResQuad.TopRight.Y * sy),
            new PointPx(fullResQuad.BottomRight.X * sx, fullResQuad.BottomRight.Y * sy),
            new PointPx(fullResQuad.BottomLeft.X * sx, fullResQuad.BottomLeft.Y * sy));

        (int outW, int outH) = previewQuad.ComputeOutputDimensionsPx(Math.Max(64, maxPreviewEdgePx));
        byte[] srcRgba = new byte[checked(srcW * srcH * 4)];
        AlphaMaskCompositor.ReadRgbaPixels(sourcePreviewBitmap, srcRgba);

        using var srcMat = new Mat(srcH, srcW, MatType.CV_8UC4);
        Marshal.Copy(srcRgba, 0, srcMat.Data, srcRgba.Length);

        SKBitmap rectified = WarpQuadToSkBitmap(srcMat, previewQuad, outW, outH);
        if (filterKind != DocumentScanFilterKind.None)
        {
            DocumentScanFilterEngine.ApplyInPlace(rectified, filterKind);
        }

        return rectified;
    }

    private static SKBitmap WarpQuadToSkBitmap(Mat srcRgbaMat, DocumentQuadPx quad, int outW, int outH)
    {
        Point2f[] srcPts =
        [
            new Point2f((float)quad.TopLeft.X, (float)quad.TopLeft.Y),
            new Point2f((float)quad.TopRight.X, (float)quad.TopRight.Y),
            new Point2f((float)quad.BottomRight.X, (float)quad.BottomRight.Y),
            new Point2f((float)quad.BottomLeft.X, (float)quad.BottomLeft.Y),
        ];
        Point2f[] dstPts =
        [
            new Point2f(0f, 0f),
            new Point2f(outW - 1f, 0f),
            new Point2f(outW - 1f, outH - 1f),
            new Point2f(0f, outH - 1f),
        ];

        using Mat homography = Cv2.GetPerspectiveTransform(srcPts, dstPts);
        using var warped = new Mat();
        Cv2.WarpPerspective(
            srcRgbaMat,
            warped,
            homography,
            new Size(outW, outH),
            InterpolationFlags.Cubic,
            BorderTypes.Replicate);

        var result = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using SKPixmap dstPixmap = result.PeekPixels()
            ?? throw new InvalidOperationException("Unable to access destination preview pixels.");

        int copyBytesPerRow = outW * 4;
        long srcStep = warped.Step();
        int dstStep = dstPixmap.RowBytes;
        IntPtr srcPtr = warped.Data;
        IntPtr dstPtr = dstPixmap.GetPixels();
        byte[] rowBuf = new byte[copyBytesPerRow];

        for (int y = 0; y < outH; y++)
        {
            Marshal.Copy(IntPtr.Add(srcPtr, (int)(y * srcStep)), rowBuf, 0, copyBytesPerRow);
            Marshal.Copy(rowBuf, 0, IntPtr.Add(dstPtr, y * dstStep), copyBytesPerRow);
        }

        result.NotifyPixelsChanged();
        return result;
    }

    private static byte[] EncodeBitmapPng(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void CollectQuadCandidates(
        Mat binary,
        double detArea,
        double invScaleX,
        double invScaleY,
        double baseWeight,
        List<CandidateQuad> candidates,
        CancellationToken cancellationToken)
    {
        Point[][] contours = Cv2.FindContoursAsArray(
            binary,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        foreach (Point[] contour in contours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contour.Length < 4)
            {
                continue;
            }

            Point[] hull = Cv2.ConvexHull(contour);
            double hullArea = Cv2.ContourArea(hull);
            if (hullArea < detArea * 0.02 || hullArea > detArea * 0.94)
            {
                continue;
            }

            double contourArea = Math.Abs(Cv2.ContourArea(contour));
            double solidity = hullArea > 1.0 ? contourArea / hullArea : 0.0;
            if (solidity < 0.72)
            {
                continue;
            }

            double perimeter = Cv2.ArcLength(hull, closed: true);
            if (perimeter < 32.0)
            {
                continue;
            }

            Point[]? quadDet = null;
            bool isExactApprox = false;
            foreach (double factor in new[] { 0.02, 0.035, 0.05 })
            {
                Point[] approx = Cv2.ApproxPolyDP(hull, factor * perimeter, closed: true);
                if (approx.Length == 4 && Cv2.IsContourConvex(approx))
                {
                    quadDet = approx;
                    isExactApprox = true;
                    break;
                }
            }

            if (quadDet is null && solidity >= 0.85)
            {
                Point[] approx = Cv2.ApproxPolyDP(hull, 0.018 * perimeter, closed: true);
                if (approx.Length is >= 4 and <= 10)
                {
                    RotatedRect minRect = Cv2.MinAreaRect(hull);
                    quadDet = minRect.Points().Select(p => new Point((int)MathF.Round(p.X), (int)MathF.Round(p.Y))).ToArray();
                }
            }

            if (quadDet is null || quadDet.Length != 4)
            {
                continue;
            }

            PointPx[] fullCorners =
            [
                new PointPx(quadDet[0].X * invScaleX, quadDet[0].Y * invScaleY),
                new PointPx(quadDet[1].X * invScaleX, quadDet[1].Y * invScaleY),
                new PointPx(quadDet[2].X * invScaleX, quadDet[2].Y * invScaleY),
                new PointPx(quadDet[3].X * invScaleX, quadDet[3].Y * invScaleY),
            ];

            DocumentQuadPx ordered = DocumentQuadPx.OrderClockwise(fullCorners);
            double quadAreaDet = ordered.ComputeAreaPx() / (invScaleX * invScaleY);
            if (quadAreaDet < detArea * 0.02 || quadAreaDet > detArea * 0.95)
            {
                continue;
            }

            double fillMatch = Math.Clamp(hullArea / Math.Max(1.0, quadAreaDet), 0.0, 1.0);
            double areaRatio = quadAreaDet / detArea;
            double confidence = Math.Clamp(baseWeight * (isExactApprox ? 0.92 : 0.80) * fillMatch, 0.25, 0.99);
            candidates.Add(new CandidateQuad(ordered, areaRatio, confidence));
        }
    }

    private static ImmutableArray<DetectedDocumentRegion> SelectFinalRegions(
        List<CandidateQuad> candidates,
        ScanDetectionMode mode,
        int fullW,
        int fullH)
    {
        if (candidates.Count == 0)
        {
            return
            [
                new DetectedDocumentRegion(
                    Guid.NewGuid(),
                    "المستند المكتشف #1",
                    DocumentQuadPx.CreateInsetDefault(fullW, fullH),
                    Confidence: 0.35),
            ];
        }

        // Deduplicate overlapping detections, keeping highest-confidence/tighter contour.
        List<CandidateQuad> ordered = candidates
            .OrderByDescending(c => c.Confidence + (c.AreaRatio * 0.25))
            .ToList();

        var kept = new List<CandidateQuad>();
        foreach (CandidateQuad cand in ordered)
        {
            bool overlapsExisting = kept.Any(existing => ComputeBoundingBoxIoU(existing.Quad, cand.Quad) > 0.50);
            if (!overlapsExisting)
            {
                kept.Add(cand);
            }
        }

        if (mode == ScanDetectionMode.SingleDocument)
        {
            CandidateQuad best = kept.OrderByDescending(c => c.AreaRatio * c.Confidence).First();
            return
            [
                new DetectedDocumentRegion(
                    Guid.NewGuid(),
                    "المستند المكتشف #1",
                    best.Quad,
                    best.Confidence),
            ];
        }

        // In auto/multi-card mode, drop a huge enclosing frame if 2+ distinct inner cards sit inside it.
        if (kept.Count >= 3)
        {
            kept.RemoveAll(outer =>
                kept.Count(inner => !ReferenceEquals(inner, outer) && IsQuadMostlyInside(inner.Quad, outer.Quad)) >= 2);
        }

        // Order top-to-bottom, left-to-right for predictable numbering.
        List<CandidateQuad> sorted = kept
            .OrderBy(c => Math.Round(c.Quad.TopLeft.Y / Math.Max(1.0, fullH * 0.12)))
            .ThenBy(c => c.Quad.TopLeft.X)
            .Take(8)
            .ToList();

        var regions = ImmutableArray.CreateBuilder<DetectedDocumentRegion>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            string label = sorted.Count > 1 ? $"بطاقة / مستند #{i + 1}" : "المستند المكتشف #1";
            regions.Add(new DetectedDocumentRegion(Guid.NewGuid(), label, sorted[i].Quad, sorted[i].Confidence));
        }

        return regions.ToImmutable();
    }

    private static double ComputeBoundingBoxIoU(DocumentQuadPx a, DocumentQuadPx b)
    {
        (double ax1, double ay1, double ax2, double ay2) = GetBounds(a);
        (double bx1, double by1, double bx2, double by2) = GetBounds(b);

        double ix1 = Math.Max(ax1, bx1);
        double iy1 = Math.Max(ay1, by1);
        double ix2 = Math.Min(ax2, bx2);
        double iy2 = Math.Min(ay2, by2);

        double interW = Math.Max(0.0, ix2 - ix1);
        double interH = Math.Max(0.0, iy2 - iy1);
        double interArea = interW * interH;
        double unionArea = ((ax2 - ax1) * (ay2 - ay1)) + ((bx2 - bx1) * (by2 - by1)) - interArea;
        return unionArea > 1.0 ? interArea / unionArea : 0.0;
    }

    private static bool IsQuadMostlyInside(DocumentQuadPx inner, DocumentQuadPx outer)
    {
        (double ix1, double iy1, double ix2, double iy2) = GetBounds(inner);
        (double ox1, double oy1, double ox2, double oy2) = GetBounds(outer);
        double cx = (ix1 + ix2) * 0.5;
        double cy = (iy1 + iy2) * 0.5;
        double innerArea = Math.Max(1.0, (ix2 - ix1) * (iy2 - iy1));
        double outerArea = Math.Max(1.0, (ox2 - ox1) * (oy2 - oy1));
        return outerArea > innerArea * 1.35 && cx >= ox1 && cx <= ox2 && cy >= oy1 && cy <= oy2;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) GetBounds(DocumentQuadPx q)
    {
        double minX = Math.Min(Math.Min(q.TopLeft.X, q.TopRight.X), Math.Min(q.BottomRight.X, q.BottomLeft.X));
        double maxX = Math.Max(Math.Max(q.TopLeft.X, q.TopRight.X), Math.Max(q.BottomRight.X, q.BottomLeft.X));
        double minY = Math.Min(Math.Min(q.TopLeft.Y, q.TopRight.Y), Math.Min(q.BottomRight.Y, q.BottomLeft.Y));
        double maxY = Math.Max(Math.Max(q.TopLeft.Y, q.TopRight.Y), Math.Max(q.BottomRight.Y, q.BottomLeft.Y));
        return (minX, minY, maxX, maxY);
    }

    private sealed record CandidateQuad(DocumentQuadPx Quad, double AreaRatio, double Confidence);
}
