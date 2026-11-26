using Ziwa.Application.Abstractions;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Assets;

public sealed class FileImageAssetImporter : IImageAssetImporter
{
    public const long MaxImageFileBytes = 128L * 1024L * 1024L;
    private readonly AssetStore _assetStore;

    public FileImageAssetImporter(AssetStore assetStore)
    {
        _assetStore = assetStore;
    }

    public async Task<AssetId> ImportImageFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));
        }

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Selected image file was not found.", fullPath);
        }

        var info = new FileInfo(fullPath);
        if (info.Length <= 0 || info.Length > MaxImageFileBytes)
        {
            throw new InvalidDataException($"Image size ({info.Length} bytes) exceeds allowed limits.");
        }

        byte[] header = new byte[Math.Min(16, (int)info.Length)];
        await using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
        {
            int read = 0;
            while (read < header.Length)
            {
                int n = await fs.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (!HasSupportedRasterMagicBytes(header.AsSpan(0, read)))
            {
                throw new InvalidDataException("الملف المختار ليس صورة مدعومة (PNG / JPEG / WebP / BMP).");
            }
        }

        byte[] allBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return _assetStore.RegisterAsset(allBytes);
    }

    public static bool HasSupportedRasterMagicBytes(ReadOnlySpan<byte> header)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return true;
        }

        // JPEG: FF D8 FF
        if (header.Length >= 3 &&
            header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return true;
        }

        // WebP: RIFF....WEBP
        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return true;
        }

        // BMP: 42 4D
        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
        {
            return true;
        }

        return false;
    }
}
