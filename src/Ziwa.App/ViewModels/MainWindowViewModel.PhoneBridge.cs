using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Bridge;
using Ziwa.Application.Commands;
using Ziwa.Domain.Bridge;
using Ziwa.Rendering.Qr;

namespace Ziwa.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private PhoneBridgeCoordinator? _phoneBridgeCoordinator;
    private SKBitmap? _phoneBridgeQrBitmap;
    private string _renderedQrUrl = string.Empty;

    [ObservableProperty]
    private bool _isPhoneBridgeDialogOpen;

    [ObservableProperty]
    private PhoneBridgeMode _selectedPhoneBridgeMode = PhoneBridgeMode.Cloud;

    [ObservableProperty]
    private string _phoneBridgeErrorText = string.Empty;

    public PhoneBridgeStatus PhoneBridgeStatus =>
        _phoneBridgeCoordinator?.Status ?? PhoneBridgeStatus.Stopped;

    public bool IsPhoneBridgeRunning => PhoneBridgeStatus.IsRunning;
    public bool IsPhoneBridgeStopped => !IsPhoneBridgeRunning;
    public bool IsPhoneBridgeBusy => _phoneBridgeCoordinator?.IsBusy == true;
    public bool IsPhoneBridgeIdle => !IsPhoneBridgeBusy;

    public bool IsPhoneBridgeCloudMode => SelectedPhoneBridgeMode == PhoneBridgeMode.Cloud;
    public bool IsPhoneBridgeLocalMode => SelectedPhoneBridgeMode == PhoneBridgeMode.LocalLan;
    public bool CanUsePhoneBridgeCloud => !string.IsNullOrWhiteSpace(PhoneBridgeStatus.CloudUrl);

    public string PhoneBridgeCloudUrl => PhoneBridgeStatus.CloudUrl;
    public string PhoneBridgeLocalUrl => PhoneBridgeStatus.LocalUrl;
    public string PhoneBridgeActiveUrl => IsPhoneBridgeCloudMode && CanUsePhoneBridgeCloud
        ? PhoneBridgeCloudUrl
        : PhoneBridgeLocalUrl;

    public string PhoneBridgePairedDevice => PhoneBridgeStatus.PairedDevice;
    public bool HasPhoneBridgePairedDevice => !string.IsNullOrWhiteSpace(PhoneBridgePairedDevice);
    public int PhoneBridgeReceivedCount => PhoneBridgeStatus.ReceivedCount;
    public bool HasPhoneBridgeReceivedPhotos => PhoneBridgeReceivedCount > 0;
    public string PhoneBridgeLastReceivedText => PhoneBridgeStatus.LastReceivedAtText;
    public bool HasPhoneBridgeError => !string.IsNullOrWhiteSpace(PhoneBridgeErrorText);

    public string PhoneBridgeStatusSummaryText => IsPhoneBridgeRunning
        ? (HasPhoneBridgePairedDevice
            ? $"متصل — {PhoneBridgePairedDevice} (تم استلام {PhoneBridgeReceivedCount})"
            : $"الخادم يعمل — بانتظار مسح رمز QR (تم استلام {PhoneBridgeReceivedCount})")
        : "الجسر متوقف حالياً";

    public SKBitmap? PhoneBridgeQrBitmap => _phoneBridgeQrBitmap;

    private void InitializePhoneBridgeIntegration(PhoneBridgeCoordinator? phoneBridgeCoordinator)
    {
        _phoneBridgeCoordinator = phoneBridgeCoordinator;
        if (_phoneBridgeCoordinator is not null)
        {
            _phoneBridgeCoordinator.StateChanged += OnPhoneBridgeCoordinatorStateChanged;
            _phoneBridgeCoordinator.DevicePaired += OnPhoneBridgeDevicePaired;
            _phoneBridgeCoordinator.PhotoReceived += OnPhoneBridgePhotoReceived;
            _phoneBridgeCoordinator.CloudErrorOccurred += OnPhoneBridgeCloudError;
        }

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("bridge.open"),
            "جسر كاميرا الهاتف (QR)…",
            "Ctrl+Shift+P",
            "استيراد",
            () => _phoneBridgeCoordinator is not null,
            OpenPhoneBridgeDialogCoreAsync));
    }

    partial void OnSelectedPhoneBridgeModeChanged(PhoneBridgeMode value)
    {
        OnPropertyChanged(nameof(IsPhoneBridgeCloudMode));
        OnPropertyChanged(nameof(IsPhoneBridgeLocalMode));
        OnPropertyChanged(nameof(PhoneBridgeActiveUrl));
        RefreshPhoneBridgeQrBitmap();
    }

    partial void OnPhoneBridgeErrorTextChanged(string value) =>
        OnPropertyChanged(nameof(HasPhoneBridgeError));

    [RelayCommand]
    private Task OpenPhoneBridgeDialogAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("bridge.open"), ct);

    [RelayCommand]
    private void ClosePhoneBridgeDialog()
    {
        // إغلاق نافذة QR لا يوقف الجسر حتى يواصل المصور الالتقاط نحو الكانفاس
        IsPhoneBridgeDialogOpen = false;
    }

    [RelayCommand]
    private void SelectPhoneBridgeCloudMode()
    {
        if (CanUsePhoneBridgeCloud)
        {
            SelectedPhoneBridgeMode = PhoneBridgeMode.Cloud;
        }
    }

    [RelayCommand]
    private void SelectPhoneBridgeLocalMode()
    {
        SelectedPhoneBridgeMode = PhoneBridgeMode.LocalLan;
    }

    [RelayCommand]
    private async Task StartPhoneBridgeAsync(CancellationToken ct)
    {
        if (_phoneBridgeCoordinator is null)
        {
            return;
        }

        PhoneBridgeErrorText = string.Empty;
        PhoneBridgeStartInfo? info = await _phoneBridgeCoordinator.StartAsync(ct);
        if (info is null)
        {
            if (!string.IsNullOrWhiteSpace(_phoneBridgeCoordinator.LastErrorMessage))
            {
                PhoneBridgeErrorText = _phoneBridgeCoordinator.LastErrorMessage;
                StatusMessage = PhoneBridgeErrorText;
            }

            return;
        }

        SelectedPhoneBridgeMode = string.IsNullOrWhiteSpace(info.CloudUrl)
            ? PhoneBridgeMode.LocalLan
            : PhoneBridgeMode.Cloud;

        if (!string.IsNullOrWhiteSpace(_phoneBridgeCoordinator.Status.CloudErrorMessage))
        {
            PhoneBridgeErrorText = _phoneBridgeCoordinator.Status.CloudErrorMessage;
        }

        RefreshPhoneBridgeBindings();
        StatusMessage = "تم تشغيل جسر كاميرا الهاتف — امسح رمز QR لبدء التصوير";
    }

    [RelayCommand]
    private async Task RestartPhoneBridgeAsync(CancellationToken ct)
    {
        if (_phoneBridgeCoordinator is null)
        {
            return;
        }

        PhoneBridgeErrorText = string.Empty;
        await _phoneBridgeCoordinator.RestartAsync(ct);
        if (!string.IsNullOrWhiteSpace(_phoneBridgeCoordinator.LastErrorMessage))
        {
            PhoneBridgeErrorText = _phoneBridgeCoordinator.LastErrorMessage;
            StatusMessage = PhoneBridgeErrorText;
            return;
        }

        if (IsPhoneBridgeCloudMode && !CanUsePhoneBridgeCloud)
        {
            SelectedPhoneBridgeMode = PhoneBridgeMode.LocalLan;
        }

        RefreshPhoneBridgeBindings();
        StatusMessage = "تمت إعادة تشغيل جسر كاميرا الهاتف";
    }

    [RelayCommand]
    private async Task StopPhoneBridgeAsync(CancellationToken ct)
    {
        if (_phoneBridgeCoordinator is null)
        {
            return;
        }

        await _phoneBridgeCoordinator.StopAsync(ct);
        PhoneBridgeErrorText = _phoneBridgeCoordinator.LastErrorMessage;
        RefreshPhoneBridgeBindings();
        StatusMessage = "تم إيقاف جسر كاميرا الهاتف";
    }

    [RelayCommand]
    private async Task CopyPhoneBridgeUrlAsync()
    {
        string url = PhoneBridgeActiveUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            TopLevel? top = (_dialogs is Services.AvaloniaProjectDialogs { Owner: { } ownerWin }
                ? TopLevel.GetTopLevel(ownerWin)
                : null)
                ?? (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
                    ? TopLevel.GetTopLevel(window)
                    : null);
            if (top?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(url);
                StatusMessage = "تم نسخ رابط جسر الهاتف إلى الحافظة";
                ShowToast("جسر الهاتف", StatusMessage, Avalonia.Controls.Notifications.NotificationType.Success);
                return;
            }
        }
        catch
        {
        }

        StatusMessage = "تعذر الوصول إلى الحافظة على هذه المنصة";
    }

    private async Task OpenPhoneBridgeDialogCoreAsync(CancellationToken ct)
    {
        IsPhoneBridgeDialogOpen = true;
        if (!IsPhoneBridgeRunning)
        {
            await StartPhoneBridgeAsync(ct);
        }
        else
        {
            RefreshPhoneBridgeBindings();
        }
    }

    private void OnPhoneBridgeCoordinatorStateChanged()
    {
        DispatchToUi(RefreshPhoneBridgeBindings);
    }

    private void OnPhoneBridgeDevicePaired(string deviceName)
    {
        DispatchToUi(() =>
        {
            StatusMessage = $"تم اقتران الهاتف بنجاح ({deviceName})";
            ShowToast("اقتران الهاتف", $"تم اقتران {deviceName} بنجاح", Avalonia.Controls.Notifications.NotificationType.Success);
            RefreshPhoneBridgeBindings();
        });
    }

    private void OnPhoneBridgePhotoReceived(PhoneBridgeReceivedPhoto photo)
    {
        DispatchToUi(() =>
        {
            PhoneBridgeInsertOutcome outcome = PhoneBridgeCanvasInserter.InsertReceivedPhoto(_session, photo);
            StatusMessage = outcome.StatusMessageAr;
            ShowToast("صورة جديدة من الهاتف", outcome.StatusMessageAr, Avalonia.Controls.Notifications.NotificationType.Success);
            RefreshPhoneBridgeBindings();
        });
    }

    private void OnPhoneBridgeCloudError(string message)
    {
        DispatchToUi(() =>
        {
            PhoneBridgeErrorText = message;
            if (!CanUsePhoneBridgeCloud && SelectedPhoneBridgeMode == PhoneBridgeMode.Cloud)
            {
                SelectedPhoneBridgeMode = PhoneBridgeMode.LocalLan;
            }

            RefreshPhoneBridgeBindings();
        });
    }

    private void RefreshPhoneBridgeBindings()
    {
        if (SelectedPhoneBridgeMode == PhoneBridgeMode.Cloud && !CanUsePhoneBridgeCloud && IsPhoneBridgeRunning)
        {
            SelectedPhoneBridgeMode = PhoneBridgeMode.LocalLan;
        }

        OnPropertyChanged(nameof(PhoneBridgeStatus));
        OnPropertyChanged(nameof(IsPhoneBridgeRunning));
        OnPropertyChanged(nameof(IsPhoneBridgeStopped));
        OnPropertyChanged(nameof(IsPhoneBridgeBusy));
        OnPropertyChanged(nameof(IsPhoneBridgeIdle));
        OnPropertyChanged(nameof(CanUsePhoneBridgeCloud));
        OnPropertyChanged(nameof(PhoneBridgeCloudUrl));
        OnPropertyChanged(nameof(PhoneBridgeLocalUrl));
        OnPropertyChanged(nameof(PhoneBridgeActiveUrl));
        OnPropertyChanged(nameof(PhoneBridgePairedDevice));
        OnPropertyChanged(nameof(HasPhoneBridgePairedDevice));
        OnPropertyChanged(nameof(PhoneBridgeReceivedCount));
        OnPropertyChanged(nameof(HasPhoneBridgeReceivedPhotos));
        OnPropertyChanged(nameof(PhoneBridgeLastReceivedText));
        OnPropertyChanged(nameof(PhoneBridgeStatusSummaryText));

        RefreshPhoneBridgeQrBitmap();
    }

    private void RefreshPhoneBridgeQrBitmap()
    {
        string url = PhoneBridgeActiveUrl;
        if (string.Equals(_renderedQrUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        SKBitmap? oldBitmap = _phoneBridgeQrBitmap;
        _renderedQrUrl = url;
        _phoneBridgeQrBitmap = string.IsNullOrWhiteSpace(url)
            ? null
            : QrCodeBitmapRenderer.RenderBitmap(url, pixelSize: 320);

        OnPropertyChanged(nameof(PhoneBridgeQrBitmap));
        DisposeBitmapAfterCompositorPass(oldBitmap);
    }

    private static void DispatchToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private async Task StopPhoneBridgeOnExitAsync()
    {
        if (_phoneBridgeCoordinator is not null && IsPhoneBridgeRunning)
        {
            await _phoneBridgeCoordinator.StopAsync(CancellationToken.None);
        }
    }

    private void DisposePhoneBridgeResources()
    {
        if (_phoneBridgeCoordinator is not null)
        {
            _phoneBridgeCoordinator.StateChanged -= OnPhoneBridgeCoordinatorStateChanged;
            _phoneBridgeCoordinator.DevicePaired -= OnPhoneBridgeDevicePaired;
            _phoneBridgeCoordinator.PhotoReceived -= OnPhoneBridgePhotoReceived;
            _phoneBridgeCoordinator.CloudErrorOccurred -= OnPhoneBridgeCloudError;
        }

        SKBitmap? oldBitmap = _phoneBridgeQrBitmap;
        _phoneBridgeQrBitmap = null;
        _renderedQrUrl = string.Empty;
        oldBitmap?.Dispose();
    }
}
