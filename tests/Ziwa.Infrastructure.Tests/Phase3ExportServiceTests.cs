using System.Collections.Immutable;
using System.Text;
using ImageMagick;
using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Pages;
using Ziwa.Application.Services;
using Ziwa.Application.Workspace;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Infrastructure.Export;
using Ziwa.Infrastructure.IO;
using Ziwa.Rendering;
using Ziwa.Rendering.Assets;
using Ziwa.Rendering.Export;
using Ziwa.Rendering.Typography;

namespace Ziwa.Infrastructure.Tests;

public sealed class Phase3ExportServiceTests
{
    [Fact]
    public async Task PageExportService_ExportsMultiPageRasterVectorPdfAndCmykTiffAtomically()
    {
        using var assetStore = new AssetStore();
        using var fontRegistry = new FontRegistry();
        var shaper = new HarfBuzzArabicShaper(fontRegistry);
        var curveRenderer = new TextOnCurveRenderer(fontRegistry, shaper);
        var renderer = new SceneRenderer(assetStore, shaper, curveRenderer);

        ZiwaDocument doc = QuickStartAndBrandService.CreateQuickStartDocument(QuickStartPresetKind.BusinessCardFront);
        doc = MultiPageOperations.EnsureFrontAndBackPages(doc);

        var workspace = new WorkspaceDocumentService(doc);
        var atomicWriter = new AtomicFileWriter();
        var service = new PageExportService(
            workspace,
            new PngJpegExporter(renderer),
            atomicWriter,
            new VectorPdfExporter(renderer),
            new CmykTiffExporter(renderer));

        string tempDir = Path.Combine(Path.GetTempPath(), $"ziwa-p3-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Multi-page numbered PNG export
            string rasterPattern = Path.Combine(tempDir, "card.png");
            ImmutableArray<ExportArtifactResult> rasterResults = await service.ExportPagesRasterAsync(
                rasterPattern,
                dpi: 300.0,
                pageRange: ExportPageRangeKind.AllPages,
                includeBleed: true,
                includeCropMarks: true);

            Assert.Equal(2, rasterResults.Length);
            Assert.All(rasterResults, r => Assert.True(File.Exists(r.FilePath)));
            Assert.Contains(rasterResults, r => r.FilePath.EndsWith("card-p1.png", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(rasterResults, r => r.FilePath.EndsWith("card-p2.png", StringComparison.OrdinalIgnoreCase));

            // Multi-page Vector PDF export
            string pdfPath = Path.Combine(tempDir, "card.pdf");
            ExportArtifactResult pdfResult = await service.ExportVectorPdfAsync(
                pdfPath,
                dpi: 300.0,
                pageRange: ExportPageRangeKind.AllPages,
                includeBleed: true,
                includeCropMarks: true,
                includeRegistrationMarks: true,
                includeCutContour: true);

            Assert.True(File.Exists(pdfResult.FilePath));
            byte[] pdfBytes = await File.ReadAllBytesAsync(pdfResult.FilePath);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdfBytes, 0, 5));

            // Active page CMYK TIFF export
            string tiffPath = Path.Combine(tempDir, "card-cmyk.tiff");
            ExportArtifactResult tiffResult = await service.ExportActivePageCmykTiffAsync(
                tiffPath,
                dpi: 300.0,
                profileKind: CmykProfileKind.CoatedFogra39,
                includeBleed: true,
                includeCropMarks: true);

            Assert.True(File.Exists(tiffResult.FilePath));
            byte[] tiffBytes = await File.ReadAllBytesAsync(tiffResult.FilePath);
            Assert.True(CmykTiffExporter.IsValidTiffHeader(tiffBytes));
            using var magick = new MagickImage(tiffBytes);
            Assert.Equal(ColorSpace.CMYK, magick.ColorSpace);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
