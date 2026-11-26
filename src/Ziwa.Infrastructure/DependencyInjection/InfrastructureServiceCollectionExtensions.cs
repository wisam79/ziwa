using Microsoft.Extensions.DependencyInjection;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Ai;
using Ziwa.Application.Commands;
using Ziwa.Application.Persistence;
using Ziwa.Application.Printing;
using Ziwa.Application.Scanner;
using Ziwa.Application.Services;
using Ziwa.Application.Session;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Infrastructure.Ai;
using Ziwa.Infrastructure.Assets;
using Ziwa.Infrastructure.Collage;
using Ziwa.Infrastructure.Export;
using Ziwa.Infrastructure.IO;
using Ziwa.Infrastructure.Persistence;
using Ziwa.Infrastructure.Platform;
using Ziwa.Infrastructure.Printing;
using Ziwa.Infrastructure.Scanner;
using Ziwa.Rendering;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Typography;

namespace Ziwa.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    private const string StudioBadgeSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
          <circle cx="32" cy="32" r="28" fill="#0B5CAB" />
          <polygon points="32,12 38,26 53,26 41,35 46,50 32,41 18,50 23,35 11,26 26,26" fill="#FFFFFF" />
        </svg>
        """;

    public static IServiceCollection AddZiwaCoreServices(
        this IServiceCollection services,
        ProjectStorageOptions? storageOptions = null,
        OfflineAiOptions? aiOptions = null,
        DocumentScannerOptions? scannerOptions = null)
    {
        ProjectStorageOptions resolvedStorage = storageOptions ?? new ProjectStorageOptions(
            Environment.GetEnvironmentVariable("ZIWA_DATA_DIRECTORY") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZiwaStudio"));
        OfflineAiOptions resolvedAi = aiOptions ?? new OfflineAiOptions(
            Path.Combine(resolvedStorage.RootDirectory, "Models"));
        DocumentScannerOptions resolvedScanner = scannerOptions ?? new DocumentScannerOptions();

        services.AddSingleton(resolvedStorage);
        services.AddSingleton(resolvedAi);
        services.AddSingleton(resolvedScanner);
        services.AddSingleton<IProjectStore, ZiwaProjectStore>();
        services.AddSingleton<IProjectAssetStore, RenderingProjectAssetStore>();
        services.AddSingleton<IProjectLibrary, SqliteProjectRepo>();
        services.AddSingleton<ICrashGuard, CrashGuard>();
        services.AddSingleton<AssetStore>();
        services.AddSingleton<IRenderAssetProvider>(sp => sp.GetRequiredService<AssetStore>());
        services.AddSingleton<IAssetRegistrar, RenderingAssetRegistrar>();
        services.AddSingleton<IImageAssetImporter, FileImageAssetImporter>();
        services.AddSingleton<IBackgroundRemovalService, OnnxBackgroundRemover>();
        services.AddSingleton<IFaceFramingService, OnnxFaceFramingService>();
        services.AddSingleton<OfflineAiCoordinator>();
        services.AddSingleton<IDocumentScannerService, OpenCvDocumentScanner>();
        services.AddSingleton<DocumentScannerCoordinator>();
        services.AddSingleton<FontRegistry>();
        services.AddSingleton<HarfBuzzArabicShaper>();
        services.AddSingleton<TextOnCurveRenderer>();
        services.AddSingleton<SceneRenderer>();
        services.AddSingleton<PngJpegExporter>();
        services.AddSingleton<VectorPdfExporter>();
        services.AddSingleton<CmykTiffExporter>();
        services.AddSingleton<IAtomicFileWriter, AtomicFileWriter>();
        services.AddSingleton<IPageExportService, PageExportService>();
        services.AddSingleton<IFreeformToCollageService, FreeformToCollageService>();
        services.AddSingleton<IPlatformCapabilities, FallbackPlatformCapabilities>();
        services.AddSingleton<IPrintPageRasterizer, SkiaPrintPageRasterizer>();
        services.AddSingleton<IPrintSpooler, FallbackPrintSpooler>();
        services.AddSingleton<CommandRegistry>();

        services.AddSingleton(sp =>
        {
            IAssetRegistrar registrar = sp.GetRequiredService<IAssetRegistrar>();
            AssetId brandLogoId = registrar.RegisterSvgAsset(QuickStartAndBrandService.DefaultStudioLogoSvg);
            return QuickStartAndBrandService.CreateDefaultBrandKit(brandLogoId);
        });

        services.AddSingleton(sp =>
        {
            IAssetRegistrar registrar = sp.GetRequiredService<IAssetRegistrar>();
            byte[] emblemBytes = ProceduralEmblemPngBuilder.BuildEmblemPngBytes();
            AssetId emblemAssetId = registrar.RegisterAsset(emblemBytes);
            AssetId svgBadgeId = registrar.RegisterSvgAsset(StudioBadgeSvg);
            ZiwaDocument doc = SampleVerticalSliceFactory.CreateShowcaseDocument(emblemAssetId);
            var svgNode = new SvgNode(
                Id: NodeId.New(),
                Name: "شارة SVG المتجهة",
                BoundsMm: new RectMm(14.0, 148.0, 20.0, 20.0),
                SvgAssetId: svgBadgeId);
            PageModel updatedPage = doc.ActivePage with { Nodes = doc.ActivePage.Nodes.Add(svgNode) };
            ZiwaDocument enriched = doc with { Pages = doc.Pages.SetItem(0, updatedPage) };
            return new DocumentSession(enriched);
        });

        services.AddSingleton(sp => new WorkspaceDocumentService(sp.GetRequiredService<DocumentSession>()));

        return services;
    }
}
