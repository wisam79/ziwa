using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Commands;
using Ziwa.Domain.Platform;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private IStructuredAppLogger? _appLogger;
    private IAppUpdaterService? _appUpdater;
    private IDropFileFilter? _dropFileFilter;
    private AppUpdateDownloadResult? _verifiedDownloadResult;
    private CancellationTokenSource? _updateOperationCts;

    [ObservableProperty]
    private bool _isUpdateDialogOpen;

    [ObservableProperty]
    private bool _isUpdateBusy;

    [ObservableProperty]
    private int _updateDownloadPercent;

    [ObservableProperty]
    private AppUpdateInfo? _availableUpdateInfo;

    [ObservableProperty]
    private string _updateStatusMessage = "اضغط «التحقق من التحديثات» لفحص أحدث إصدار متاح.";

    public bool IsUpdateIdle => !IsUpdateBusy;
    public bool HasAvailableUpdate => AvailableUpdateInfo is { HasUpdate: true };
    public bool CanDownloadAvailableUpdate => HasAvailableUpdate && IsUpdateIdle;
    public bool HasVerifiedUpdateReadyToInstall => _verifiedDownloadResult is not null && IsUpdateIdle;
    public string UpdateLatestVersionText => AvailableUpdateInfo?.LatestVersion ?? "—";
    public string UpdateCurrentVersionText => AvailableUpdateInfo?.CurrentVersion ?? "1.0.0";
    public string UpdateReleaseNotesText => AvailableUpdateInfo?.ReleaseNotes ?? string.Empty;
    public bool HasUpdateReleaseNotes => !string.IsNullOrWhiteSpace(UpdateReleaseNotesText);
    public string UpdateChecksumPreviewText => AvailableUpdateInfo is { ExpectedSha256.Length: > 0 } info
        ? info.ExpectedSha256
        : "غير منشورة (سيتم الرفض تلقائياً - Fail-Closed)";

    private void InitializeDiagnosticsAndUpdateIntegration(
        IStructuredAppLogger? appLogger,
        IAppUpdaterService? appUpdater,
        IDropFileFilter? dropFileFilter)
    {
        _appLogger = appLogger;
        _appUpdater = appUpdater;
        _dropFileFilter = dropFileFilter;

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("support.exportLogs"),
            "تصدير سجلات الدعم الفني…",
            string.Empty,
            "دعم",
            () => _appLogger is not null && _dialogs is not null,
            ExportSupportLogsCoreAsync));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("app.checkUpdates"),
            "التحقق من وجود تحديثات…",
            string.Empty,
            "تطبيق",
            () => _appUpdater is not null,
            OpenAndCheckUpdatesCoreAsync));

        _appLogger?.LogInfo("Startup", "MainWindowViewModel initialized");
    }

    partial void OnIsUpdateBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUpdateIdle));
        OnPropertyChanged(nameof(CanDownloadAvailableUpdate));
        OnPropertyChanged(nameof(HasVerifiedUpdateReadyToInstall));
    }

    partial void OnAvailableUpdateInfoChanged(AppUpdateInfo? value)
    {
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(CanDownloadAvailableUpdate));
        OnPropertyChanged(nameof(UpdateLatestVersionText));
        OnPropertyChanged(nameof(UpdateCurrentVersionText));
        OnPropertyChanged(nameof(UpdateReleaseNotesText));
        OnPropertyChanged(nameof(HasUpdateReleaseNotes));
        OnPropertyChanged(nameof(UpdateChecksumPreviewText));
    }

    [RelayCommand]
    private Task ExportSupportLogsAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("support.exportLogs"), ct);

    [RelayCommand]
    private Task OpenUpdateDialogAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("app.checkUpdates"), ct);

    [RelayCommand]
    private void CloseUpdateDialog()
    {
        if (IsUpdateBusy)
        {
            return;
        }

        IsUpdateDialogOpen = false;
    }

    [RelayCommand]
    private void CancelUpdateOperation()
    {
        _updateOperationCts?.Cancel();
        UpdateStatusMessage = "جارٍ إلغاء عملية التحديث...";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        if (_appUpdater is null || IsUpdateBusy)
        {
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _updateOperationCts = linkedCts;
        IsUpdateBusy = true;
        _verifiedDownloadResult = null;
        UpdateDownloadPercent = 0;
        UpdateStatusMessage = "جارٍ التحقق من وجود تحديثات...";

        try
        {
            AppUpdateInfo info = await _appUpdater.CheckForUpdateAsync(linkedCts.Token);
            AvailableUpdateInfo = info;
            UpdateStatusMessage = info.HasUpdate
                ? $"يتوفر إصدار جديد: {info.LatestVersion}"
                : $"أنت تستخدم أحدث إصدار ({info.CurrentVersion}).";
            StatusMessage = UpdateStatusMessage;
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "تم إلغاء التحقق من التحديثات";
        }
        catch (Exception ex)
        {
            _appLogger?.LogError("Updater", "Update check failed", ex);
            UpdateStatusMessage = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            _updateOperationCts = null;
            IsUpdateBusy = false;
            OnPropertyChanged(nameof(HasVerifiedUpdateReadyToInstall));
        }
    }

    [RelayCommand]
    private async Task DownloadAndVerifyUpdateAsync(CancellationToken ct)
    {
        if (_appUpdater is null || AvailableUpdateInfo is null || IsUpdateBusy)
        {
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _updateOperationCts = linkedCts;
        IsUpdateBusy = true;
        _verifiedDownloadResult = null;
        UpdateDownloadPercent = 0;
        UpdateStatusMessage = "جارٍ تنزيل التحديث والتحقق من بصمة SHA-256...";

        try
        {
            var progress = new Progress<int>(pct => UpdateDownloadPercent = pct);
            AppUpdateDownloadResult verified = await _appUpdater.DownloadAndVerifyUpdateAsync(
                AvailableUpdateInfo,
                progress,
                linkedCts.Token);
            _verifiedDownloadResult = verified;
            UpdateDownloadPercent = 100;
            UpdateStatusMessage = $"تم التحقق من سلامة حزمة التحديث ({verified.ActualSha256[..12]}…). جاهز للتثبيت.";
            StatusMessage = UpdateStatusMessage;
            ShowToast("التحديثات", UpdateStatusMessage, global::Avalonia.Controls.Notifications.NotificationType.Success);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "تم إلغاء تنزيل التحديث";
        }
        catch (Exception ex)
        {
            _appLogger?.LogError("Updater", "Update download/verification failed", ex);
            UpdateStatusMessage = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            _updateOperationCts = null;
            IsUpdateBusy = false;
            OnPropertyChanged(nameof(HasVerifiedUpdateReadyToInstall));
        }
    }

    [RelayCommand]
    private async Task LaunchVerifiedUpdateAsync(CancellationToken ct)
    {
        if (_appUpdater is null || _verifiedDownloadResult is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        try
        {
            bool launched = await _appUpdater.LaunchVerifiedInstallerAsync(_verifiedDownloadResult, ct);
            UpdateStatusMessage = launched
                ? "تم تشغيل مثبّت التحديث المُتحقَّق منه بنجاح"
                : "تعذر تشغيل مثبّت التحديث";
            StatusMessage = UpdateStatusMessage;
        }
        catch (Exception ex)
        {
            _appLogger?.LogError("Updater", "Failed launching verified installer", ex);
            UpdateStatusMessage = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    public async Task OpenExternalProjectFileAsync(string? filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !filePath.EndsWith(".ziwa", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(filePath))
        {
            return;
        }

        _appLogger?.LogInfo("Startup", $"Opening external project path: {Path.GetFileName(filePath)}");
        await _projects.OpenAsync(filePath, ct);
    }

    public async Task HandleDroppedPathsAsync(IEnumerable<string> rawPaths, CancellationToken ct = default)
    {
        string[] paths = rawPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        if (paths.Length == 1 && paths[0].EndsWith(".ziwa", StringComparison.OrdinalIgnoreCase))
        {
            await OpenExternalProjectFileAsync(paths[0], ct);
            return;
        }

        IReadOnlyList<string> imagePaths = _dropFileFilter is not null
            ? _dropFileFilter.FilterSupportedImageFiles(paths)
            : [];

        if (imagePaths.Count == 0)
        {
            StatusMessage = "تم تجاهل الملفات المسحوبة: يُقبل سحب الصور الصالحة (PNG / JPEG / WebP / BMP) أو مشروع .ziwa فقط";
            _appLogger?.LogWarning("DragDrop", "Rejected dropped non-image files");
            return;
        }

        if (_imageImporter is null)
        {
            StatusMessage = "خدمة استيراد الصور غير مهيأة";
            return;
        }

        int importedCount = 0;
        foreach (string imagePath in imagePaths)
        {
            try
            {
                AssetId importedId = await _imageImporter.ImportImageFileAsync(imagePath, ct);
                AssetPixelDimensions? dims = _assetRegistrar?.TryGetAssetDimensions(importedId);
                int w = dims?.WidthPx ?? 0;
                int h = dims?.HeightPx ?? 0;
                Ziwa.Application.Bridge.PhoneBridgeCanvasInserter.InsertReceivedPhoto(
                    _session,
                    new PhoneBridgeReceivedPhoto(
                        importedId,
                        w,
                        h,
                        Domain.Bridge.PhoneBridgePhotoSource.LocalLan,
                        DateTimeOffset.UtcNow,
                        Path.GetFileNameWithoutExtension(imagePath)));
                importedCount++;
            }
            catch (Exception ex)
            {
                _appLogger?.LogWarning("DragDrop", $"Skipped invalid dropped image: {ex.Message}");
            }
        }

        StatusMessage = importedCount > 0
            ? $"تم إدراج {importedCount} صورة مسحوبة في مساحة العمل"
            : "تعذر استيراد الصور المسحوبة";
    }

    private async Task ExportSupportLogsCoreAsync(CancellationToken ct)
    {
        if (_appLogger is null || _dialogs is null)
        {
            StatusMessage = "خدمة سجلات الدعم غير مهيأة";
            return;
        }

        string? destZipPath = await _dialogs.PickSaveSupportLogsAsync(ct);
        if (string.IsNullOrWhiteSpace(destZipPath))
        {
            return;
        }

        try
        {
            string exportedPath = await _appLogger.ExportSanitizedSupportLogsAsync(destZipPath, ct);
            StatusMessage = $"تم تصدير سجلات الدعم المنقّاة بنجاح إلى {Path.GetFileName(exportedPath)}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "تم إلغاء تصدير سجلات الدعم الفني";
        }
        catch (Exception ex)
        {
            _appLogger.LogError("Diagnostics", "Failed exporting support logs", ex);
            StatusMessage = $"تعذر تصدير سجلات الدعم الفني: {ex.Message}";
        }
    }

    private async Task OpenAndCheckUpdatesCoreAsync(CancellationToken ct)
    {
        IsUpdateDialogOpen = true;
        await CheckForUpdatesAsync(ct);
    }
}
