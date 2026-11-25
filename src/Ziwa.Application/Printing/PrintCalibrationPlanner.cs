using System.Collections.Concurrent;
using System.Collections.Immutable;
using Ziwa.Application.Pages;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Validation;

namespace Ziwa.Application.Printing;

public static class PrintCalibrationPlanner
{
    public const string VirtualPrinterName = "طابعة زِيوا الافتراضية للمعاينة (300 DPI)";
    public const string VirtualPrinterReadyStatusAr = "جاهزة للمعاينة والاختبار";
    public const double CropMarksMarginMm = 8.0;

    public static HardwarePrintCapabilities CreateVirtualA4Capabilities(
        string printerName,
        PrintOrientationKind orientation = PrintOrientationKind.Portrait,
        bool borderless = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);

        const int dpi = 300;
        const int portraitWidthPx = 2480;
        const int portraitHeightPx = 3508;

        int physicalWidthPx = orientation == PrintOrientationKind.Landscape ? portraitHeightPx : portraitWidthPx;
        int physicalHeightPx = orientation == PrintOrientationKind.Landscape ? portraitWidthPx : portraitHeightPx;
        int offsetPx = borderless ? 0 : 50;
        int printableWidthPx = physicalWidthPx - (offsetPx * 2);
        int printableHeightPx = physicalHeightPx - (offsetPx * 2);

        return new HardwarePrintCapabilities(
            PrinterName: printerName,
            DpiX: dpi,
            DpiY: dpi,
            PhysicalWidthPx: physicalWidthPx,
            PhysicalHeightPx: physicalHeightPx,
            PhysicalOffsetXPx: offsetPx,
            PhysicalOffsetYPx: offsetPx,
            PrintableWidthPx: printableWidthPx,
            PrintableHeightPx: printableHeightPx);
    }

    public static PhysicalPrintCalibrationPlan ComputeCalibrationPlan(
        PageModel page,
        HardwarePrintCapabilities capabilities,
        DirectPrintJobTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(ticket);

        SchemaValidator.AssertFinitePositive(page.WidthMm, nameof(page.WidthMm));
        SchemaValidator.AssertFinitePositive(page.HeightMm, nameof(page.HeightMm));
        if (!SchemaValidator.IsFiniteNonNegative(page.BleedMm))
        {
            throw new ArgumentOutOfRangeException(nameof(page.BleedMm), page.BleedMm, "Bleed must be finite and >= 0.");
        }

        SchemaValidator.AssertFinitePositive(capabilities.DpiX, nameof(capabilities.DpiX));
        SchemaValidator.AssertFinitePositive(capabilities.DpiY, nameof(capabilities.DpiY));
        SchemaValidator.AssertFinitePositive(capabilities.PhysicalWidthPx, nameof(capabilities.PhysicalWidthPx));
        SchemaValidator.AssertFinitePositive(capabilities.PhysicalHeightPx, nameof(capabilities.PhysicalHeightPx));
        SchemaValidator.AssertFinitePositive(capabilities.PrintableWidthPx, nameof(capabilities.PrintableWidthPx));
        SchemaValidator.AssertFinitePositive(capabilities.PrintableHeightPx, nameof(capabilities.PrintableHeightPx));
        if (capabilities.PhysicalOffsetXPx < 0 || capabilities.PhysicalOffsetYPx < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capabilities), "Physical printer offsets cannot be negative.");
        }

        double bleedMm = ticket.IncludeBleed ? page.BleedMm : 0.0;
        double marksMm = ticket.IncludeCropMarks ? CropMarksMarginMm : 0.0;
        double contentWidthMm = page.WidthMm + (2.0 * (bleedMm + marksMm));
        double contentHeightMm = page.HeightMm + (2.0 * (bleedMm + marksMm));

        double leftMm = (capabilities.PhysicalWidthMm - contentWidthMm) * 0.5;
        double topMm = (capabilities.PhysicalHeightMm - contentHeightMm) * 0.5;

        int effOffsetX = ticket.Borderless ? 0 : capabilities.PhysicalOffsetXPx;
        int effOffsetY = ticket.Borderless ? 0 : capabilities.PhysicalOffsetYPx;

        int destDcLeftPx = (int)Math.Round(DomainMath.MmToPx(leftMm, capabilities.DpiX), MidpointRounding.AwayFromZero) - effOffsetX;
        int destDcTopPx = (int)Math.Round(DomainMath.MmToPx(topMm, capabilities.DpiY), MidpointRounding.AwayFromZero) - effOffsetY;
        int destDcWidthPx = DomainMath.MmToPixelsRounded(contentWidthMm, capabilities.DpiX);
        int destDcHeightPx = DomainMath.MmToPixelsRounded(contentHeightMm, capabilities.DpiY);

        bool clips = !ticket.Borderless &&
            (leftMm < capabilities.UnprintableLeftMm - 0.05 ||
             topMm < capabilities.UnprintableTopMm - 0.05 ||
             (leftMm + contentWidthMm) > (capabilities.PhysicalWidthMm - capabilities.UnprintableRightMm + 0.05) ||
             (topMm + contentHeightMm) > (capabilities.PhysicalHeightMm - capabilities.UnprintableBottomMm + 0.05));

        double printableLeftMm = ticket.Borderless ? 0.0 : capabilities.UnprintableLeftMm;
        double printableTopMm = ticket.Borderless ? 0.0 : capabilities.UnprintableTopMm;
        double printableWidthMm = ticket.Borderless ? capabilities.PhysicalWidthMm : capabilities.PrintableWidthMm;
        double printableHeightMm = ticket.Borderless ? capabilities.PhysicalHeightMm : capabilities.PrintableHeightMm;

        return new PhysicalPrintCalibrationPlan(
            SheetWidthMm: capabilities.PhysicalWidthMm,
            SheetHeightMm: capabilities.PhysicalHeightMm,
            PrintableLeftMm: printableLeftMm,
            PrintableTopMm: printableTopMm,
            PrintableWidthMm: printableWidthMm,
            PrintableHeightMm: printableHeightMm,
            ContentPhysicalLeftMm: leftMm,
            ContentPhysicalTopMm: topMm,
            ContentWidthMm: contentWidthMm,
            ContentHeightMm: contentHeightMm,
            DestDcLeftPx: destDcLeftPx,
            DestDcTopPx: destDcTopPx,
            DestDcWidthPx: destDcWidthPx,
            DestDcHeightPx: destDcHeightPx,
            ClipsAtHardwareMargins: clips);
    }
}

