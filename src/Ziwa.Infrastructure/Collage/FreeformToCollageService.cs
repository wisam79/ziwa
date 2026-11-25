using Ziwa.Application.Abstractions;
using Ziwa.Application.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Export;

namespace Ziwa.Infrastructure.Collage;

public sealed class FreeformToCollageService : IFreeformToCollageService
{
    private readonly PngJpegExporter _exporter;
    private readonly IAssetRegistrar _assetRegistrar;

    public FreeformToCollageService(PngJpegExporter exporter, IAssetRegistrar assetRegistrar)
    {
        _exporter = exporter;
        _assetRegistrar = assetRegistrar;
    }

    public ZiwaDocument TransferFreeformPageToCollage(
        PageModel sourceFreeformPage,
        ZiwaDocument targetCollageDocument,
        double captureDpi = 300.0)
    {
        if (targetCollageDocument.ActivePage.CollageLayout is null)
        {
            throw new InvalidOperationException("Target document active page does not have an active CollageLayout.");
        }

        byte[] capturedPng = _exporter.ExportPageToBytes(sourceFreeformPage, captureDpi, RasterExportFormat.Png);
        AssetId capturedAssetId = _assetRegistrar.RegisterAsset(capturedPng);

        // Preserve target sheet dimensions and CollageGridSpec untouched
        ZiwaDocument populated = CollageOperations.EnqueuePendingAssets(targetCollageDocument, [capturedAssetId]);
        return populated.ActivePage.CollageLayout!.AutoRepeatMode
            ? populated
            : CollageOperations.AutoDistributePendingToSlots(populated);
    }
}
