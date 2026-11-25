using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Abstractions;

public interface IAssetRegistrar
{
    AssetId RegisterAsset(ReadOnlySpan<byte> encodedBytes);
    AssetId RegisterSvgAsset(string rawSvgXml);
    AssetPixelDimensions? TryGetAssetDimensions(AssetId assetId);
}
