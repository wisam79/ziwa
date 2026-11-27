using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Commands;
using Ziwa.Application.Printing;
using Ziwa.Domain.PrePress;

namespace Ziwa.App.ViewModels;

public sealed record ExportDpiOption(double Dpi, string TitleAr);

public sealed record PrintOrientationOption(PrintOrientationKind Kind, string TitleAr);

public sealed record CmykProfileOption(CmykProfileKind Kind, string TitleAr);

public sealed partial class MainWindowViewModel
{
    private bool _isSyncingPrintOptions;

    public IReadOnlyList<ExportDpiOption> AvailableExportDpiOptions { get; } =
    [
        new(150.0, "150 DPI (مسودة سريعة)"),
        new(300.0, "300 DPI (طباعة استوديو قياسية)"),
        new(600.0, "600 DPI (دقة فائقة / أختام)"),
    ];

    public IReadOnlyList<PrintOrientationOption> AvailableOrientationOptions { get; } =
    [
        new(PrintOrientationKind.Portrait, "عمودي (Portrait)"),
        new(PrintOrientationKind.Landscape, "أفقي (Landscape)"),
    ];

    public IReadOnlyList<CmykProfileOption> AvailableCmykProfileOptions { get; } =
    [
        new(CmykProfileKind.CoatedFogra39, "Coated FOGRA39 (أوروبا/عام)"),
        new(CmykProfileKind.USWebCoatedSwop, "US Web Coated SWOP v2"),
        new(CmykProfileKind.CustomIcc, "ملف ICC مخصص"),
    ];

    [ObservableProperty]
    private ExportDpiOption? _selectedExportDpiOption;

    [ObservableProperty]
    private PrintOrientationOption? _selectedPrintOrientationOption;

    [ObservableProperty]
    private CmykProfileOption? _selectedCmykProfileOption;

    private void RegisterRasterExportCommands()
    {
        _commandRegistry.Register(new CommandDefinition(
            new CommandId("file.exportPng"),
            "تصدير PNG عالي الدقة",
            string.Empty,
            "ملف",
            () => true,
            ct => ExportRasterCoreAsync(asJpeg: false, ct)));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("file.exportJpeg"),
            "تصدير JPEG عالي الدقة",
            string.Empty,
            "ملف",
            () => true,
            ct => ExportRasterCoreAsync(asJpeg: true, ct)));
    }

    private void SyncPrintExportOptions()
    {
        _isSyncingPrintOptions = true;
        try
        {
            double docDpi = Document.PrintSettings.ExportDpi;
            SelectedExportDpiOption = AvailableExportDpiOptions
                .OrderBy(o => Math.Abs(o.Dpi - docDpi))
                .FirstOrDefault();

            SelectedPrintOrientationOption = AvailableOrientationOptions.FirstOrDefault(o => o.Kind == SelectedPrintOrientation)
                ?? AvailableOrientationOptions[0];

            SelectedCmykProfileOption = AvailableCmykProfileOptions.FirstOrDefault(o => o.Kind == SelectedCmykProfile)
                ?? AvailableCmykProfileOptions[0];
        }
        finally
        {
            _isSyncingPrintOptions = false;
        }

        OnPropertyChanged(nameof(EffectiveDpiText));
    }

    partial void OnSelectedExportDpiOptionChanged(ExportDpiOption? value)
    {
        if (_isSyncingPrintOptions || value is null)
        {
            return;
        }

        if (Math.Abs(Document.PrintSettings.ExportDpi - value.Dpi) > 0.5)
        {
            _session.ApplyCommand("تغيير دقة التصدير والطباعة", doc => doc with
            {
                PrintSettings = doc.PrintSettings with { ExportDpi = value.Dpi },
            });
            StatusMessage = $"تم ضبط دقة التصدير على {value.Dpi:0} DPI";
        }
    }

    partial void OnSelectedPrintOrientationOptionChanged(PrintOrientationOption? value)
    {
        if (!_isSyncingPrintOptions && value is not null && SelectedPrintOrientation != value.Kind)
        {
            SelectedPrintOrientation = value.Kind;
        }
    }

    partial void OnSelectedCmykProfileOptionChanged(CmykProfileOption? value)
    {
        if (!_isSyncingPrintOptions && value is not null)
        {
            SelectedCmykProfile = value.Kind;
        }
    }

    [RelayCommand]
    private void ReviewPreflightWarnings()
    {
        IsPrintDialogOpen = false;
        OpenInspector();
        if (PreflightIssues.Count > 0)
        {
            SelectPreflightIssue(PreflightIssues[0]);
        }
    }

    [RelayCommand]
    private Task ExportPngAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("file.exportPng"), ct);

    [RelayCommand]
    private Task ExportJpegAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("file.exportJpeg"), ct);

    private async Task<string?> ResolveExportDestinationAsync(
        string defaultFileName,
        string suggestedBaseName,
        string extension,
        string fileTypeLabel,
        CancellationToken ct)
    {
        if (_dialogs is { CanPickExportSavePath: true })
        {
            string safeBase = SanitizeExportFileName(suggestedBaseName);
            string? picked = await _dialogs.PickSaveExportAsync(
                $"{safeBase}.{extension}",
                extension,
                fileTypeLabel,
                ct);
            if (string.IsNullOrWhiteSpace(picked))
            {
                StatusMessage = "تم إلغاء التصدير";
                return null;
            }

            return picked;
        }

        string exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZiwaStudio",
            "Exports");
        return Path.Combine(exportDir, defaultFileName);
    }

    private static string SanitizeExportFileName(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "ziwa-design";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(title.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "ziwa-design" : cleaned;
    }

    private async Task ExportRasterCoreAsync(bool asJpeg, CancellationToken ct)
    {
        string ext = asJpeg ? "jpg" : "png";
        string label = asJpeg ? "صورة JPEG" : "صورة PNG";
        string? outputPath = await ResolveExportDestinationAsync(
            $"ziwa-export.{ext}",
            Document.Title,
            ext,
            label,
            ct);
        if (outputPath is null)
        {
            return;
        }

        ExportPageRangeKind range = PrintAllPages ? ExportPageRangeKind.AllPages : ExportPageRangeKind.ActivePageOnly;
        IReadOnlyList<ExportArtifactResult> results = await _exportService.ExportPagesRasterAsync(
            outputPath,
            Document.PrintSettings.ExportDpi,
            asJpeg: asJpeg,
            pageRange: range,
            includeBleed: PrintIncludeBleed,
            includeCropMarks: PrintIncludeCropMarks,
            includeRegistrationMarks: PrintIncludeRegistrationMarks,
            includeCutContour: PrintIncludeCutContour,
            cancellationToken: ct);

        if (results.Count > 0)
        {
            ExportArtifactResult first = results[0];
            StatusMessage = results.Count == 1
                ? $"تم تصدير {ext.ToUpperInvariant()} ذرياً ({first.PixelWidth}×{first.PixelHeight} px @ {first.Dpi:0} DPI)"
                : $"تم تصدير {results.Count} صفحات بصيغة {ext.ToUpperInvariant()} @ {first.Dpi:0} DPI";
            ShowToast("التصدير النقطي", StatusMessage, NotificationType.Success);
        }
    }
}
