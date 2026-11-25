using System.Collections.Immutable;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;

namespace Ziwa.Application.Printing;

public enum PrintOrientationKind
{
    Portrait,
    Landscape,
}

public sealed record PrinterDeviceInfo(
    string PrinterName,
    bool IsDefault,
    bool IsVirtualPreview,
    string StatusTextAr);

public sealed record HardwarePrintCapabilities(
    string PrinterName,
    int DpiX,
    int DpiY,
    int PhysicalWidthPx,
    int PhysicalHeightPx,
    int PhysicalOffsetXPx,
    int PhysicalOffsetYPx,
    int PrintableWidthPx,
    int PrintableHeightPx)
{
    public double PhysicalWidthMm => DomainMath.PxToMm(PhysicalWidthPx, DpiX);

    public double PhysicalHeightMm => DomainMath.PxToMm(PhysicalHeightPx, DpiY);

    public double UnprintableLeftMm => DomainMath.PxToMm(PhysicalOffsetXPx, DpiX);

    public double UnprintableTopMm => DomainMath.PxToMm(PhysicalOffsetYPx, DpiY);

    public double PrintableWidthMm => DomainMath.PxToMm(PrintableWidthPx, DpiX);

    public double PrintableHeightMm => DomainMath.PxToMm(PrintableHeightPx, DpiY);

    public double UnprintableRightMm =>
        DomainMath.PxToMm(Math.Max(0, PhysicalWidthPx - PhysicalOffsetXPx - PrintableWidthPx), DpiX);

    public double UnprintableBottomMm =>
        DomainMath.PxToMm(Math.Max(0, PhysicalHeightPx - PhysicalOffsetYPx - PrintableHeightPx), DpiY);
}

public sealed record DirectPrintJobTicket(
    string PrinterName,
    string DocumentTitle,
    PrintOrientationKind Orientation = PrintOrientationKind.Portrait,
    int Copies = 1,
    bool Borderless = false,
    bool IncludeBleed = false,
    bool IncludeCropMarks = false,
    ExportPageRangeKind PageRange = ExportPageRangeKind.ActivePageOnly,
    ImmutableArray<int> SpecificPageIndices = default);

public sealed record PhysicalPrintCalibrationPlan(
    double SheetWidthMm,
    double SheetHeightMm,
    double PrintableLeftMm,
    double PrintableTopMm,
    double PrintableWidthMm,
    double PrintableHeightMm,
    double ContentPhysicalLeftMm,
    double ContentPhysicalTopMm,
    double ContentWidthMm,
    double ContentHeightMm,
    int DestDcLeftPx,
    int DestDcTopPx,
    int DestDcWidthPx,
    int DestDcHeightPx,
    bool ClipsAtHardwareMargins);

public sealed record PrintRasterBuffer(
    int WidthPx,
    int HeightPx,
    byte[] BgraTopDownBytes);

public sealed record PrintJobSubmissionResult(
    int SpoolJobId,
    int PagesPrinted,
    string PrinterName,
    PhysicalPrintCalibrationPlan FirstPagePlan);

public interface IPrintPageRasterizer
{
    PrintRasterBuffer RasterizePageForGdi(
        PageModel page,
        double dpiX,
        double dpiY,
        bool includeBleed,
        bool includeCropMarks);
}

public interface IPrintSpooler
{
    IReadOnlyList<PrinterDeviceInfo> EnumeratePrinters();

    HardwarePrintCapabilities QueryCapabilities(
        string printerName,
        PrintOrientationKind orientation = PrintOrientationKind.Portrait);

    Task<PrintJobSubmissionResult> PrintDocumentAsync(
        ZiwaDocument document,
        DirectPrintJobTicket ticket,
        CancellationToken cancellationToken = default);

    bool CancelSpoolJob(string printerName, int spoolJobId);
}
