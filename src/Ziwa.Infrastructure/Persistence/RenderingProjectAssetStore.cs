using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Persistence;

public sealed class RenderingProjectAssetStore(AssetStore store) : IProjectAssetStore
{
    public ProjectSnapshot Capture(ZiwaDocument document)
    {
        _ = ProjectDocumentSerializer.Serialize(document);
        var assets = ImmutableArray.CreateBuilder<ProjectAsset>();
        foreach (AssetId id in ProjectAssetReferences.Collect(document))
        {
            if (store.TryGetEncodedBytes(id, out byte[]? bytes) && bytes is not null)
            {
                assets.Add(new ProjectAsset(id, ProjectAssetKind.Raster, bytes));
            }
            else if (store.TryGetSanitizedSvg(id, out string? svg) && svg is not null)
            {
                assets.Add(new ProjectAsset(id, ProjectAssetKind.Svg, Encoding.UTF8.GetBytes(svg)));
            }
            else
            {
                throw new InvalidDataException($"Project asset is missing: {id.Value}");
            }
        }

        return new ProjectSnapshot(document, assets.ToImmutable());
    }

    public void Restore(ProjectSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        foreach (ProjectAsset asset in snapshot.Assets)
        {
            Register(store, asset);
        }
    }

    internal static void ValidateSnapshot(ProjectSnapshot snapshot)
    {
        _ = ProjectDocumentSerializer.Serialize(snapshot.Document);
        if (snapshot.Assets.IsDefault || snapshot.Assets.Length > ZiwaProjectStore.MaxAssets)
        {
            throw new InvalidDataException("Invalid project asset collection.");
        }

        var ids = new HashSet<AssetId>();
        long total = 0;
        using var validationStore = new AssetStore();
        foreach (ProjectAsset asset in snapshot.Assets)
        {
            if (asset is null || asset.Data is null || asset.Data.Length == 0 || asset.Data.Length > ZiwaProjectStore.MaxAssetBytes || !ids.Add(asset.Id))
            {
                throw new InvalidDataException("Invalid, duplicate or oversized project asset.");
            }

            total += asset.Data.Length;
            if (total > ZiwaProjectStore.MaxProjectBytes)
            {
                throw new InvalidDataException("Project assets exceed the supported size limit.");
            }

            string hash = Convert.ToHexStringLower(SHA256.HashData(asset.Data));
            string expected = asset.Kind switch
            {
                ProjectAssetKind.Raster => hash,
                ProjectAssetKind.Svg => $"svg-{hash}",
                _ => throw new InvalidDataException("Unknown project asset kind."),
            };
            if (asset.Id.Value != expected || Register(validationStore, asset) != asset.Id)
            {
                throw new InvalidDataException("Asset fingerprint mismatch or unsafe SVG.");
            }

            if (validationStore.TryGetMetadata(asset.Id, out AssetMetadata? metadata) && metadata is not null &&
                (metadata.WidthPx > 32768 || metadata.HeightPx > 32768 || (long)metadata.WidthPx * metadata.HeightPx > 100_000_000))
            {
                throw new InvalidDataException("Raster image exceeds the supported decoded size.");
            }
        }

        if (!ids.SetEquals(ProjectAssetReferences.Collect(snapshot.Document)))
        {
            throw new InvalidDataException("Project assets do not match the document references.");
        }
    }

    private static AssetId Register(AssetStore target, ProjectAsset asset)
    {
        try
        {
            return asset.Kind switch
            {
                ProjectAssetKind.Raster => target.RegisterAsset(asset.Data),
                ProjectAssetKind.Svg => target.RegisterSvgAsset(new UTF8Encoding(false, true).GetString(asset.Data)),
                _ => throw new InvalidDataException("Unknown project asset kind."),
            };
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException)
        {
            throw new InvalidDataException("Invalid project asset payload.", ex);
        }
    }
}
