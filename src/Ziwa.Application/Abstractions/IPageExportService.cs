using System.Collections.Immutable;
using Ziwa.Domain.PrePress;

namespace Ziwa.Application.Abstractions;

public sealed record ExportArtifactResult(
    string FilePath,
    int PixelWidth,
    int PixelHeight,
    double Dpi,
    long ByteLength);

public interface IPageExportService
{
    Task<ExportArtifactResult> ExportActivePageAsync(
        string destinationPath,
        double dpi,
        bool asJpeg = false,
        CancellationToken cancellationToken = default);

    Task<ImmutableArray<ExportArtifactResult>> ExportPagesRasterAsync(
        string destinationPathOrPattern,
        double dpi,
        ExportPageRangeKind pageRange = ExportPageRangeKind.AllPages,
        bool asJpeg = false,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        ImmutableArray<int> specificPageIndices = default,
        CancellationToken cancellationToken = default);

    Task<ExportArtifactResult> ExportVectorPdfAsync(
        string destinationPath,
        double dpi,
        ExportPageRangeKind pageRange = ExportPageRangeKind.AllPages,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        ImmutableArray<int> specificPageIndices = default,
        CancellationToken cancellationToken = default);

    Task<ExportArtifactResult> ExportActivePageCmykTiffAsync(
        string destinationPath,
        double dpi,
        CmykProfileKind profileKind = CmykProfileKind.CoatedFogra39,
        string? customIccFilePath = null,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        CancellationToken cancellationToken = default);
}
