using System.Runtime.InteropServices;
using SkiaSharp;
using Ziwa.Application.Printing;
using Ziwa.Domain.Math;
using Ziwa.Domain.Models;
using Ziwa.Domain.Validation;
using Ziwa.Rendering;
using Ziwa.Rendering.PrePress;

namespace Ziwa.Infrastructure.Printing;

public sealed class SkiaPrintPageRasterizer : IPrintPageRasterizer
{
    private readonly SceneRenderer _renderer;

    public SkiaPrintPageRasterizer(SceneRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public PrintRasterBuffer RasterizePageForGdi(
        PageModel page,
        double dpiX,
        double dpiY,
        bool includeBleed,
        bool includeCropMarks)
    {
        ArgumentNullException.ThrowIfNull(page);
        SchemaValidator.AssertFinitePositive(page.WidthMm, nameof(page.WidthMm));
        SchemaValidator.AssertFinitePositive(page.HeightMm, nameof(page.HeightMm));
        SchemaValidator.AssertFinitePositive(dpiX, nameof(dpiX));
        SchemaValidator.AssertFinitePositive(dpiY, nameof(dpiY));

        double dpi = Math.Max(dpiX, dpiY);
        double bleedMm = includeBleed ? page.BleedMm : 0.0;
        double marksMm = includeCropMarks ? PrintMarksAndCutContourRenderer.DefaultMarksMarginMm : 0.0;

        double totalWidthMm = page.WidthMm + (2.0 * (bleedMm + marksMm));
        double totalHeightMm = page.HeightMm + (2.0 * (bleedMm + marksMm));

        int widthPx = DomainMath.MmToPixelsRounded(totalWidthMm, dpi);
        int heightPx = DomainMath.MmToPixelsRounded(totalHeightMm, dpi);

        var surfaceInfo = new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(surfaceInfo)
            ?? throw new InvalidOperationException($"Unable to allocate SKSurface ({widthPx}x{heightPx}) for GDI printing.");

        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        float pxPerMm = (float)DomainMath.PixelsPerMm(dpi);
        int rootSave = canvas.Save();
        try
        {
            canvas.Scale(pxPerMm, pxPerMm);

            int pageSave = canvas.Save();
            try
            {
                if (marksMm > 0.0)
                {
                    canvas.Translate((float)marksMm, (float)marksMm);
                }

                var renderOptions = new RenderOptions(
                    Dpi: dpi,
                    QualityMode: RenderQualityMode.PrintFullResolution,
                    IsPreScaledToMillimeters: true,
                    IncludeBleed: includeBleed);
                _renderer.RenderPage(canvas, page, renderOptions);
            }
            finally
            {
                canvas.RestoreToCount(pageSave);
            }

            if (includeCropMarks)
            {
                int marksSave = canvas.Save();
                try
                {
                    float trimOriginMm = (float)(marksMm + bleedMm);
                    if (trimOriginMm > 0f)
                    {
                        canvas.Translate(trimOriginMm, trimOriginMm);
                    }

                    PrintMarksAndCutContourRenderer.RenderCropAndRegistrationMarks(
                        canvas,
                        page,
                        includeCropMarks: true,
                        includeRegistrationMarks: false);
                }
                finally
                {
                    canvas.RestoreToCount(marksSave);
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(rootSave);
        }

        canvas.Flush();

        int rowBytes = checked(widthPx * 4);
        byte[] bgraBytes = new byte[checked(rowBytes * heightPx)];
        var readInfo = new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        var pinned = GCHandle.Alloc(bgraBytes, GCHandleType.Pinned);
        try
        {
            if (!surface.ReadPixels(readInfo, pinned.AddrOfPinnedObject(), rowBytes, 0, 0))
            {
                throw new InvalidOperationException($"Failed to read BGRA pixels from SKSurface ({widthPx}x{heightPx}).");
            }
        }
        finally
        {
            pinned.Free();
        }

        return new PrintRasterBuffer(widthPx, heightPx, bgraBytes);
    }
}

public sealed class FallbackPrintSpooler : IPrintSpooler
{
    public const string VirtualPrinterName = PrintCalibrationPlanner.VirtualPrinterName;

    private readonly VirtualPrintSpoolerCore _core;

    public FallbackPrintSpooler(IPrintPageRasterizer rasterizer)
    {
        _core = new VirtualPrintSpoolerCore(rasterizer);
    }

    public IReadOnlyList<PrinterDeviceInfo> EnumeratePrinters() => _core.EnumeratePrinters();

    public HardwarePrintCapabilities QueryCapabilities(
        string printerName,
        PrintOrientationKind orientation = PrintOrientationKind.Portrait) =>
        _core.QueryCapabilities(printerName, orientation);

    public Task<PrintJobSubmissionResult> PrintDocumentAsync(
        ZiwaDocument document,
        DirectPrintJobTicket ticket,
        CancellationToken cancellationToken = default) =>
        _core.PrintDocumentAsync(document, ticket, cancellationToken);

    public bool CancelSpoolJob(string printerName, int spoolJobId) =>
        _core.CancelSpoolJob(printerName, spoolJobId);
}
