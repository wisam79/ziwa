using Ziwa.Application.Abstractions;
using Ziwa.Domain.Bridge;

namespace Ziwa.Application.Bridge;

public sealed class PhoneBridgeCoordinator : IDisposable
{
    private readonly IPhoneBridgeService _bridgeService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public PhoneBridgeCoordinator(IPhoneBridgeService bridgeService)
    {
        _bridgeService = bridgeService;
        _bridgeService.StatusChanged += OnServiceStatusChanged;
        _bridgeService.DevicePaired += OnServiceDevicePaired;
        _bridgeService.PhotoReceived += OnServicePhotoReceived;
        _bridgeService.CloudErrorOccurred += OnServiceCloudError;
    }

    public event Action? StateChanged;
    public event Action<string>? DevicePaired;
    public event Action<PhoneBridgeReceivedPhoto>? PhotoReceived;
    public event Action<string>? CloudErrorOccurred;

    public PhoneBridgeStatus Status => _bridgeService.CurrentStatus;
    public bool IsBusy { get; private set; }
    public string LastErrorMessage { get; private set; } = string.Empty;

    public async Task<PhoneBridgeStartInfo?> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        IsBusy = true;
        LastErrorMessage = string.Empty;
        StateChanged?.Invoke();

        try
        {
            return await _bridgeService.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LastErrorMessage = "تم إلغاء تشغيل جسر الهاتف";
            return null;
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"تعذر تشغيل جسر الهاتف: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        IsBusy = true;
        LastErrorMessage = string.Empty;
        StateChanged?.Invoke();

        try
        {
            await _bridgeService.StopAsync(cancellationToken).ConfigureAwait(false);
            await _bridgeService.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LastErrorMessage = "تم إلغاء إعادة تشغيل جسر الهاتف";
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"تعذر إعادة تشغيل جسر الهاتف: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        LastErrorMessage = string.Empty;
        StateChanged?.Invoke();

        try
        {
            await _bridgeService.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastErrorMessage = $"تعذر إيقاف جسر الهاتف: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
            _gate.Release();
        }
    }

    private void OnServiceStatusChanged(PhoneBridgeStatus _) => StateChanged?.Invoke();
    private void OnServiceDevicePaired(string deviceName) => DevicePaired?.Invoke(deviceName);
    private void OnServicePhotoReceived(PhoneBridgeReceivedPhoto photo) => PhotoReceived?.Invoke(photo);
    private void OnServiceCloudError(string message) => CloudErrorOccurred?.Invoke(message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bridgeService.StatusChanged -= OnServiceStatusChanged;
        _bridgeService.DevicePaired -= OnServiceDevicePaired;
        _bridgeService.PhotoReceived -= OnServicePhotoReceived;
        _bridgeService.CloudErrorOccurred -= OnServiceCloudError;
        _gate.Dispose();
    }
}
