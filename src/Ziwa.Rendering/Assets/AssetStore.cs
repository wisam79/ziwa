using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Svg.Skia;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Svg;
using Ziwa.Rendering.Typography;

namespace Ziwa.Rendering.Assets;

public sealed record AssetMetadata(AssetId Id, int WidthPx, int HeightPx, SKEncodedOrigin Origin);

public readonly struct AssetImageLease : IDisposable
{
    private readonly bool _ownsDisposal;

    public AssetImageLease(SKImage? image, bool ownsDisposal)
    {
        Image = image;
        _ownsDisposal = ownsDisposal;
    }

    public SKImage? Image { get; }

    public void Dispose()
    {
        if (_ownsDisposal)
        {
            Image?.Dispose();
        }
    }
}

public interface IRenderAssetProvider
{
    AssetImageLease AcquireImage(AssetId assetId, RenderQualityMode qualityMode);
    SKPicture? AcquireSvgPicture(AssetId svgAssetId);
    SKPicture? GetOrBuildSvgPicture(string rawSvgXml);
}

public sealed class AssetStore : IRenderAssetProvider, IDisposable
{
    public const int MaxPreviewEdgePx = 2048;

    private readonly ConcurrentDictionary<AssetId, StoredAsset> _assets = new();
    private readonly ConcurrentDictionary<AssetId, StoredSvgAsset> _svgAssets = new();
    private readonly DisplayProxyCache _previewCache;
    private readonly FontRegistry _fontRegistry;
    private readonly HarfBuzzArabicShaper _shaper;
    private readonly bool _ownsFontRegistry;
    private bool _disposed;

    public AssetStore(long previewCacheMaxBytes = 256L * 1024L * 1024L)
    {
        _previewCache = new DisplayProxyCache(previewCacheMaxBytes);
        _fontRegistry = new FontRegistry();
        _shaper = new HarfBuzzArabicShaper(_fontRegistry);
        _ownsFontRegistry = true;
    }

    public AssetStore(
        FontRegistry fontRegistry,
        HarfBuzzArabicShaper shaper,
        long previewCacheMaxBytes = 256L * 1024L * 1024L)
    {
        _previewCache = new DisplayProxyCache(previewCacheMaxBytes);
        _fontRegistry = fontRegistry;
        _shaper = shaper;
        _ownsFontRegistry = false;
    }

    public int AssetCount => _assets.Count;
    public int SvgAssetCount => _svgAssets.Count;
    public int CachedPreviewCount => _previewCache.Count;

    public AssetId RegisterAsset(ReadOnlySpan<byte> encodedBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (encodedBytes.IsEmpty)
        {
            throw new ArgumentException("Asset payload cannot be empty.", nameof(encodedBytes));
        }

        byte[] hash = SHA256.HashData(encodedBytes);
        var id = new AssetId(Convert.ToHexStringLower(hash));

        if (_assets.ContainsKey(id))
        {
            return id;
        }

        byte[] copy = encodedBytes.ToArray();
        using SKData data = SKData.CreateCopy(copy);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null)
        {
            throw new InvalidDataException("Encoded bytes do not represent a valid raster image.");
        }

        SKEncodedOrigin origin = codec.EncodedOrigin;
        bool swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int logicalWidth = swapsAxes ? codec.Info.Height : codec.Info.Width;
        int logicalHeight = swapsAxes ? codec.Info.Width : codec.Info.Height;

