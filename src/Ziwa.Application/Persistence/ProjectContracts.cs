using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Persistence;

public enum ProjectAssetKind
{
    Raster,
    Svg,
}

public sealed record ProjectAsset(AssetId Id, ProjectAssetKind Kind, byte[] Data);

public sealed record ProjectSnapshot(ZiwaDocument Document, ImmutableArray<ProjectAsset> Assets);

public interface IProjectAssetStore
{
    ProjectSnapshot Capture(ZiwaDocument document);
    void Restore(ProjectSnapshot snapshot);
}

public interface IProjectStore
{
    Task SaveAsync(string path, ProjectSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<ProjectSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default);
}

public interface IDocumentMigrator
{
    int FromVersion { get; }
    int ToVersion { get; }
    JsonObject Migrate(JsonObject document);
}

public sealed record ProjectLibraryEntry(
    string Path,
    DocumentId DocumentId,
    string Title,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset OpenedAtUtc);

public interface IProjectLibrary
{
    Task<IReadOnlyList<ProjectLibraryEntry>> ListAsync(CancellationToken cancellationToken = default);
    Task RecordAsync(ProjectLibraryEntry entry, CancellationToken cancellationToken = default);
    Task ExportBackupAsync(string path, CancellationToken cancellationToken = default);
    Task<int> ImportBackupAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record RecoveryEntry(string Id, string Title, DateTimeOffset CapturedAtUtc, string? ProjectPath);

public interface ICrashGuard
{
    Task<IReadOnlyList<RecoveryEntry>> StartAsync(CancellationToken cancellationToken = default);
    Task CaptureAsync(ProjectSnapshot snapshot, string? projectPath, CancellationToken cancellationToken = default);
    Task<ProjectSnapshot> LoadRecoveryAsync(string id, CancellationToken cancellationToken = default);
    Task DiscardRecoveryAsync(string id, CancellationToken cancellationToken = default);
    Task ClearCurrentAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

public sealed record ProjectStorageOptions(string RootDirectory);

public enum UnsavedChangesChoice
{
    Cancel,
    Save,
    Discard,
}

public interface IProjectDialogs
{
    Task<string?> PickOpenProjectAsync(CancellationToken cancellationToken = default);
    Task<string?> PickSaveProjectAsync(string title, string? currentPath, CancellationToken cancellationToken = default);
    Task<string?> PickOpenBackupAsync(CancellationToken cancellationToken = default);
    Task<string?> PickSaveBackupAsync(CancellationToken cancellationToken = default);
    Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default);
}
