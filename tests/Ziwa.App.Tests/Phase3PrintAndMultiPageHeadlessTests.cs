using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Printing;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;
#if WINDOWS
using Ziwa.Platform.Windows.Printing;
#endif

namespace Ziwa.App.Tests;

public sealed class Phase3PrintAndMultiPageHeadlessTests
{
    [AvaloniaFact]
    public void MultiPageFrontBackCreationTogglingAndProtectedDelete_WorkInMainWindow()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Single(vm.PageTabs);
        Assert.False(vm.CanDeleteActivePage);

        // Protected delete on single page leaves 1 page
        vm.DeleteActivePageCommand.Execute(null);
        Assert.Single(vm.Document.Pages);

        // Front/Back creation adds Back page and activates index 1
        vm.EnsureOrToggleFrontBackCommand.Execute(null);
        Assert.Equal(2, vm.Document.Pages.Length);
        Assert.Equal(2, vm.PageTabs.Count);
        Assert.Equal(1, vm.Document.ActivePageIndex);
        Assert.True(vm.CanDeleteActivePage);

        // Toggle back to Front
        vm.EnsureOrToggleFrontBackCommand.Execute(null);
        Assert.Equal(0, vm.Document.ActivePageIndex);

        // Duplicate + Move + Delete
        vm.DuplicateActivePageCommand.Execute(null);
        Assert.Equal(3, vm.Document.Pages.Length);
        Assert.True(vm.CanMovePageLater);
        vm.MovePageLaterCommand.Execute(null);
        Assert.Equal(2, vm.Document.ActivePageIndex);

        vm.DeleteActivePageCommand.Execute(null);
        Assert.Equal(2, vm.Document.Pages.Length);

        window.Close();
    }

    [AvaloniaFact]
    public void PreflightIssueSelection_SelectsOffendingNodeWithoutMutatingGeometry()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var nearTrimText = new TextNode(
            Id: NodeId.New(),
            Name: "نص قريب من الحافة",
            BoundsMm: new RectMm(0.5, 10.0, 45.0, 12.0),
            Text: "تحذير قص",
            FontFamily: "Cairo",
            FontSizePt: 12.0,
            FontWeight: 600,
            Color: new ColorRgba(20, 24, 31, 255));

        vm.Session.ApplyCommand("إضافة نص قريب من القص", doc =>
        {
            PageModel updated = doc.ActivePage with { Nodes = doc.ActivePage.Nodes.Add(nearTrimText) };
            return doc with { Pages = doc.Pages.SetItem(doc.ActivePageIndex, updated) };
        });

        Assert.True(vm.HasPreflightWarnings);
        PreflightIssue trimIssue = Assert.Single(
            vm.PreflightIssues,
            i => i.NodeId == nearTrimText.Id && i.Kind == PreflightIssueKind.TextNearTrimLine);

        vm.Session.ClearSelection();
        RectMm boundsBefore = nearTrimText.BoundsMm;

        vm.SelectPreflightIssueCommand.Execute(trimIssue);
        Assert.Contains(nearTrimText.Id, vm.Session.SelectedNodeIds);

        TextNode after = Assert.IsType<TextNode>(vm.ActivePage.Nodes.Single(n => n.Id == nearTrimText.Id));
        Assert.Equal(boundsBefore, after.BoundsMm);

        window.Close();
    }

    [AvaloniaFact]
    public async Task PrintDialogOverlay_DirectVirtualPrintCancelAndPdfCmykExport_WorkEndToEnd()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.OpenPrintDialogCommand.Execute(null);
        Assert.True(vm.IsPrintDialogOpen);
        Assert.NotEmpty(vm.AvailablePrinters);

        vm.SelectVirtualPreviewPrinterCommand.Execute(null);
        Assert.NotNull(vm.SelectedPrinter);
        Assert.True(vm.SelectedPrinter!.IsVirtualPreview);
        Assert.False(string.IsNullOrWhiteSpace(vm.PrintCalibrationSummaryText));

        await vm.ExecuteDirectPrintCommand.ExecuteAsync(null);
        Assert.True(vm.LastSpoolJobId > 0);
        Assert.True(vm.CanCancelLastPrintJob);

        vm.CancelLastPrintJobCommand.Execute(null);
        Assert.False(vm.CanCancelLastPrintJob);

        string exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZiwaStudio",
            "Exports");
        string pdfPath = Path.Combine(exportDir, "ziwa-export.pdf");
        string tiffPath = Path.Combine(exportDir, "ziwa-export-cmyk.tiff");

        try
        {
            await vm.ExportVectorPdfCommand.ExecuteAsync(null);
            Assert.True(File.Exists(pdfPath));
            Assert.True(new FileInfo(pdfPath).Length > 100);

            await vm.ExportCmykTiffCommand.ExecuteAsync(null);
            Assert.True(File.Exists(tiffPath));
            Assert.True(new FileInfo(tiffPath).Length > 100);
        }
        finally
        {
            if (File.Exists(pdfPath))
            {
                File.Delete(pdfPath);
            }

            if (File.Exists(tiffPath))
            {
                File.Delete(tiffPath);
            }
        }

        vm.ClosePrintDialogCommand.Execute(null);
        Assert.False(vm.IsPrintDialogOpen);

        window.Close();
    }

#if WINDOWS
    [Fact]
    public void WindowsGdiPrintSpooler_EnumeratesVirtualAndLocalPrintersAndDisposesSafeHandlesCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        IPrintSpooler spooler = sp.GetRequiredService<IPrintSpooler>();
        Assert.IsType<GdiPrintSpooler>(spooler);

        IReadOnlyList<PrinterDeviceInfo> printers = spooler.EnumeratePrinters();
        Assert.Contains(printers, p => p.IsVirtualPreview);

        PrinterDeviceInfo virtualPrinter = printers.First(p => p.IsVirtualPreview);
        HardwarePrintCapabilities caps = spooler.QueryCapabilities(virtualPrinter.PrinterName, PrintOrientationKind.Portrait);
        Assert.Equal(300, caps.DpiX);
        Assert.True(caps.PhysicalWidthPx > 0);

        using SafeGlobalMemoryHandle mem = SafeGlobalMemoryHandle.Allocate(64);
        Assert.False(mem.IsInvalid);

        using var invalidPrinter = new SafePrinterHandle(IntPtr.Zero, ownsHandle: false);
        Assert.True(invalidPrinter.IsInvalid);

        using var invalidDc = new SafeGdiDcHandle(IntPtr.Zero, ownsHandle: false);
        Assert.True(invalidDc.IsInvalid);
    }
#endif
}
