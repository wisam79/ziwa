using Ziwa.Domain.Platform;

namespace Ziwa.Application.Abstractions;

public interface IStructuredAppLogger
{
    string LogDirectory { get; }

    void LogInfo(string category, string message);
    void LogWarning(string category, string message);
    void LogError(string category, string message, Exception? exception = null);

    Task<string> ExportSanitizedSupportLogsAsync(
        string destinationZipPath,
        CancellationToken cancellationToken = default);
}

public interface IAppUpdaterService
{
    Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    Task<AppUpdateDownloadResult> DownloadAndVerifyUpdateAsync(
        AppUpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> LaunchVerifiedInstallerAsync(
        AppUpdateDownloadResult verifiedUpdate,
        CancellationToken cancellationToken = default);
}

public interface ISingleInstanceCoordinator : IDisposable
{
    event Action<string?>? SecondaryInstanceActivated;

    bool TryAcquireOrForward(string[] args, out bool forwardedToPrimary);
    void StartListening();
}

public interface IFileAssociationService
{
    bool IsZiwaExtensionRegistered();
    void EnsureZiwaExtensionRegistered();
}

public interface IDropFileFilter
{
    IReadOnlyList<string> FilterSupportedImageFiles(IEnumerable<string> candidatePaths);
    bool IsSupportedImageFile(string path);
}
