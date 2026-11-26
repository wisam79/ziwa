using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;

namespace Ziwa.Infrastructure.Persistence;

public sealed class CrashGuard : ICrashGuard, IDisposable, IAsyncDisposable
{
    private readonly string _root;
    private readonly IProjectStore _store;
    private readonly IAtomicFileWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RecoveryMetadata> _candidates = new(StringComparer.Ordinal);
    private string? _sessionDirectory;
    private FileStream? _lock;
    private bool _disposed;

    public CrashGuard(ProjectStorageOptions options, IProjectStore projectStore, IAtomicFileWriter atomicWriter)
    {
        _root = Path.Combine(PersistenceFiles.Root(options), "recovery");
        _store = projectStore;
        _writer = atomicWriter;
    }

    public async Task<IReadOnlyList<RecoveryEntry>> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessionDirectory is not null) { return Entries(); }
            PersistenceFiles.EnsureRegularDirectory(_root);
            foreach (string directory in Directory.EnumerateDirectories(_root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = Path.GetFileName(directory);
                if (!PersistenceFiles.IsSafeId(id) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { continue; }
                string lockPath = Path.Combine(directory, "session.lock");
                if (!File.Exists(lockPath)) { continue; }
                FileStream? candidateLock = TryLock(lockPath);
                if (candidateLock is null) { continue; }
                using (candidateLock)
                {
                    try
                    {
                        RecoveryMetadata metadata = await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
                        ProjectSnapshot recovered = await _store.LoadAsync(Path.Combine(directory, metadata.File), cancellationToken).ConfigureAwait(false);
                        if (metadata.ProjectPath is { } projectPath && File.Exists(projectPath) && File.GetLastWriteTimeUtc(projectPath) >= metadata.CapturedAtUtc.UtcDateTime)
                        {
                            try
                            {
                                ProjectSnapshot saved = await _store.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false);
                                if (saved.Document.Id == recovered.Document.Id) { continue; }
                            }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                            {
                                // A newer but damaged file must not hide a valid recovery snapshot.
                            }
                        }

                        _candidates[id] = metadata;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
                    {
                        // Invalid snapshots remain on disk for inspection; never silently delete user work.
                    }
                }
            }

            string ownDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            PersistenceFiles.EnsureRegularDirectory(ownDirectory);
            string ownLockPath = Path.Combine(ownDirectory, "session.lock");
            await _writer.WriteBytesAtomicallyAsync(ownLockPath, new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
            _lock = new FileStream(ownLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            _sessionDirectory = ownDirectory;
            return Entries();
        }
        finally { _gate.Release(); }
    }

    public async Task CaptureAsync(ProjectSnapshot snapshot, string? projectPath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessionDirectory is null) { throw new InvalidOperationException("Recovery session has not started."); }
            string file = $"snapshot-{Guid.NewGuid():N}.ziwa";
            string path = Path.Combine(_sessionDirectory, file);
            bool committed = false;
            try
            {
                await _store.SaveAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
                var metadata = new RecoveryMetadata(1, file, snapshot.Document.Title, DateTimeOffset.UtcNow,
                    projectPath is null ? null : Path.GetFullPath(projectPath));
                await PersistenceFiles.WriteJsonAsync(_writer, Path.Combine(_sessionDirectory, "manifest.json"), metadata, cancellationToken).ConfigureAwait(false);
                committed = true;
                foreach (string old in Directory.EnumerateFiles(_sessionDirectory, "snapshot-*.ziwa"))
                {
                    if (old != path) { DeleteFileBestEffort(old); }
                }
            }
            finally
            {
                if (!committed) { DeleteFileBestEffort(path); }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<ProjectSnapshot> LoadRecoveryAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string directory = CandidateDirectory(id);
            using FileStream candidateLock = TryLock(Path.Combine(directory, "session.lock"))
                ?? throw new IOException("Recovery session is still active.");
            RecoveryMetadata metadata = await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
            return await _store.LoadAsync(Path.Combine(directory, metadata.File), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DiscardRecoveryAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string directory = CandidateDirectory(id);
            using (FileStream candidateLock = TryLock(Path.Combine(directory, "session.lock"))
                ?? throw new IOException("Recovery session is still active."))
            {
                // Clearing the manifest under the lock prevents another process discovering a deleted snapshot.
                File.Delete(Path.Combine(directory, "manifest.json"));
            }

            Directory.Delete(directory, recursive: true);
            _candidates.Remove(id);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearCurrentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearCurrentCore();
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ClearCurrentCore();
            _lock?.Dispose();
            _lock = null;
            if (_sessionDirectory is { } directory)
            {
                Directory.Delete(directory, recursive: true);
                _sessionDirectory = null;
            }
        }
        finally { _gate.Release(); }
    }

    private void ClearCurrentCore()
    {
        if (_sessionDirectory is null) { return; }
        File.Delete(Path.Combine(_sessionDirectory, "manifest.json"));
        foreach (string file in Directory.EnumerateFiles(_sessionDirectory, "snapshot-*.ziwa")) { File.Delete(file); }
    }

    private string CandidateDirectory(string id)
    {
        if (!PersistenceFiles.IsSafeId(id) || !_candidates.ContainsKey(id)) { throw new InvalidDataException("Unknown recovery snapshot."); }
        string directory = Path.Combine(_root, id);
        PersistenceFiles.EnsureRegularDirectory(directory);
        return directory;
    }

    private IReadOnlyList<RecoveryEntry> Entries() => _candidates
        .Select(pair => new RecoveryEntry(pair.Key, pair.Value.Title, pair.Value.CapturedAtUtc, pair.Value.ProjectPath))
        .OrderByDescending(entry => entry.CapturedAtUtc).ToArray();

    private static async Task<RecoveryMetadata> ReadMetadataAsync(string directory, CancellationToken cancellationToken)
    {
        RecoveryMetadata metadata = await PersistenceFiles.ReadJsonAsync<RecoveryMetadata>(Path.Combine(directory, "manifest.json"), cancellationToken).ConfigureAwait(false);
        if (metadata.Version != 1 || metadata.File is null || metadata.File.Length != 46 ||
            !metadata.File.StartsWith("snapshot-", StringComparison.Ordinal) || !metadata.File.EndsWith(".ziwa", StringComparison.Ordinal) ||
            !PersistenceFiles.IsSafeId(metadata.File.Substring(9, 32)) || string.IsNullOrWhiteSpace(metadata.Title))
        {
            throw new InvalidDataException("Invalid recovery metadata.");
        }

        PersistenceFiles.EnsureRegularFile(Path.Combine(directory, metadata.File));
        return metadata;
    }

    private static FileStream? TryLock(string path)
    {
        try
        {
            PersistenceFiles.EnsureRegularFile(path);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _lock?.Dispose();
            _lock = null;
            // Abnormal disposal deliberately retains recovery data. Only CompleteAsync marks a clean exit.
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record RecoveryMetadata(int Version, string File, string Title, DateTimeOffset CapturedAtUtc, string? ProjectPath);
}
