using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ziwa.Application.Pages;
using Ziwa.Application.Printing;
using Ziwa.Domain.Models;
using Ziwa.Domain.Validation;

namespace Ziwa.Platform.Windows.Printing;

[SupportedOSPlatform("windows")]
public sealed class GdiPrintSpooler : IPrintSpooler
{
    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterEnumConnections = 0x00000004;

    private const uint PrinterStatusPaused = 0x00000001;
    private const uint PrinterStatusError = 0x00000002;
    private const uint PrinterStatusPaperJam = 0x00000008;
    private const uint PrinterStatusPaperOut = 0x00000010;
    private const uint PrinterStatusOffline = 0x00000080;
    private const uint PrinterStatusPrinting = 0x00000400;

    private const uint DmOutBuffer = 0x00000002;
    private const uint DmInBuffer = 0x00000008;
    private const uint DmOrientation = 0x00000001;
    private const uint DmCopies = 0x00000100;
    private const short DmOrientPortrait = 1;
    private const short DmOrientLandscape = 2;

    private const int DmFieldsOffset = 72;
    private const int DmOrientationOffset = 76;
    private const int DmCopiesOffset = 86;

    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int LogPixelsX = 88;
    private const int LogPixelsY = 90;
    private const int PhysicalWidth = 110;
    private const int PhysicalHeight = 111;
    private const int PhysicalOffsetX = 112;
    private const int PhysicalOffsetY = 113;

    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const uint SrcCopy = 0x00CC0020;
    private const uint JobControlCancel = 3;

    private readonly IPrintPageRasterizer _rasterizer;
    private readonly VirtualPrintSpoolerCore _virtualFallback;

    public GdiPrintSpooler(IPrintPageRasterizer rasterizer)
    {
        _rasterizer = rasterizer ?? throw new ArgumentNullException(nameof(rasterizer));
        _virtualFallback = new VirtualPrintSpoolerCore(rasterizer);
    }

    public IReadOnlyList<PrinterDeviceInfo> EnumeratePrinters()
    {
        var printers = new List<PrinterDeviceInfo>();
        bool hasHardwareDefault = false;

        try
        {
            string? defaultPrinterName = TryGetDefaultPrinterName();
            uint flags = PrinterEnumLocal | PrinterEnumConnections;

            _ = EnumPrintersW(flags, null, 2, IntPtr.Zero, 0, out uint bytesNeeded, out _);
            if (bytesNeeded > 0)
            {
                using SafeGlobalMemoryHandle buffer = SafeGlobalMemoryHandle.Allocate((int)bytesNeeded);
                IntPtr bufferPtr = buffer.DangerousGetHandle();

                if (EnumPrintersW(flags, null, 2, bufferPtr, bytesNeeded, out _, out uint returned) && returned > 0)
                {
                    int structSize = Marshal.SizeOf<PRINTER_INFO_2W>();
                    for (uint i = 0; i < returned; i++)
                    {
                        IntPtr itemPtr = IntPtr.Add(bufferPtr, checked((int)(i * (uint)structSize)));
                        PRINTER_INFO_2W info = Marshal.PtrToStructure<PRINTER_INFO_2W>(itemPtr);
                        string? name = Marshal.PtrToStringUni(info.pPrinterName);
                        if (string.IsNullOrWhiteSpace(name) ||
                            string.Equals(name, PrintCalibrationPlanner.VirtualPrinterName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        bool isDefault = !string.IsNullOrWhiteSpace(defaultPrinterName) &&
                            string.Equals(name, defaultPrinterName, StringComparison.OrdinalIgnoreCase);
                        if (isDefault)
                        {
                            hasHardwareDefault = true;
                        }

                        printers.Add(new PrinterDeviceInfo(
                            PrinterName: name,
                            IsDefault: isDefault,
                            IsVirtualPreview: false,
                            StatusTextAr: FormatPrinterStatusAr(info.Status)));
                    }
                }
            }
        }
        catch
        {
            // Spooler service may be stopped or unavailable; always preserve virtual preview printer.
        }

        printers.Add(new PrinterDeviceInfo(
            PrinterName: PrintCalibrationPlanner.VirtualPrinterName,
            IsDefault: !hasHardwareDefault,
            IsVirtualPreview: true,
            StatusTextAr: PrintCalibrationPlanner.VirtualPrinterReadyStatusAr));

        return printers;
    }

    public HardwarePrintCapabilities QueryCapabilities(
        string printerName,
        PrintOrientationKind orientation = PrintOrientationKind.Portrait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);

        if (IsVirtualPrinter(printerName))
        {
            return _virtualFallback.QueryCapabilities(printerName, orientation);
        }

        using SafeGdiDcHandle hdc = CreateConfiguredPrinterDc(printerName, orientation, copies: null);
        return ReadCapabilitiesFromDc(printerName, hdc);
    }

    public Task<PrintJobSubmissionResult> PrintDocumentAsync(
        ZiwaDocument document,
        DirectPrintJobTicket ticket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket.PrinterName);
        cancellationToken.ThrowIfCancellationRequested();

        if (document.Pages.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException("Cannot print a document with no pages.");
        }

        if (IsVirtualPrinter(ticket.PrinterName))
        {
            return _virtualFallback.PrintDocumentAsync(document, ticket, cancellationToken);
        }

        return Task.Run(
            () => PrintDocumentOnGdi(document, ticket, cancellationToken),
            cancellationToken);
    }

