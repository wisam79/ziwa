using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Primitives;

namespace Ziwa.Infrastructure.Persistence;

public sealed class SqliteProjectRepo : IProjectLibrary, IDisposable, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _root;
    private readonly string _databasePath;
    private readonly IProjectStore _projectStore;
    private readonly IAtomicFileWriter _atomicWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteProjectRepo(ProjectStorageOptions options, IProjectStore projectStore, IAtomicFileWriter atomicWriter)
    {
        _root = PersistenceFiles.Root(options);
        _databasePath = Path.Combine(_root, "projects.db");
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _atomicWriter = atomicWriter ?? throw new ArgumentNullException(nameof(atomicWriter));
    }

    public async Task<IReadOnlyList<ProjectLibraryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ListCoreAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(ProjectLibraryEntry entry, CancellationToken cancellationToken = default)
    {
        entry = ValidateEntry(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await RecordCoreAsync(connection, null, entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportBackupAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string destination = Path.GetFullPath(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ProjectLibraryEntry> projects = await ListCoreAsync(connection, cancellationToken).ConfigureAwait(false);
            if (projects.Count > PersistenceFiles.MaxBackupProjects)
            {
                throw new InvalidDataException("The library exceeds the backup project limit.");
            }

            if (destination.Equals(_databasePath, PersistenceFiles.PathComparison)
                || destination.Equals(_databasePath + "-wal", PersistenceFiles.PathComparison)
                || destination.Equals(_databasePath + "-shm", PersistenceFiles.PathComparison)
                || projects.Any(entry => destination.Equals(entry.Path, PersistenceFiles.PathComparison)))
            {
                throw new InvalidDataException("A backup cannot replace a project or the library database.");
            }

            staging = Path.Combine(_root, ".backup-" + Guid.NewGuid().ToString("N"));
            PersistenceFiles.EnsureRegularDirectory(staging);
            await _atomicWriter.WriteAtomicallyAsync(destination, async (output, token) =>
            {
                var manifestProjects = new List<BackupProject>();
                long totalBytes = 0;
                // The archive carries a logical metadata snapshot and the actual bundles, not a live
                // SQLite database file. Copying projects.db would lose uncheckpointed WAL transactions.
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (int index = 0; index < projects.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ProjectLibraryEntry entry = projects[index];
                        byte[] bytes = await PersistenceFiles.ReadBoundedAsync(entry.Path,
                            PersistenceFiles.MaxProjectBytes, cancellationToken).ConfigureAwait(false);
                        totalBytes += bytes.Length;
                        if (totalBytes > PersistenceFiles.MaxBackupBytes)
                        {
                            throw new InvalidDataException("The backup exceeds the total project size limit.");
                        }

                        string fileName = index.ToString("D6", CultureInfo.InvariantCulture) + ".ziwa";
                        string stagedPath = Path.Combine(staging, fileName);
                        await _atomicWriter.WriteBytesAtomicallyAsync(stagedPath, bytes, cancellationToken).ConfigureAwait(false);
                        ProjectSnapshot snapshot = await _projectStore.LoadAsync(stagedPath, cancellationToken).ConfigureAwait(false);
                        if (snapshot.Document.Id != entry.DocumentId)
                        {
                            throw new InvalidDataException("A library project no longer matches its recorded document id.");
                        }

                        string archivePath = "projects/" + fileName;
                        await using (Stream target = archive.CreateEntry(archivePath, CompressionLevel.Optimal).Open())
                        {
                            await target.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        }

                        manifestProjects.Add(new BackupProject(archivePath, entry,
                            Convert.ToHexString(SHA256.HashData(bytes))));
                        File.Delete(stagedPath);
                    }

                    byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new BackupManifest(1, manifestProjects),
                        PersistenceFiles.JsonOptions);
                    if (manifest.Length > PersistenceFiles.MaxManifestBytes)
                    {
                        throw new InvalidDataException("The backup manifest exceeds its size limit.");
                    }

                    await using Stream targetManifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                    await targetManifest.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
                }

            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (staging is not null)
            {
                PersistenceFiles.DeleteDirectoryBestEffort(staging);
            }

            _gate.Release();
        }
    }

    public async Task<int> ImportBackupAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        string? importedDirectory = null;
        bool committed = false;
        bool published = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            string source = Path.GetFullPath(path);
            PersistenceFiles.EnsureRegularFile(source);
            await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (sourceStream.Length > PersistenceFiles.MaxBackupBytes)
            {
                throw new InvalidDataException("The archive exceeds its size limit.");
            }

            using var archive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: true);
            Dictionary<string, ZipArchiveEntry> entries = ValidateArchiveEntries(archive);
            if (!entries.TryGetValue("manifest.json", out ZipArchiveEntry? manifestEntry)
                || manifestEntry.Length > PersistenceFiles.MaxManifestBytes)
            {
                throw new InvalidDataException("Missing or oversized backup manifest.");
            }

            BackupManifest manifest;
            await using (Stream stream = manifestEntry.Open())
            {
                byte[] bytes = await PersistenceFiles.ReadBoundedAsync(stream,
                    PersistenceFiles.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
                ProjectDocumentSerializer.ValidateUniqueProperties(bytes);
                try
                {
                    manifest = JsonSerializer.Deserialize<BackupManifest>(bytes, PersistenceFiles.JsonOptions)
                        ?? throw new InvalidDataException("Empty backup manifest.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("Invalid backup manifest.", exception);
                }
            }

            ValidateManifest(manifest, entries);
            string importsRoot = Path.Combine(_root, "imports");
            PersistenceFiles.EnsureRegularDirectory(importsRoot);
            string importId = Guid.NewGuid().ToString("N");
            staging = Path.Combine(importsRoot, ".staging-" + importId);
            importedDirectory = Path.Combine(importsRoot, importId);
            PersistenceFiles.EnsureRegularDirectory(staging);
            var imported = new List<ProjectLibraryEntry>();
            foreach (BackupProject project in manifest.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry zipEntry = entries[project.File];
                byte[] bytes;
                await using (Stream stream = zipEntry.Open())
                {
                    bytes = await PersistenceFiles.ReadBoundedAsync(stream,
                        PersistenceFiles.MaxProjectBytes, cancellationToken).ConfigureAwait(false);
                }

                if (bytes.LongLength != zipEntry.Length
                    || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(project.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Backup project checksum mismatch.");
                }

                string name = Path.GetFileName(project.File);
                string stagedPath = Path.Combine(staging, name);
                await _atomicWriter.WriteBytesAtomicallyAsync(stagedPath, bytes, cancellationToken).ConfigureAwait(false);
                ProjectSnapshot snapshot = await _projectStore.LoadAsync(stagedPath, cancellationToken).ConfigureAwait(false);
                if (snapshot.Document.Id != project.Metadata.DocumentId)
                {
                    throw new InvalidDataException("Backup project metadata does not match the bundle.");
                }

                imported.Add(project.Metadata with { Path = Path.Combine(importedDirectory, name) });
            }

            // Only publish the unique managed directory after every bundle has passed validation.
            // Directory.Move never overwrites existing data; the database transaction is all-or-nothing.
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, importedDirectory);
            published = true;
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (ProjectLibraryEntry entry in imported)
            {
                await RecordCoreAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            committed = true;
            return imported.Count;
        }
        finally
        {
            if (staging is not null)
            {
                PersistenceFiles.DeleteDirectoryBestEffort(staging);
            }

            if (!committed && published && importedDirectory is not null)
            {
                PersistenceFiles.DeleteDirectoryBestEffort(importedDirectory);
            }

            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        PersistenceFiles.EnsureRegularDirectory(_root);
        if (File.Exists(_databasePath))
        {
            PersistenceFiles.EnsureRegularFile(_databasePath);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 10,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!_initialized)
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAtUtc TEXT NOT NULL);";
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                migration.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
                int version = Convert.ToInt32(await migration.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (version > SchemaVersion)
                {
                    throw new InvalidDataException("The project library schema is newer than this application.");
                }

                if (version < 1)
                {
                    migration.CommandText = """
                        CREATE TABLE Projects (
                            PathKey TEXT PRIMARY KEY NOT NULL,
                            Path TEXT NOT NULL,
                            DocumentId TEXT NOT NULL,
                            Title TEXT NOT NULL,
                            SavedAtUtc TEXT NOT NULL,
                            OpenedAtUtc TEXT NOT NULL
                        );
                        CREATE INDEX IX_Projects_OpenedAtUtc ON Projects (OpenedAtUtc DESC);
                        INSERT INTO SchemaMigrations (Version, AppliedAtUtc) VALUES ($version, $applied);
                        """;
                    migration.Parameters.AddWithValue("$version", 1);
                    migration.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
                _initialized = true;
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<ProjectLibraryEntry>> ListCoreAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Path, DocumentId, Title, SavedAtUtc, OpenedAtUtc FROM Projects ORDER BY OpenedAtUtc DESC, PathKey;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ProjectLibraryEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ProjectLibraryEntry(reader.GetString(0), new DocumentId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }

        return entries;
    }

    private static async Task RecordCoreAsync(SqliteConnection connection, SqliteTransaction? transaction,
        ProjectLibraryEntry entry, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Projects (PathKey, Path, DocumentId, Title, SavedAtUtc, OpenedAtUtc)
            VALUES ($key, $path, $document, $title, $saved, $opened)
            ON CONFLICT (PathKey) DO UPDATE SET Path = excluded.Path, DocumentId = excluded.DocumentId,
                Title = excluded.Title, SavedAtUtc = excluded.SavedAtUtc, OpenedAtUtc = excluded.OpenedAtUtc;
            """;
        command.Parameters.AddWithValue("$key", OperatingSystem.IsWindows() ? entry.Path.ToUpperInvariant() : entry.Path);
        command.Parameters.AddWithValue("$path", entry.Path);
        command.Parameters.AddWithValue("$document", entry.DocumentId.Value.ToString("D"));
        command.Parameters.AddWithValue("$title", entry.Title);
        command.Parameters.AddWithValue("$saved", entry.SavedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$opened", entry.OpenedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProjectLibraryEntry ValidateEntry(ProjectLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Path);
        if (entry.DocumentId.Value == Guid.Empty || entry.Title is null || entry.Title.Length > 4096)
        {
            throw new InvalidDataException("Invalid project library metadata.");
        }

        return entry with { Path = Path.GetFullPath(entry.Path) };
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateArchiveEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > PersistenceFiles.MaxBackupProjects + 1)
        {
            throw new InvalidDataException("The archive contains too many entries.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            // Accept only our exact portable format, never arbitrary filesystem destinations.
            string name = entry.FullName;
            bool projectName = name.Length == 20 && name.StartsWith("projects/", StringComparison.Ordinal)
                && name.EndsWith(".ziwa", StringComparison.Ordinal)
                && name.AsSpan(9, 6).IndexOfAnyExceptInRange('0', '9') < 0;
            if ((name != "manifest.json" && !projectName) || !uniqueNames.Add(name) || entry.Length < 0)
            {
                throw new InvalidDataException("Unexpected, duplicate, or unsafe backup entry.");
            }

            entries.Add(name, entry);
        }

        return entries;
    }

    private static void ValidateManifest(BackupManifest manifest, Dictionary<string, ZipArchiveEntry> entries)
    {
        if (manifest.Version != 1 || manifest.Projects is null
            || manifest.Projects.Count > PersistenceFiles.MaxBackupProjects || entries.Count != manifest.Projects.Count + 1)
        {
            throw new InvalidDataException("Unsupported or inconsistent backup manifest.");
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (BackupProject project in manifest.Projects)
        {
            if (project is null || project.File is null || !project.File.StartsWith("projects/", StringComparison.Ordinal)
                || !used.Add(project.File) || !entries.TryGetValue(project.File, out ZipArchiveEntry? entry)
                || entry.Length > PersistenceFiles.MaxProjectBytes || project.Sha256 is null
                || project.Sha256.Length != 64 || !project.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Invalid backup project metadata.");
            }

            try
            {
                ValidateEntry(project.Metadata);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Invalid backup project path.", exception);
            }

            totalBytes += entry.Length;
            if (totalBytes > PersistenceFiles.MaxBackupBytes)
            {
                throw new InvalidDataException("The backup exceeds the total project size limit.");
            }
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record BackupManifest(int Version, List<BackupProject> Projects);
    private sealed record BackupProject(string File, ProjectLibraryEntry Metadata, string Sha256);
}
