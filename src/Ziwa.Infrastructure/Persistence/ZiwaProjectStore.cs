using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Primitives;

namespace Ziwa.Infrastructure.Persistence;

public sealed class ZiwaProjectStore : IProjectStore
{
    public const int MaxAssets = 4096;
    public const int MaxAssetBytes = 128 * 1024 * 1024;
    public const long MaxProjectBytes = 512L * 1024 * 1024;
    private const int MaxManifestBytes = 10 * 1024 * 1024;
    private const string Format = "ZiwaProject";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    private readonly IAtomicFileWriter _writer;
    private readonly IDocumentMigrator[] _migrators;

    public ZiwaProjectStore(IAtomicFileWriter writer, IEnumerable<IDocumentMigrator>? migrators = null)
    {
        _writer = writer;
        _migrators = (migrators ?? []).ToArray();
    }

    public Task SaveAsync(string path, ProjectSnapshot snapshot, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenderingProjectAssetStore.ValidateSnapshot(snapshot);
        using JsonDocument document = JsonDocument.Parse(ProjectDocumentSerializer.Serialize(snapshot.Document), new JsonDocumentOptions { MaxDepth = 128 });
        AssetEntry[] entries = snapshot.Assets.Select(asset => new AssetEntry(
            asset.Id.Value, asset.Kind, AssetPath(asset.Id), Convert.ToHexStringLower(SHA256.HashData(asset.Data)), asset.Data.Length)).ToArray();
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest(Format, 1, document.RootElement, entries), JsonOptions);
        if (manifest.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("Project manifest exceeds the supported size limit.");
        }

        await _writer.WriteAtomicallyAsync(path, async (stream, token) =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            await WriteEntryAsync(archive, "manifest.json", manifest, token).ConfigureAwait(false);
            foreach (ProjectAsset asset in snapshot.Assets)
            {
                await WriteEntryAsync(archive, AssetPath(asset.Id), asset.Data, token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public Task<ProjectSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxProjectBytes + MaxManifestBytes)
        {
            throw new InvalidDataException("Project file exceeds the supported size limit.");
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > MaxAssets + 1)
        {
            throw new InvalidDataException("Invalid project entry count.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!names.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Any(p => p is "." or ".." or ""))
            {
                throw new InvalidDataException("Duplicate or unsafe project archive entry.");
            }
        }

        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Project manifest is missing.");
        byte[] manifestBytes = await ReadEntryAsync(manifestEntry, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        ProjectDocumentSerializer.ValidateUniqueProperties(manifestBytes);
        Manifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, JsonOptions) ?? throw new InvalidDataException("Project manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Project manifest is invalid.", ex);
        }

        if (manifest.Format != Format || manifest.Version != 1 || manifest.Assets is null || manifest.Assets.Length > MaxAssets || archive.Entries.Count != manifest.Assets.Length + 1)
        {
            throw new InvalidDataException("Unsupported or incomplete project package.");
        }

        var document = ProjectDocumentSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(manifest.Document, JsonOptions), _migrators);
        var assets = ImmutableArray.CreateBuilder<ProjectAsset>();
        var assetIds = new HashSet<AssetId>();
        long total = 0;
        foreach (AssetEntry descriptor in manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor is null || descriptor.Size <= 0 || descriptor.Size > MaxAssetBytes || !Enum.IsDefined(descriptor.Kind))
            {
                throw new InvalidDataException("Invalid project asset descriptor.");
            }

            var id = new AssetId(descriptor.Id);
            string expectedId = descriptor.Kind == ProjectAssetKind.Svg ? $"svg-{descriptor.Sha256}" : descriptor.Sha256;
            if (descriptor.Sha256 is null || descriptor.Sha256.Length != 64 || descriptor.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                id.Value != expectedId || descriptor.Path != AssetPath(id) || !assetIds.Add(id))
            {
                throw new InvalidDataException("Invalid project asset identity or path.");
            }

            total += descriptor.Size;
            if (total > MaxProjectBytes)
            {
                throw new InvalidDataException("Project assets exceed the supported size limit.");
            }

            ZipArchiveEntry entry = archive.GetEntry(descriptor.Path) ?? throw new InvalidDataException("Project asset is missing.");
            byte[] bytes = await ReadEntryAsync(entry, (int)descriptor.Size, cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength != descriptor.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != descriptor.Sha256)
            {
                throw new InvalidDataException("Project asset checksum mismatch.");
            }

            assets.Add(new ProjectAsset(id, descriptor.Kind, bytes));
        }

        var snapshot = new ProjectSnapshot(document, assets.ToImmutable());
        RenderingProjectAssetStore.ValidateSnapshot(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }, cancellationToken);

    private static string AssetPath(AssetId id) => $"assets/{id.Value}.bin";

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] data, CancellationToken ct)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream output = entry.Open();
        await output.WriteAsync(data, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int limit, CancellationToken ct)
    {
        if (entry.Length > limit)
        {
            throw new InvalidDataException("Project entry exceeds the supported size limit.");
        }

        await using Stream input = entry.Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        while (true)
        {
            int read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) { return output.ToArray(); }
            if (output.Length + read > limit) { throw new InvalidDataException("Project entry exceeds the supported size limit."); }
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    private sealed record Manifest(string Format, int Version, JsonElement Document, AssetEntry[] Assets);
    private sealed record AssetEntry(string Id, ProjectAssetKind Kind, string Path, string Sha256, long Size);
}
