using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Validation;

namespace Ziwa.Rendering.Export;

public sealed class VectorPdfExporter
{
    private readonly SceneRenderer _renderer;

    public VectorPdfExporter(SceneRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    public byte[] ExportSinglePageToPdfBytes(
        PageModel page,
        double dpi = 300.0,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        return ExportDocumentToPdfBytes(
            [page],
            dpi,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks,
            includeCutContour,
            cancellationToken);
    }

    public byte[] ExportDocumentToPdfBytes(
        IReadOnlyList<PageModel> pages,
        double dpi = 300.0,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException("At least one page is required for PDF export.", nameof(pages));
        }

        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream();
        var metadata = new SKDocumentPdfMetadata
        {
            RasterDpi = (float)dpi,
            EncodingQuality = 95,
            Creator = "Ziwa Studio",
        };

        float ptPerMm = (float)(DomainMath.PointsPerInch / DomainMath.MmPerInch);

        using (SKDocument pdf = SKDocument.CreatePdf(stream, metadata)
            ?? throw new InvalidOperationException("Unable to create Skia PDF document."))
        {
            for (int i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PageModel page = pages[i];
                ArgumentNullException.ThrowIfNull(page);

                (_, _, double totalWmm, double totalHmm) = TiledRasterRenderer.ComputeExportMediaGeometry(
                    page,
                    includeBleed,
                    includeCropMarks,
                    includeRegistrationMarks);

                float widthPt = (float)DomainMath.MmToPt(totalWmm);
                float heightPt = (float)DomainMath.MmToPt(totalHmm);

                SKCanvas canvas = pdf.BeginPage(widthPt, heightPt);
                int saveCount = canvas.Save();
                try
                {
                    canvas.Scale(ptPerMm, ptPerMm);
                    TiledRasterRenderer.RenderPageWithMarksOnMmCanvas(
                        canvas,
                        _renderer,
                        page,
                        dpi,
                        includeBleed,
                        includeCropMarks,
                        includeRegistrationMarks,
                        includeCutContour);
                }
                finally
                {
                    canvas.RestoreToCount(saveCount);
                    pdf.EndPage();
                }
            }

            pdf.Close();
        }

        return stream.ToArray();
    }
}
