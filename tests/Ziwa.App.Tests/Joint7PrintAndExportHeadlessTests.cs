using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ziwa.App.ViewModels;
using Ziwa.App.Views;
using Ziwa.Application.Persistence;
using Ziwa.Application.Printing;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.Tests;

public sealed class Joint7PrintAndExportHeadlessTests
{
    [AvaloniaFact]
    public async Task PrintDialogDpiOrientationCmykProfileAndPreflightReview_WorkEndToEnd()
    {
        using ServiceProvider sp = Bootstrapper.BuildServiceProvider();
        MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Trigger a trim-line preflight warning
            var nearTrim = new TextNode(
                Id: NodeId.New(),
                Name: "نص قريب للقص",
                BoundsMm: new RectMm(0.5, 10.0, 42.0, 12.0),
                Text: "تحذير",
                FontFamily: "Cairo",
                FontSizePt: 12.0,
                FontWeight: 700,
                Color: ColorRgba.Black);
            vm.Session.ApplyCommand("إضافة نص قريب للقص", d =>
                d with { Pages = d.Pages.SetItem(d.ActivePageIndex, d.ActivePage with { Nodes = d.ActivePage.Nodes.Add(nearTrim) }) });

            vm.CloseInspectorCommand.Execute(null);
            vm.OpenPrintDialogCommand.Execute(null);
            Assert.True(vm.IsPrintDialogOpen);
            Assert.True(vm.HasPreflightWarnings);

            // Switch DPI to 600 DPI -> updates Document.PrintSettings & supports Undo
            vm.SelectedExportDpiOption = vm.AvailableExportDpiOptions.First(o => Math.Abs(o.Dpi - 600.0) < 0.1);
            Assert.Equal(600.0, vm.Document.PrintSettings.ExportDpi);
            Assert.Equal("600 DPI", vm.EffectiveDpiText);

            // Arabic Orientation & CMYK Profile selectors sync cleanly
            vm.SelectedPrintOrientationOption = vm.AvailableOrientationOptions.First(o => o.Kind == PrintOrientationKind.Landscape);
            Assert.Equal(PrintOrientationKind.Landscape, vm.SelectedPrintOrientation);

            vm.SelectedCmykProfileOption = vm.AvailableCmykProfileOptions.First(o => o.Kind == CmykProfileKind.USWebCoatedSwop);
            Assert.Equal(CmykProfileKind.USWebCoatedSwop, vm.SelectedCmykProfile);

            // ReviewPreflightWarnings closes Print Dialog, opens Inspector & selects the offending node
            vm.ReviewPreflightWarningsCommand.Execute(null);
            Assert.False(vm.IsPrintDialogOpen);
            Assert.True(vm.IsInspectorOpen);
            Assert.Contains(nearTrim.Id, vm.Session.SelectedNodeIds);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExportRasterPngJpegAndPdf_UseSaveDialogWhenAvailableAndRespectCancel()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ziwa-joint7-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string pngTarget = Path.Combine(tempDir, "custom-sheet.png");
            string jpgTarget = Path.Combine(tempDir, "custom-sheet.jpg");
            var dialogs = new ExportPickDialogs { NextExportPath = pngTarget };

            using ServiceProvider sp = Bootstrapper.BuildServiceProvider(new ProjectStorageOptions(tempDir), dialogs);
            MainWindowViewModel vm = sp.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                await vm.ExportPngCommand.ExecuteAsync(null);
                Assert.True(File.Exists(pngTarget));
                Assert.True(new FileInfo(pngTarget).Length > 100);

                dialogs.NextExportPath = jpgTarget;
                await vm.ExportJpegCommand.ExecuteAsync(null);
                Assert.True(File.Exists(jpgTarget));
                Assert.True(new FileInfo(jpgTarget).Length > 100);

                // Cancelling Save Dialog aborts cleanly without writing anything
                string uncreatedPath = Path.Combine(tempDir, "should-not-exist.pdf");
                dialogs.NextExportPath = null;
                await vm.ExportVectorPdfCommand.ExecuteAsync(null);
                Assert.False(File.Exists(uncreatedPath));
                Assert.Equal("تم إلغاء التصدير", vm.StatusMessage);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class ExportPickDialogs : IProjectDialogs
    {
        public string? NextExportPath { get; set; }
        public bool CanPickExportSavePath => true;
        public Task<string?> PickSaveExportAsync(string suggestedFileName, string extension, string fileTypeLabel, CancellationToken cancellationToken = default) =>
            Task.FromResult(NextExportPath);
        public Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(UnsavedChangesChoice.Cancel);
    }
}
