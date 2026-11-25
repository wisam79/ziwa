using ImageMagick;
using SkiaSharp;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Validation;

namespace Ziwa.Rendering.Export;

public sealed class CmykTiffExporter
{
    private readonly SceneRenderer _renderer;

    public CmykTiffExporter(SceneRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    public static bool IsValidTiffHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        bool isIntelLittleEndian = bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00;
        bool isMotorolaBigEndian = bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A;
        return isIntelLittleEndian || isMotorolaBigEndian;
    }

    public byte[] ExportPageToCmykTiffBytes(
        PageModel page,
        double dpi = 300.0,
        CmykProfileKind profileKind = CmykProfileKind.CoatedFogra39,
        byte[]? customIccProfileBytes = null,
        bool includeBleed = false,
        bool includeCropMarks = false,
        bool includeRegistrationMarks = false,
        bool includeCutContour = false,
        int maxSingleSurfaceEdgePx = TiledRasterRenderer.DefaultMaxSingleSurfaceEdgePx,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        SchemaValidator.AssertFinitePositive(dpi, nameof(dpi));
        cancellationToken.ThrowIfCancellationRequested();

        IColorProfile targetProfile = ResolveTargetProfile(profileKind, customIccProfileBytes);

        byte[] rgbaBytes = TiledRasterRenderer.RenderPageToRgbaBytes(
            _renderer,
            page,
            dpi,
            includeBleed,
            includeCropMarks,
            includeRegistrationMarks,
            includeCutContour,
            SKColors.White,
            out int widthPx,
            out int heightPx,
            maxSingleSurfaceEdgePx: maxSingleSurfaceEdgePx,
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var settings = new PixelReadSettings((uint)widthPx, (uint)heightPx, StorageType.Char, PixelMapping.RGBA);
        using var image = new MagickImage(rgbaBytes.AsSpan(), settings);

        image.TransformColorSpace(ColorProfiles.SRGB, targetProfile);
        image.ColorSpace = ColorSpace.CMYK;
        image.SetProfile(targetProfile);

        image.Format = MagickFormat.Tiff;
        image.Settings.Compression = CompressionMethod.LZW;
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);

        cancellationToken.ThrowIfCancellationRequested();

        byte[] tiffBytes = image.ToByteArray(MagickFormat.Tiff);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValidTiffHeader(tiffBytes))
        {
            throw new InvalidDataException("Generated CMYK TIFF output does not contain a valid TIFF header.");
        }

        return tiffBytes;
    }

    private static IColorProfile ResolveTargetProfile(CmykProfileKind profileKind, byte[]? customIccProfileBytes)
    {
        return profileKind switch
        {
            CmykProfileKind.CoatedFogra39 => ColorProfiles.CoatedFOGRA39,
            CmykProfileKind.USWebCoatedSwop => ColorProfiles.USWebCoatedSWOP,
            CmykProfileKind.CustomIcc => customIccProfileBytes is { Length: > 0 }
                ? new ColorProfile(customIccProfileBytes)
                : throw new ArgumentException(
                    "Custom ICC profile bytes must be provided when CmykProfileKind.CustomIcc is selected.",
                    nameof(customIccProfileBytes)),
            _ => throw new ArgumentOutOfRangeException(nameof(profileKind), profileKind, "Unsupported CMYK color profile kind."),
        };
    }
}
