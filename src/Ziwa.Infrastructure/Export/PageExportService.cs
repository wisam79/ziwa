using System.Collections.Immutable;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Pages;
using Ziwa.Application.Services;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Rendering.Export;

namespace Ziwa.Infrastructure.Export;

public sealed class PageExportService : IPageExportService
{
    private readonly WorkspaceDocumentService _workspace;
    private readonly PngJpegExporter _exporter;
    private readonly IAtomicFileWriter _atomicWriter;
    private readonly VectorPdfExporter? _pdfExporter;
    private readonly CmykTiffExporter? _cmykTiffExporter;

    public PageExportService(
        WorkspaceDocumentService workspace,
        PngJpegExporter exporter,
        IAtomicFileWriter atomicWriter,
        VectorPdfExporter? pdfExporter = null,
        CmykTiffExporter? cmykTiffExporter = null)
    {
        _workspace = workspace;
        _exporter = exporter;
        _atomicWriter = atomicWriter;
        _pdfExporter = pdfExporter;
        _cmykTiffExporter = cmykTiffExporter;
    }

    public async Task<ExportArtifactResult> ExportActivePageAsync(
        string destinationPath,
        double dpi,
        bool asJpeg = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PageModel page = _workspace.CurrentDocument.ActivePage;
        RasterExportFormat format = asJpeg ? RasterExportFormat.Jpeg : RasterExportFormat.Png;
        byte[] bytes = _exporter.ExportPageToBytes(page, dpi, format, cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await _atomicWriter.WriteBytesAtomicallyAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);

        int widthPx = DomainMath.MmToPixelsRounded(page.WidthMm, dpi);
        int heightPx = DomainMath.MmToPixelsRounded(page.HeightMm, dpi);
        return new ExportArtifactResult(destinationPath, widthPx, heightPx, dpi, bytes.LongLength);
    }

    public async Task<ImmutableArray<ExportArtifactResult>> ExportPagesRasterAsync(
        string destinationPathOrPattern,
        double dpi,
        ExportPageRangeKind pageRange = ExportPageRangeKind.AllPages,
        bool asJpeg = false,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        ImmutableArray<int> specificPageIndices = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPathOrPattern);
        cancellationToken.ThrowIfCancellationRequested();

        ZiwaDocument doc = _workspace.CurrentDocument;
        ImmutableArray<int> pageIndices = MultiPageOperations.ResolvePageIndices(doc, pageRange, specificPageIndices);
        RasterExportFormat format = asJpeg ? RasterExportFormat.Jpeg : RasterExportFormat.Png;

        var results = ImmutableArray.CreateBuilder<ExportArtifactResult>(pageIndices.Length);
        bool multi = pageIndices.Length > 1;
        string? directory = Path.GetDirectoryName(destinationPathOrPattern);
        string stem = Path.GetFileNameWithoutExtension(destinationPathOrPattern);
        string ext = Path.GetExtension(destinationPathOrPattern);
        if (string.IsNullOrEmpty(ext))
        {
            ext = asJpeg ? ".jpg" : ".png";
        }

        foreach (int pageIndex in pageIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PageModel page = doc.Pages[pageIndex];
            string targetPath = multi
                ? Path.Combine(directory ?? string.Empty, $"{stem}-p{pageIndex + 1}{ext}")
                : destinationPathOrPattern;

            byte[] bytes = _exporter.ExportPageToBytes(
                page,
                dpi,
                format,
                includeBleed: includeBleed,
                includeCropMarks: includeCropMarks,
                includeRegistrationMarks: includeRegistrationMarks,
                includeCutContour: includeCutContour,
                cancellationToken: cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            await _atomicWriter.WriteBytesAtomicallyAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);

            (_, _, double totalWmm, double totalHmm) = TiledRasterRenderer.ComputeExportMediaGeometry(
                page,
                includeBleed,
                includeCropMarks,
                includeRegistrationMarks);

            results.Add(new ExportArtifactResult(
                targetPath,
                DomainMath.MmToPixelsRounded(totalWmm, dpi),
                DomainMath.MmToPixelsRounded(totalHmm, dpi),
                dpi,
                bytes.LongLength));
        }

        return results.ToImmutable();
    }

    public async Task<ExportArtifactResult> ExportVectorPdfAsync(
        string destinationPath,
        double dpi,
        ExportPageRangeKind pageRange = ExportPageRangeKind.AllPages,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        ImmutableArray<int> specificPageIndices = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (_pdfExporter is null)
        {
            throw new InvalidOperationException("VectorPdfExporter is not configured.");
        }

        ZiwaDocument doc = _workspace.CurrentDocument;
        ImmutableArray<int> pageIndices = MultiPageOperations.ResolvePageIndices(doc, pageRange, specificPageIndices);
        PageModel[] pages = [.. pageIndices.Select(i => doc.Pages[i])];

        byte[] pdfBytes = _pdfExporter.ExportDocumentToPdfBytes(
            pages,
            dpi,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks,
            includeCutContour,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await _atomicWriter.WriteBytesAtomicallyAsync(destinationPath, pdfBytes, cancellationToken).ConfigureAwait(false);

        (_, _, double totalWmm, double totalHmm) = TiledRasterRenderer.ComputeExportMediaGeometry(
            pages[0],
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks);

        return new ExportArtifactResult(
            destinationPath,
            DomainMath.MmToPixelsRounded(totalWmm, dpi),
            DomainMath.MmToPixelsRounded(totalHmm, dpi),
            dpi,
            pdfBytes.LongLength);
    }

    public async Task<ExportArtifactResult> ExportActivePageCmykTiffAsync(
        string destinationPath,
        double dpi,
        CmykProfileKind profileKind = CmykProfileKind.CoatedFogra39,
        string? customIccFilePath = null,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (_cmykTiffExporter is null)
        {
            throw new InvalidOperationException("CmykTiffExporter is not configured.");
        }

        byte[]? customIccBytes = null;
        if (profileKind == CmykProfileKind.CustomIcc && !string.IsNullOrWhiteSpace(customIccFilePath))
        {
            customIccBytes = await File.ReadAllBytesAsync(customIccFilePath, cancellationToken).ConfigureAwait(false);
        }

        PageModel page = _workspace.CurrentDocument.ActivePage;
        byte[] tiffBytes = _cmykTiffExporter.ExportPageToCmykTiffBytes(
            page,
            dpi,
            profileKind,
            customIccBytes,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks,
            includeCutContour,
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await _atomicWriter.WriteBytesAtomicallyAsync(destinationPath, tiffBytes, cancellationToken).ConfigureAwait(false);

        (_, _, double totalWmm, double totalHmm) = TiledRasterRenderer.ComputeExportMediaGeometry(
            page,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks);

        return new ExportArtifactResult(
            destinationPath,
            DomainMath.MmToPixelsRounded(totalWmm, dpi),
            DomainMath.MmToPixelsRounded(totalHmm, dpi),
            dpi,
            tiffBytes.LongLength);
    }
}
