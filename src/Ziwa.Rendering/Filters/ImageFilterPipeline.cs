using System.Runtime.InteropServices;
using SkiaSharp;
using Ziwa.Domain.Math;
using Ziwa.Domain.Primitives;

namespace Ziwa.Rendering.Filters;

public sealed class ImageFilterPipeline : IDisposable
{
    public ImageFilterPipeline(
        SKImage image,
        SKRect sourceRect,
        SKFilterQuality filterQuality,
        SKColorFilter? colorFilter,
        SKImageFilter? imageFilter,
        bool ownsImage)
    {
        Image = image;
        SourceRect = sourceRect;
        FilterQuality = filterQuality;
        ColorFilter = colorFilter;
        ImageFilter = imageFilter;
        _ownsImage = ownsImage;
    }

    private readonly bool _ownsImage;

    public SKImage Image { get; }
    public SKRect SourceRect { get; }
    public SKFilterQuality FilterQuality { get; }
    public SKColorFilter? ColorFilter { get; }
    public SKImageFilter? ImageFilter { get; }

    public static ImageFilterPipeline Prepare(
        SKImage sourceImage,
        SKRect sourceCropRect,
        double destWidthMm,
        double destHeightMm,
        double dpi,
        ImageFilterSpec filters)
    {
        SKColorFilter? colorFilter = BuildCombinedColorFilter(filters);
        double effectiveDpi = Math.Max(72.0, dpi);
        float dpiScale = (float)(effectiveDpi / 96.0);
        SKImageFilter? blurFilter = filters.BlurPx > 0.001
            ? SKImageFilter.CreateBlur(
                (float)Math.Clamp(filters.BlurPx * dpiScale, 0.01, 200.0),
                (float)Math.Clamp(filters.BlurPx * dpiScale, 0.01, 200.0))
            : null;

        bool needsPixelPass = filters.PixelateBlockSize > 1 ||
            filters.LuminanceThreshold is not null ||
            filters.ScanFilter != DocumentScanFilterKind.None;
        if (!needsPixelPass)
        {
            return new ImageFilterPipeline(
                sourceImage,
                sourceCropRect,
                SKFilterQuality.High,
                colorFilter,
                blurFilter,
                ownsImage: false);
        }

        int destPxW = Math.Clamp(DomainMath.MmToPixelsRounded(Math.Max(1.0, destWidthMm), effectiveDpi), 1, 4096);
        int destPxH = Math.Clamp(DomainMath.MmToPixelsRounded(Math.Max(1.0, destHeightMm), effectiveDpi), 1, 4096);

        int workW = destPxW;
        int workH = destPxH;
        if (filters.PixelateBlockSize > 1)
        {
            int effectiveBlock = Math.Max(2, (int)Math.Round(filters.PixelateBlockSize * dpiScale));
            workW = Math.Max(1, destPxW / effectiveBlock);
            workH = Math.Max(1, destPxH / effectiveBlock);
        }

        var workInfo = new SKImageInfo(workW, workH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var workBitmap = new SKBitmap(workInfo);
        using (var canvas = new SKCanvas(workBitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint
            {
                IsAntialias = filters.PixelateBlockSize <= 1,
                FilterQuality = filters.PixelateBlockSize > 1 ? SKFilterQuality.Low : SKFilterQuality.High,
                ColorFilter = colorFilter,
                ImageFilter = blurFilter,
            };
            canvas.DrawImage(sourceImage, sourceCropRect, SKRect.Create(0f, 0f, workW, workH), paint);
        }

        colorFilter?.Dispose();
        blurFilter?.Dispose();

        if (filters.LuminanceThreshold is double rawThreshold)
        {
            double threshold = DomainMath.Clamp(rawThreshold, 0.0, 1.0);
            ApplyLuminanceThresholdInPlace(workBitmap, threshold);
        }

        if (filters.ScanFilter != DocumentScanFilterKind.None)
        {
            DocumentScanFilterEngine.ApplyInPlace(workBitmap, filters.ScanFilter);
        }

        SKImage processed = SKImage.FromBitmap(workBitmap);
        bool keepCrispPixels = filters.PixelateBlockSize > 1 ||
            filters.LuminanceThreshold is not null ||
            filters.ScanFilter == DocumentScanFilterKind.OtsuBlackAndWhite;
        return new ImageFilterPipeline(
            processed,
            SKRect.Create(0f, 0f, processed.Width, processed.Height),
            keepCrispPixels ? SKFilterQuality.None : SKFilterQuality.High,
            colorFilter: null,
            imageFilter: null,
            ownsImage: true);
    }

    public static SKColorFilter? BuildCombinedColorFilter(ImageFilterSpec filters)
    {
        double brightness = filters.Brightness;
        double contrast = filters.Contrast;
        double saturation = filters.Saturation;
        bool applySepia = false;
        (float R, float G, float B) tint = (1f, 1f, 1f);

        switch (filters.Preset)
        {
            case PhotoFilterPreset.WarmStudio:
                brightness += 0.04;
                contrast += 0.08;
                saturation += 0.12;
                tint = (1.06f, 1.01f, 0.93f);
                break;

            case PhotoFilterPreset.CoolCrisp:
                brightness += 0.02;
                contrast += 0.12;
                saturation -= 0.08;
                tint = (0.94f, 0.99f, 1.07f);
                break;

            case PhotoFilterPreset.ClassicBw:
                contrast += 0.15;
                saturation = -1.0;
                break;

            case PhotoFilterPreset.HighKeyId:
                brightness += 0.12;
                contrast += 0.06;
                saturation -= 0.05;
                break;

            case PhotoFilterPreset.SepiaVintage:
                applySepia = true;
                break;
        }

        bool hasBcs = Math.Abs(brightness) > 0.0001 ||
                       Math.Abs(contrast) > 0.0001 ||
                       Math.Abs(saturation) > 0.0001 ||
                       tint != (1f, 1f, 1f);

        if (!hasBcs && !applySepia)
        {
            return null;
        }

        float b = (float)DomainMath.Clamp(brightness, -1.0, 1.0);
        float c = (float)(1.0 + DomainMath.Clamp(contrast, -0.9, 2.0));
        float t = 0.5f * (1f - c);
        float s = (float)(1.0 + DomainMath.Clamp(saturation, -1.0, 2.0));

        const float rw = 0.2126f;
        const float gw = 0.7152f;
        const float bw = 0.0722f;
        float invS = 1f - s;

        float sr = invS * rw;
        float sg = invS * gw;
        float sb = invS * bw;

        float[] bcsMatrix =
        [
            c * (sr + s) * tint.R, c * sg * tint.R,       c * sb * tint.R,       0f, b + t,
            c * sr * tint.G,       c * (sg + s) * tint.G, c * sb * tint.G,       0f, b + t,
            c * sr * tint.B,       c * sg * tint.B,       c * (sb + s) * tint.B, 0f, b + t,
            0f,                    0f,                    0f,                    1f, 0f,
        ];

        using SKColorFilter bcsFilter = SKColorFilter.CreateColorMatrix(bcsMatrix);
        if (!applySepia)
        {
            return SKColorFilter.CreateColorMatrix(bcsMatrix);
        }

        float[] sepiaMatrix =
        [
            0.393f, 0.769f, 0.189f, 0f, 0f,
            0.349f, 0.686f, 0.168f, 0f, 0f,
            0.272f, 0.534f, 0.131f, 0f, 0f,
            0f,     0f,     0f,     1f, 0f,
        ];
        using SKColorFilter sepiaFilter = SKColorFilter.CreateColorMatrix(sepiaMatrix);
        return SKColorFilter.CreateCompose(bcsFilter, sepiaFilter);
    }

    private static void ApplyLuminanceThresholdInPlace(SKBitmap bitmap, double threshold)
    {
        IntPtr ptr = bitmap.GetPixels(out IntPtr byteLen);
        int count = (int)byteLen;
        if (ptr == IntPtr.Zero || count <= 0)
        {
            return;
        }

        byte[] buffer = new byte[count];
        Marshal.Copy(ptr, buffer, 0, count);

        for (int i = 0; i + 3 < count; i += 4)
        {
            byte r = buffer[i];
            byte g = buffer[i + 1];
            byte b = buffer[i + 2];
            double lum = ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255.0;
            byte bw = lum >= threshold ? (byte)255 : (byte)0;
            buffer[i] = bw;
            buffer[i + 1] = bw;
            buffer[i + 2] = bw;
        }

        Marshal.Copy(buffer, 0, ptr, count);
    }

    public void Dispose()
    {
        ColorFilter?.Dispose();
        ImageFilter?.Dispose();
        if (_ownsImage)
        {
            Image.Dispose();
        }
    }
}