        var metadata = new AssetMetadata(id, logicalWidth, logicalHeight, origin);
        _assets.TryAdd(id, new StoredAsset(copy, metadata));
        return id;
    }

    public AssetId RegisterSvgAsset(string rawSvgXml)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string sanitized = SvgSanitizer.Sanitize(rawSvgXml);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sanitized));
        var id = new AssetId($"svg-{Convert.ToHexStringLower(hash)}");

        if (_svgAssets.ContainsKey(id))
        {
            return id;
        }

        string outlinedSvg = SvgArabicTextOutliner.OutlineTextElements(sanitized, _fontRegistry, _shaper);
        byte[] renderBytes = Encoding.UTF8.GetBytes(outlinedSvg);

        var skSvg = new SKSvg();
        using var stream = new MemoryStream(renderBytes, writable: false);
        skSvg.Load(stream);
        if (skSvg.Picture is null)
        {
            skSvg.Dispose();
            throw new InvalidDataException("Sanitized SVG failed to parse into a vector picture.");
        }

        if (!_svgAssets.TryAdd(id, new StoredSvgAsset(sanitized, skSvg)))
        {
            skSvg.Dispose();
        }

        return id;
    }

    public bool TryGetEncodedBytes(AssetId assetId, out byte[]? bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_assets.TryGetValue(assetId, out StoredAsset? stored))
        {
            bytes = (byte[])stored.EncodedBytes.Clone();
            return true;
        }

        bytes = null;
        return false;
    }

    public bool TryGetMetadata(AssetId assetId, out AssetMetadata? metadata)
    {
        if (_assets.TryGetValue(assetId, out StoredAsset? stored))
        {
            metadata = stored.Metadata;
            return true;
        }

        metadata = null;
        return false;
    }

    public bool TryGetSanitizedSvg(AssetId svgAssetId, out string? sanitizedXml)
    {
        if (_svgAssets.TryGetValue(svgAssetId, out StoredSvgAsset? stored))
        {
            sanitizedXml = stored.SanitizedXml;
            return true;
        }

        sanitizedXml = null;
        return false;
    }

    public AssetImageLease AcquireImage(AssetId assetId, RenderQualityMode qualityMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_assets.TryGetValue(assetId, out StoredAsset? stored))
        {
            return default;
        }

        if (qualityMode == RenderQualityMode.InteractivePreview)
        {
            if (_previewCache.TryGet(assetId, out SKImage? cached) && cached is not null)
            {
                return new AssetImageLease(cached, ownsDisposal: false);
            }

            SKImage decodedPreview = DecodeNormalized(stored.EncodedBytes, MaxPreviewEdgePx);
            SKImage cachedInstance = _previewCache.Put(assetId, decodedPreview);
            return new AssetImageLease(cachedInstance, ownsDisposal: false);
        }

        SKImage fullRes = DecodeNormalized(stored.EncodedBytes, maxEdgePx: null);
        return new AssetImageLease(fullRes, ownsDisposal: true);
    }

    public SKPicture? AcquireSvgPicture(AssetId svgAssetId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _svgAssets.TryGetValue(svgAssetId, out StoredSvgAsset? stored)
            ? stored.Svg.Picture
            : null;
    }

    public SKPicture? GetOrBuildSvgPicture(string rawSvgXml)
    {
        AssetId id = RegisterSvgAsset(rawSvgXml);
        return AcquireSvgPicture(id);
    }

    private static SKImage DecodeNormalized(byte[] encodedBytes, int? maxEdgePx)
    {
        using SKData data = SKData.CreateCopy(encodedBytes);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null)
        {
            throw new InvalidDataException("Unable to create SKCodec for registered asset.");
        }

        SKImageInfo decodeInfo = codec.Info;
        if (maxEdgePx is int maxEdge && maxEdge > 0)
        {
            int longest = Math.Max(decodeInfo.Width, decodeInfo.Height);
            if (longest > maxEdge)
            {
                float desiredScale = (float)maxEdge / longest;
                SKSizeI scaled = codec.GetScaledDimensions(desiredScale);
                if (scaled.Width > 0 && scaled.Height > 0)
                {
                    decodeInfo = decodeInfo.WithSize(scaled.Width, scaled.Height);
                }
            }
        }

        decodeInfo = decodeInfo.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Premul);
        using var decodedBitmap = new SKBitmap(decodeInfo);
        SKCodecResult result = codec.GetPixels(decodeInfo, decodedBitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"Failed to decode raster asset ({result}).");
        }

        SKEncodedOrigin origin = codec.EncodedOrigin;
        int srcW = decodedBitmap.Width;
        int srcH = decodedBitmap.Height;
        int targetW = srcW;
        int targetH = srcH;

        if (maxEdgePx is int targetMaxEdge && targetMaxEdge > 0)
        {
            int longest = Math.Max(srcW, srcH);
            if (longest > targetMaxEdge)
            {
                double scale = (double)targetMaxEdge / longest;
                targetW = Math.Max(1, (int)Math.Round(srcW * scale));
                targetH = Math.Max(1, (int)Math.Round(srcH * scale));
            }
        }

        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            if (targetW == srcW && targetH == srcH)
            {
                return SKImage.FromBitmap(decodedBitmap);
            }

            using SKBitmap? resized = decodedBitmap.Resize(
                new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul),
                SKFilterQuality.Medium)
                ?? throw new InvalidOperationException("Failed to resize preview bitmap.");
            return SKImage.FromBitmap(resized);
        }

        bool swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int outWidth = swapsAxes ? targetH : targetW;
        int outHeight = swapsAxes ? targetW : targetH;

        using var orientedBitmap = new SKBitmap(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(orientedBitmap))
        {
            canvas.Clear(SKColors.Transparent);
            SKMatrix orientationMatrix = CreateExifOrientationMatrix(origin, targetW, targetH);
            if (targetW != srcW || targetH != srcH)
            {
                SKMatrix downscale = SKMatrix.CreateScale((float)targetW / srcW, (float)targetH / srcH);
                orientationMatrix = orientationMatrix.PreConcat(downscale);
            }

            canvas.SetMatrix(orientationMatrix);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(decodedBitmap, 0f, 0f, paint);
        }

        return SKImage.FromBitmap(orientedBitmap);
    }

    private static SKMatrix CreateExifOrientationMatrix(SKEncodedOrigin origin, int srcWidth, int srcHeight)
    {
        return origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1f, 0f, srcWidth, 0f, 1f, 0f, 0f, 0f, 1f),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1f, 0f, srcWidth, 0f, -1f, srcHeight, 0f, 0f, 1f),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1f, 0f, 0f, 0f, -1f, srcHeight, 0f, 0f, 1f),
            SKEncodedOrigin.LeftTop => new SKMatrix(0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f),
            SKEncodedOrigin.RightTop => new SKMatrix(0f, -1f, srcHeight, 1f, 0f, 0f, 0f, 0f, 1f),
            SKEncodedOrigin.RightBottom => new SKMatrix(0f, -1f, srcHeight, -1f, 0f, srcWidth, 0f, 0f, 1f),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0f, 1f, 0f, -1f, 0f, srcWidth, 0f, 0f, 1f),
            _ => SKMatrix.Identity,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewCache.Dispose();
        _assets.Clear();

        foreach (StoredSvgAsset svg in _svgAssets.Values)
        {
            svg.Svg.Dispose();
        }

        _svgAssets.Clear();

        if (_ownsFontRegistry)
        {
            _fontRegistry.Dispose();
        }
    }

    private sealed record StoredAsset(byte[] EncodedBytes, AssetMetadata Metadata);
    private sealed record StoredSvgAsset(string SanitizedXml, SKSvg Svg);
}
