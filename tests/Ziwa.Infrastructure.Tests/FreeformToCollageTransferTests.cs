using Xunit;
using Ziwa.Application.Services;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.Assets;
using Ziwa.Infrastructure.Collage;
using Ziwa.Rendering;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Typography;

namespace Ziwa.Infrastructure.Tests;

public sealed class FreeformToCollageTransferTests
{
    [Fact]
    public void TransferFreeformPageToCollage_DistributesCapturedDesignWithoutMutatingSheetDimensionsOrGridSpec()
    {
        using var assetStore = new AssetStore();
        using var fontRegistry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(fontRegistry);
        var curveRenderer = new TextOnCurveRenderer(fontRegistry, shaper);
        var renderer = new SceneRenderer(assetStore, shaper, curveRenderer);
        var exporter = new PngJpegExporter(renderer);
        var registrar = new RenderingAssetRegistrar(assetStore);
        var transferService = new FreeformToCollageService(exporter, registrar);

        AssetId emblemId = registrar.RegisterAsset(ProceduralEmblemPngBuilder.BuildEmblemPngBytes(128));
        ZiwaDocument freeformDoc = SampleVerticalSliceFactory.CreateShowcaseDocument(emblemId);
        PageModel sourceFreeform = freeformDoc.ActivePage;

        ZiwaDocument targetCollage = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.IdPhotoSheet4x6);
        CollageGridSpec originalSpec = targetCollage.ActivePage.CollageLayout!.Spec;
        double originalW = targetCollage.ActivePage.WidthMm;
        double originalH = targetCollage.ActivePage.HeightMm;

        ZiwaDocument transferred = transferService.TransferFreeformPageToCollage(sourceFreeform, targetCollage, 300.0);
        PageModel resultPage = transferred.ActivePage;

        Assert.Equal(originalW, resultPage.WidthMm);
        Assert.Equal(originalH, resultPage.HeightMm);
        Assert.Equal(originalSpec, resultPage.CollageLayout!.Spec);
        Assert.Contains(resultPage.CollageLayout.Slots, s => s.AssignedAssetId is { IsEmpty: false });
    }
}
