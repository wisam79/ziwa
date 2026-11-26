using System.Text.Json;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;

namespace Ziwa.Infrastructure.Persistence;

internal static class PersistenceFiles
{
    internal const int MaxManifestBytes = 1024 * 1024;
    internal const int MaxProjectBytes = 128 * 1024 * 1024;
    internal const long MaxBackupBytes = 512L * 1024 * 1024;
    internal const int MaxBackupProjects = 1000;
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };
    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Root(ProjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        return Path.GetFullPath(options.RootDirectory);
    }

    internal static bool IsSafeId(string id) => id.Length == 32 && Guid.TryParseExact(id, "N", out _);

    internal static void EnsureRegularDirectory(string path)
    {
        Directory.CreateDirectory(path);
        // Do not follow externally planted junctions/symlinks inside managed storage.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Managed storage cannot contain symbolic links or junctions.");
            }
        }
    }

    internal static void EnsureRegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InvalidDataException("Expected a regular file.");
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken cancellationToken)
    {
        EnsureRegularFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedAsync(stream, limit, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > limit)
            {
                throw new InvalidDataException("Persistence data exceeds the supported size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static Task WriteJsonAsync<T>(IAtomicFileWriter writer, string path, T value, CancellationToken cancellationToken) =>
        writer.WriteBytesAtomicallyAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken);

    internal static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBoundedAsync(path, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        ProjectDocumentSerializer.ValidateUniqueProperties(bytes);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Persistence metadata is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Persistence metadata is invalid.", exception);
        }
    }

    internal static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Failed staging cleanup never hides the original failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