public sealed class VirtualPrintSpoolerCore : IPrintSpooler
{
    private readonly IPrintPageRasterizer _rasterizer;
    private readonly ConcurrentDictionary<int, bool> _activeJobs = new();
    private int _nextJobId;

    public VirtualPrintSpoolerCore(IPrintPageRasterizer rasterizer)
    {
        _rasterizer = rasterizer ?? throw new ArgumentNullException(nameof(rasterizer));
    }

    public IReadOnlyList<PrinterDeviceInfo> EnumeratePrinters()
    {
        return
        [
            new PrinterDeviceInfo(
                PrintCalibrationPlanner.VirtualPrinterName,
                IsDefault: true,
                IsVirtualPreview: true,
                StatusTextAr: PrintCalibrationPlanner.VirtualPrinterReadyStatusAr),
        ];
    }

    public HardwarePrintCapabilities QueryCapabilities(
        string printerName,
        PrintOrientationKind orientation = PrintOrientationKind.Portrait)
    {
        string effectiveName = string.IsNullOrWhiteSpace(printerName)
            ? PrintCalibrationPlanner.VirtualPrinterName
            : printerName;
        return PrintCalibrationPlanner.CreateVirtualA4Capabilities(effectiveName, orientation);
    }

    public Task<PrintJobSubmissionResult> PrintDocumentAsync(
        ZiwaDocument document,
        DirectPrintJobTicket ticket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();

        if (document.Pages.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("Cannot print a document with no pages.");
        }

        string effectivePrinterName = string.IsNullOrWhiteSpace(ticket.PrinterName)
            ? PrintCalibrationPlanner.VirtualPrinterName
            : ticket.PrinterName;

        HardwarePrintCapabilities capabilities = PrintCalibrationPlanner.CreateVirtualA4Capabilities(
            effectivePrinterName,
            ticket.Orientation,
            ticket.Borderless);

        ImmutableArray<int> pageIndices = MultiPageOperations.ResolvePageIndices(
            document,
            ticket.PageRange,
            ticket.SpecificPageIndices);

        int copies = Math.Clamp(ticket.Copies, 1, 999);
        PhysicalPrintCalibrationPlan? firstPagePlan = null;

        foreach (int pageIndex in pageIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PageModel page = document.Pages[pageIndex];
            PhysicalPrintCalibrationPlan plan = PrintCalibrationPlanner.ComputeCalibrationPlan(page, capabilities, ticket);
            firstPagePlan ??= plan;

            _ = _rasterizer.RasterizePageForGdi(
                page,
                capabilities.DpiX,
                capabilities.DpiY,
                ticket.IncludeBleed,
                ticket.IncludeCropMarks);
        }

        cancellationToken.ThrowIfCancellationRequested();

        int spoolJobId = Interlocked.Increment(ref _nextJobId);
        _activeJobs[spoolJobId] = true;

        int pagesPrinted = pageIndices.Length * copies;
        return Task.FromResult(new PrintJobSubmissionResult(
            SpoolJobId: spoolJobId,
            PagesPrinted: pagesPrinted,
            PrinterName: effectivePrinterName,
            FirstPagePlan: firstPagePlan!));
    }

    public bool CancelSpoolJob(string printerName, int spoolJobId)
    {
        if (spoolJobId <= 0)
        {
            return false;
        }

        return _activeJobs.TryRemove(spoolJobId, out _);
    }
}