    public bool CancelSpoolJob(string printerName, int spoolJobId)
    {
        if (string.IsNullOrWhiteSpace(printerName) || spoolJobId <= 0)
        {
            return false;
        }

        if (IsVirtualPrinter(printerName))
        {
            return _virtualFallback.CancelSpoolJob(printerName, spoolJobId);
        }

        if (!OpenPrinterW(printerName, out SafePrinterHandle hPrinter, IntPtr.Zero))
        {
            hPrinter.Dispose();
            return false;
        }

        using (hPrinter)
        {
            return !hPrinter.IsInvalid && SetJobW(hPrinter, (uint)spoolJobId, 0, IntPtr.Zero, JobControlCancel);
        }
    }

    private PrintJobSubmissionResult PrintDocumentOnGdi(
        ZiwaDocument document,
        DirectPrintJobTicket ticket,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int copies = Math.Clamp(ticket.Copies, 1, 999);
        ImmutableArray<int> pageIndices = MultiPageOperations.ResolvePageIndices(
            document,
            ticket.PageRange,
            ticket.SpecificPageIndices);

        using SafeGdiDcHandle hdc = CreateConfiguredPrinterDc(ticket.PrinterName, ticket.Orientation, copies);
        HardwarePrintCapabilities capabilities = ReadCapabilitiesFromDc(ticket.PrinterName, hdc);

        var preparedPages = new List<(PrintRasterBuffer Buffer, PhysicalPrintCalibrationPlan Plan)>(pageIndices.Length);
        foreach (int pageIndex in pageIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PageModel page = document.Pages[pageIndex];
            PhysicalPrintCalibrationPlan plan = PrintCalibrationPlanner.ComputeCalibrationPlan(page, capabilities, ticket);
            PrintRasterBuffer raster = _rasterizer.RasterizePageForGdi(
                page,
                capabilities.DpiX,
                capabilities.DpiY,
                ticket.IncludeBleed,
                ticket.IncludeCropMarks);

            preparedPages.Add((raster, plan));
        }

        var docInfo = new DOCINFOW
        {
            cbSize = Marshal.SizeOf<DOCINFOW>(),
            lpszDocName = string.IsNullOrWhiteSpace(ticket.DocumentTitle) ? "Ziwa Studio Document" : ticket.DocumentTitle,
            lpszOutput = null,
            lpszDatatype = null,
            fwType = 0,
        };

        int spoolJobId = StartDocW(hdc, ref docInfo);
        if (spoolJobId <= 0)
        {
            throw new InvalidOperationException(
                $"Win32 StartDocW failed for '{ticket.PrinterName}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        bool docStarted = true;
        int pagesPrinted = 0;

        try
        {
            for (int copy = 0; copy < copies; copy++)
            {
                foreach ((PrintRasterBuffer raster, PhysicalPrintCalibrationPlan plan) in preparedPages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (StartPage(hdc) <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Win32 StartPage failed for '{ticket.PrinterName}' (Win32 error {Marshal.GetLastWin32Error()}).");
                    }

                    BlitTopDownBgraToDc(hdc, raster, plan, ticket.PrinterName);

                    if (EndPage(hdc) <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Win32 EndPage failed for '{ticket.PrinterName}' (Win32 error {Marshal.GetLastWin32Error()}).");
                    }

                    pagesPrinted++;
                }
            }

            if (EndDoc(hdc) <= 0)
            {
                throw new InvalidOperationException(
                    $"Win32 EndDoc failed for '{ticket.PrinterName}' (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            docStarted = false;
        }
        catch
        {
            if (docStarted)
            {
                _ = AbortDoc(hdc);
            }

            throw;
        }

        return new PrintJobSubmissionResult(
            SpoolJobId: spoolJobId,
            PagesPrinted: pagesPrinted,
            PrinterName: ticket.PrinterName,
            FirstPagePlan: preparedPages[0].Plan);
    }

    private static SafeGdiDcHandle CreateConfiguredPrinterDc(
        string printerName,
        PrintOrientationKind orientation,
        int? copies)
    {
        if (!OpenPrinterW(printerName, out SafePrinterHandle hPrinter, IntPtr.Zero) || hPrinter.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            hPrinter.Dispose();
            throw new InvalidOperationException(
                $"Win32 OpenPrinterW failed for '{printerName}' (Win32 error {err}).");
        }

        using (hPrinter)
        {
            int devModeBytes = DocumentPropertiesW(IntPtr.Zero, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (devModeBytes <= 0)
            {
                throw new InvalidOperationException(
                    $"Win32 DocumentPropertiesW size query failed for '{printerName}' (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            using SafeGlobalMemoryHandle devModeMem = SafeGlobalMemoryHandle.Allocate(devModeBytes);
            IntPtr devModePtr = devModeMem.DangerousGetHandle();

            if (DocumentPropertiesW(IntPtr.Zero, hPrinter, printerName, devModePtr, IntPtr.Zero, DmOutBuffer) < 0)
            {
                throw new InvalidOperationException(
                    $"Win32 DocumentPropertiesW read failed for '{printerName}' (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            uint dmFields = unchecked((uint)Marshal.ReadInt32(devModePtr, DmFieldsOffset));
            dmFields |= DmOrientation;
            Marshal.WriteInt16(
                devModePtr,
                DmOrientationOffset,
                orientation == PrintOrientationKind.Landscape ? DmOrientLandscape : DmOrientPortrait);

            if (copies is int copyCount)
            {
                dmFields |= DmCopies;
                Marshal.WriteInt16(devModePtr, DmCopiesOffset, (short)Math.Clamp(copyCount, 1, 999));
            }

            Marshal.WriteInt32(devModePtr, DmFieldsOffset, unchecked((int)dmFields));

            if (DocumentPropertiesW(IntPtr.Zero, hPrinter, printerName, devModePtr, devModePtr, DmInBuffer | DmOutBuffer) < 0)
            {
                throw new InvalidOperationException(
                    $"Win32 DocumentPropertiesW apply failed for '{printerName}' (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            SafeGdiDcHandle hdc = CreateDCW("WINSPOOL", printerName, null, devModePtr);
            if (hdc.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                hdc.Dispose();
                throw new InvalidOperationException(
                    $"Win32 CreateDCW failed for '{printerName}' (Win32 error {err}).");
            }

            return hdc;
        }
    }

    private static HardwarePrintCapabilities ReadCapabilitiesFromDc(string printerName, SafeGdiDcHandle hdc)
    {
        int dpiX = GetDeviceCaps(hdc, LogPixelsX);
        int dpiY = GetDeviceCaps(hdc, LogPixelsY);
        int physicalWidthPx = GetDeviceCaps(hdc, PhysicalWidth);
        int physicalHeightPx = GetDeviceCaps(hdc, PhysicalHeight);
        int physicalOffsetXPx = GetDeviceCaps(hdc, PhysicalOffsetX);
        int physicalOffsetYPx = GetDeviceCaps(hdc, PhysicalOffsetY);
        int printableWidthPx = GetDeviceCaps(hdc, HorzRes);
        int printableHeightPx = GetDeviceCaps(hdc, VertRes);

        if (!SchemaValidator.IsFinitePositive(dpiX) ||
            !SchemaValidator.IsFinitePositive(dpiY) ||
            !SchemaValidator.IsFinitePositive(physicalWidthPx) ||
            !SchemaValidator.IsFinitePositive(physicalHeightPx) ||
            !SchemaValidator.IsFinitePositive(printableWidthPx) ||
            !SchemaValidator.IsFinitePositive(printableHeightPx) ||
            physicalOffsetXPx < 0 ||
            physicalOffsetYPx < 0)
        {
            throw new InvalidOperationException(
                $"Win32 GetDeviceCaps returned invalid capabilities for '{printerName}' (Win32 error {Marshal.GetLastWin32Error()}): " +
                $"DPI=({dpiX}x{dpiY}), Physical=({physicalWidthPx}x{physicalHeightPx}), " +
                $"Offset=({physicalOffsetXPx},{physicalOffsetYPx}), Printable=({printableWidthPx}x{printableHeightPx}).");
        }

        return new HardwarePrintCapabilities(
            PrinterName: printerName,
            DpiX: dpiX,
            DpiY: dpiY,
            PhysicalWidthPx: physicalWidthPx,
            PhysicalHeightPx: physicalHeightPx,
            PhysicalOffsetXPx: physicalOffsetXPx,
            PhysicalOffsetYPx: physicalOffsetYPx,
            PrintableWidthPx: printableWidthPx,
            PrintableHeightPx: printableHeightPx);
    }

    private static void BlitTopDownBgraToDc(
        SafeGdiDcHandle hdc,
        PrintRasterBuffer raster,
        PhysicalPrintCalibrationPlan plan,
        string printerName)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = raster.WidthPx,
            biHeight = -raster.HeightPx,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BiRgb,
            biSizeImage = (uint)raster.BgraTopDownBytes.Length,
            biXPelsPerMeter = 0,
            biYPelsPerMeter = 0,
            biClrUsed = 0,
            biClrImportant = 0,
        };

        var pinned = GCHandle.Alloc(raster.BgraTopDownBytes, GCHandleType.Pinned);
        try
        {
            int scanLines = StretchDIBits(
                hdc,
                plan.DestDcLeftPx,
                plan.DestDcTopPx,
                plan.DestDcWidthPx,
                plan.DestDcHeightPx,
                0,
                0,
                raster.WidthPx,
                raster.HeightPx,
                pinned.AddrOfPinnedObject(),
                ref bmi,
                DibRgbColors,
                SrcCopy);

            if (scanLines <= 0)
            {
                throw new InvalidOperationException(
                    $"Win32 StretchDIBits failed for '{printerName}' (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private static bool IsVirtualPrinter(string printerName) =>
        string.Equals(printerName, PrintCalibrationPlanner.VirtualPrinterName, StringComparison.Ordinal);

    private static string? TryGetDefaultPrinterName()
    {
        int charCount = 0;
        _ = GetDefaultPrinterW(null, ref charCount);
        if (charCount <= 0)
        {
            return null;
        }

        char[] buffer = new char[charCount];
        if (!GetDefaultPrinterW(buffer, ref charCount) || charCount <= 1)
        {
            return null;
        }

        return new string(buffer, 0, charCount - 1).TrimEnd('\0');
    }

    private static string FormatPrinterStatusAr(uint status)
    {
        if (status == 0)
        {
            return "جاهزة";
        }

        if ((status & PrinterStatusOffline) != 0)
        {
            return "غير متصلة";
        }

        if ((status & PrinterStatusPaperJam) != 0)
        {
            return "انحشار ورق";
        }

        if ((status & PrinterStatusPaperOut) != 0)
        {
            return "نفاد الورق";
        }

        if ((status & PrinterStatusError) != 0)
        {
            return "خطأ في الطابعة";
        }

        if ((status & PrinterStatusPaused) != 0)
        {
            return "متوقفة مؤقتاً";
        }

        if ((status & PrinterStatusPrinting) != 0)
        {
            return "جاري الطباعة";
        }

        return "جاهزة";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2W
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFOW
    {
        public int cbSize;
        public string? lpszDocName;
        public string? lpszOutput;
        public string? lpszDatatype;
        public uint fwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinterW([Out] char[]? pszBuffer, ref int pcchBuffer);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrintersW(
        uint Flags,
        string? Name,
        uint Level,
        IntPtr pPrinterEnum,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinterW(
        string pPrinterName,
        out SafePrinterHandle phPrinter,
        IntPtr pDefault);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentPropertiesW(
        IntPtr hwnd,
        SafePrinterHandle hPrinter,
        string pDeviceName,
        IntPtr pDevModeOutput,
        IntPtr pDevModeInput,
        uint fMode);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetJobW(
        SafePrinterHandle hPrinter,
        uint JobId,
        uint Level,
        IntPtr pJob,
        uint Command);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeGdiDcHandle CreateDCW(
        string? pwszDriver,
        string pwszDevice,
        string? pszPort,
        IntPtr pdm);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDeviceCaps(SafeGdiDcHandle hdc, int index);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocW(SafeGdiDcHandle hdc, ref DOCINFOW lpdi);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(SafeGdiDcHandle hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int AbortDoc(SafeGdiDcHandle hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(SafeGdiDcHandle hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(SafeGdiDcHandle hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(
        SafeGdiDcHandle hdc,
        int xDest,
        int yDest,
        int DestWidth,
        int DestHeight,
        int xSrc,
        int ySrc,
        int SrcWidth,
        int SrcHeight,
        IntPtr lpBits,
        ref BITMAPINFOHEADER lpbmi,
        uint iUsage,
        uint rop);
}
