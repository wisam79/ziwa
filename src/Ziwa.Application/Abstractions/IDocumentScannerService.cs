using System.Collections.Immutable;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Scanner;

namespace Ziwa.Application.Abstractions;

public sealed record ScanDetectionResult(
    int ImageWidthPx,
    int ImageHeightPx,
    ImmutableArray<DetectedDocumentRegion> Regions);

public sealed record ScanExtractionRequestItem(
    Guid RegionId,
    string LabelAr,
    DocumentQuadPx Quad,
    DocumentScanFilterKind Filter);

public sealed record ExtractedScanDocument(
    Guid RegionId,
    string LabelAr,
    AssetId FilteredAssetId,
    AssetId UnfilteredAssetId,
    int WidthPx,
    int HeightPx,
    DocumentScanFilterKind AppliedFilter);

public interface IDocumentScannerService
{
    Task<ScanDetectionResult> DetectDocumentsAsync(
        AssetId sourceAssetId,
        ScanDetectionMode mode = ScanDetectionMode.AutoSingleOrMulti,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<ExtractedScanDocument>> ExtractDocumentsAsync(
        AssetId sourceAssetId,
        IReadOnlyList<ScanExtractionRequestItem> items,
        CancellationToken cancellationToken = default);
}
