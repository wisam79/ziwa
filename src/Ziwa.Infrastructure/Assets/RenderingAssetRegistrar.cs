using Ziwa.Application.Abstractions;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Assets;

public sealed class RenderingAssetRegistrar : IAssetRegistrar
{
    private readonly AssetStore _assetStore;

    public RenderingAssetRegistrar(AssetStore assetStore)
    {
        _assetStore = assetStore;
    }

    public AssetId RegisterAsset(ReadOnlySpan<byte> encodedBytes) => _assetStore.RegisterAsset(encodedBytes);

    public AssetId RegisterSvgAsset(string rawSvgXml) => _assetStore.RegisterSvgAsset(rawSvgXml);

    public AssetPixelDimensions? TryGetAssetDimensions(AssetId assetId) =>
        _assetStore.TryGetMetadata(assetId, out AssetMetadata? meta) && meta is not null
            ? new AssetPixelDimensions(meta.WidthPx, meta.HeightPx)
            : null;
}
