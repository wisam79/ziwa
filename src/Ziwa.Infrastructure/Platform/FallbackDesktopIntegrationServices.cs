using Ziwa.Application.Abstractions;
using Ziwa.Infrastructure.Assets;

namespace Ziwa.Infrastructure.Platform;

public sealed class FallbackDropFileFilter : IDropFileFilter
{
    public IReadOnlyList<string> FilterSupportedImageFiles(IEnumerable<string> candidatePaths)
    {
        if (candidatePaths is null)
        {
            return [];
        }

        var accepted = new List<string>();
        foreach (string rawPath in candidatePaths)
        {
            if (IsSupportedImageFile(rawPath))
            {
                accepted.Add(Path.GetFullPath(rawPath));
            }
        }

        return accepted;
    }

    public bool IsSupportedImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var info = new FileInfo(fullPath);
            if (info.Length <= 0 || info.Length > FileImageAssetImporter.MaxImageFileBytes)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[16];
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read = fs.Read(header);
            return FileImageAssetImporter.HasSupportedRasterMagicBytes(header[..read]);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class FallbackSingleInstanceCoordinator : ISingleInstanceCoordinator
{
    public event Action<string?>? SecondaryInstanceActivated
    {
        add { }
        remove { }
    }

    public bool TryAcquireOrForward(string[] args, out bool forwardedToPrimary)
    {
        forwardedToPrimary = false;
        return true;
    }

    public void StartListening()
    {
    }

    public void Dispose()
    {
    }
}

public sealed class FallbackFileAssociationService : IFileAssociationService
{
    public bool IsZiwaExtensionRegistered() => false;
    public void EnsureZiwaExtensionRegistered() { }
}
