using System.Runtime.InteropServices;
using SkiaSharp;
using Ziwa.Domain.Primitives;

namespace Ziwa.Rendering.Filters;

public static class DocumentScanFilterEngine
{
    public static byte ComputeOtsuThreshold(ReadOnlySpan<int> histogram256, int totalPixels)
    {
        if (histogram256.Length != 256)
        {
            throw new ArgumentException("Histogram must contain 256 bins.", nameof(histogram256));
        }

        if (totalPixels <= 0)
        {
            return 128;
        }

        double totalSum = 0.0;
        for (int i = 0; i < 256; i++)
        {
            totalSum += (double)i * histogram256[i];
        }

        double sumB = 0.0;
        int wB = 0;
        double maxVariance = -1.0;
        int bestThreshold = 128;

        for (int t = 0; t < 256; t++)
        {
            wB += histogram256[t];
            if (wB == 0)
            {
                continue;
            }

            int wF = totalPixels - wB;
            if (wF == 0)
            {
                break;
            }

            sumB += (double)t * histogram256[t];
            double mB = sumB / wB;
            double mF = (totalSum - sumB) / wF;
            double diff = mB - mF;
            double betweenVariance = (double)wB * wF * diff * diff;

            if (betweenVariance > maxVariance)
            {
                maxVariance = betweenVariance;
                bestThreshold = t;
            }
        }

        return (byte)Math.Clamp(bestThreshold, 24, 235);
    }

    public static void ApplyInPlace(
        SKBitmap bitmap,
        DocumentScanFilterKind filterKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (filterKind == DocumentScanFilterKind.None || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return;
        }

        int w = bitmap.Width;
        int h = bitmap.Height;
        using SKPixmap? pixmap = bitmap.PeekPixels();
        if (pixmap is null)
        {
            return;
        }

        IntPtr ptr = pixmap.GetPixels();
        int rowBytes = pixmap.RowBytes;
        int totalBytes = rowBytes * h;
        if (ptr == IntPtr.Zero || totalBytes <= 0)
        {
            return;
        }

        byte[] buffer = new byte[totalBytes];
        Marshal.Copy(ptr, buffer, 0, totalBytes);

        switch (filterKind)
        {
            case DocumentScanFilterKind.MagicColor:
                ApplyMagicColor(buffer, w, h, rowBytes, cancellationToken);
                break;
            case DocumentScanFilterKind.OtsuBlackAndWhite:
                ApplyOtsuBlackAndWhite(buffer, w, h, rowBytes, cancellationToken);
                break;
            case DocumentScanFilterKind.EnhancedGrayscale:
                ApplyEnhancedGrayscale(buffer, w, h, rowBytes, cancellationToken);
                break;
            case DocumentScanFilterKind.RemoveYellowing:
                ApplyRemoveYellowing(buffer, w, h, rowBytes, cancellationToken);
                break;
            case DocumentScanFilterKind.Sharpen:
                ApplySharpen(buffer, w, h, rowBytes, cancellationToken);
                break;
        }

        Marshal.Copy(buffer, 0, ptr, totalBytes);
        bitmap.NotifyPixelsChanged();
    }

    private static void ApplyMagicColor(
        byte[] buffer,
        int w,
        int h,
        int rowBytes,
        CancellationToken cancellationToken)
    {
        float[] luma = ExtractLuminance(buffer, w, h, rowBytes);
        int radius = Math.Clamp(Math.Min(w, h) / 20, 4, 48);
        float[] localMean = BoxBlurLuminance(luma, w, h, radius);
        (float wbR, float wbG, float wbB) = EstimatePaperWhiteBalanceGains(buffer, w, h, rowBytes);

        for (int y = 0; y < h; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int rowOffset = y * rowBytes;
            int lumaRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int px = rowOffset + (x * 4);
                float r = buffer[px] * wbR;
                float g = buffer[px + 1] * wbG;
                float b = buffer[px + 2] * wbB;

                float bgEst = Math.Max(68f, localMean[lumaRow + x] * 1.06f);
                float gain = Math.Clamp(250f / bgEst, 0.85f, 2.25f);

                float nr = r * gain;
                float ng = g * gain;
                float nb = b * gain;

                float lum = (0.299f * nr) + (0.587f * ng) + (0.114f * nb);
                float maxC = MathF.Max(nr, MathF.Max(ng, nb));
                float minC = MathF.Min(nr, MathF.Min(ng, nb));
                float chroma = maxC - minC;

                if (lum > 224f && chroma < 28f)
                {
                    buffer[px] = 255;
                    buffer[px + 1] = 255;
                    buffer[px + 2] = 255;
                    continue;
                }

                // Crisp ink contrast + vivid stamp/pen saturation
                nr = ((nr - 128f) * 1.24f) + 128f;
                ng = ((ng - 128f) * 1.24f) + 128f;
                nb = ((nb - 128f) * 1.24f) + 128f;
                float cLum = (0.299f * nr) + (0.587f * ng) + (0.114f * nb);
                nr = cLum + ((nr - cLum) * 1.22f);
                ng = cLum + ((ng - cLum) * 1.22f);
                nb = cLum + ((nb - cLum) * 1.22f);

                buffer[px] = ClampByte(nr);
                buffer[px + 1] = ClampByte(ng);
                buffer[px + 2] = ClampByte(nb);
            }
        }
    }

    private static void ApplyOtsuBlackAndWhite(
        byte[] buffer,
        int w,
        int h,
        int rowBytes,
        CancellationToken cancellationToken)
    {
        float[] luma = ExtractLuminance(buffer, w, h, rowBytes);
        int radius = Math.Clamp(Math.Min(w, h) / 18, 5, 40);
        float[] localMean = BoxBlurLuminance(luma, w, h, radius);

        int[] histogram = new int[256];
        int totalPixels = w * h;
        for (int i = 0; i < totalPixels; i++)
        {
            int bin = Math.Clamp((int)MathF.Round(luma[i]), 0, 255);
            histogram[bin]++;
        }

        byte otsuT = ComputeOtsuThreshold(histogram, totalPixels);

        for (int y = 0; y < h; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int rowOffset = y * rowBytes;
            int lumaRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = lumaRow + x;
                float val = luma[idx];
                float localGate = localMean[idx] * 0.88f;
                float effectiveT = MathF.Max(otsuT, localGate);
                bool isPaper = val >= effectiveT && val >= localGate;
                byte bw = isPaper ? (byte)255 : (byte)0;

                int px = rowOffset + (x * 4);
                buffer[px] = bw;
                buffer[px + 1] = bw;
                buffer[px + 2] = bw;
            }
        }
    }

    private static void ApplyEnhancedGrayscale(
        byte[] buffer,
        int w,
        int h,
        int rowBytes,
        CancellationToken cancellationToken)
    {
        float[] luma = ExtractLuminance(buffer, w, h, rowBytes);
        int radius = Math.Clamp(Math.Min(w, h) / 20, 4, 44);
        float[] localMean = BoxBlurLuminance(luma, w, h, radius);

        for (int y = 0; y < h; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int rowOffset = y * rowBytes;
            int lumaRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = lumaRow + x;
                float bg = Math.Max(60f, localMean[idx] * 1.05f);
                float normalized = luma[idx] * (248f / bg);
                float stretched = (normalized - 26f) * (255f / 210f);
                byte gray = ClampByte(stretched);

                int px = rowOffset + (x * 4);
                buffer[px] = gray;
                buffer[px + 1] = gray;
                buffer[px + 2] = gray;
            }
        }
    }

    private static void ApplyRemoveYellowing(
        byte[] buffer,
        int w,
        int h,
        int rowBytes,
        CancellationToken cancellationToken)
    {
        (float gainR, float gainG, float gainB) = EstimatePaperWhiteBalanceGains(buffer, w, h, rowBytes);

        for (int y = 0; y < h; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int rowOffset = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int px = rowOffset + (x * 4);
                float r = buffer[px] * gainR;
                float g = buffer[px + 1] * gainG;
                float b = buffer[px + 2] * gainB;

                float lum = (0.299f * r) + (0.587f * g) + (0.114f * b);
                if (lum > 236f && MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b)) < 26f)
                {
                    buffer[px] = 255;
                    buffer[px + 1] = 255;
                    buffer[px + 2] = 255;
                }
                else
                {
                    buffer[px] = ClampByte(r);
                    buffer[px + 1] = ClampByte(g);
                    buffer[px + 2] = ClampByte(b);
                }
            }
        }
    }

    private static (float GainR, float GainG, float GainB) EstimatePaperWhiteBalanceGains(
        byte[] buffer,
        int w,
        int h,
        int rowBytes)
    {
        int[] hist = new int[256];
        int totalPixels = w * h;
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int px = rowOffset + (x * 4);
                int lum = Math.Clamp(((77 * buffer[px]) + (150 * buffer[px + 1]) + (29 * buffer[px + 2])) >> 8, 0, 255);
                hist[lum]++;
            }
        }

        // Find 75th percentile paper luminance
        int targetRank = (int)(totalPixels * 0.75);
        int acc = 0;
        int p75 = 180;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            if (acc >= targetRank)
            {
                p75 = i;
                break;
            }
        }

        double sumR = 0;
        double sumG = 0;
        double sumB = 0;
        int paperCount = 0;
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                int px = rowOffset + (x * 4);
                int lum = ((77 * buffer[px]) + (150 * buffer[px + 1]) + (29 * buffer[px + 2])) >> 8;
                if (lum >= p75)
                {
                    sumR += buffer[px];
                    sumG += buffer[px + 1];
                    sumB += buffer[px + 2];
                    paperCount++;
                }
            }
        }

        float meanR = paperCount > 0 ? (float)(sumR / paperCount) : 240f;
        float meanG = paperCount > 0 ? (float)(sumG / paperCount) : 235f;
        float meanB = paperCount > 0 ? (float)(sumB / paperCount) : 210f;

        float targetWhite = MathF.Max(244f, MathF.Max(meanR, meanG));
        float gainR = Math.Clamp(targetWhite / MathF.Max(70f, meanR), 0.85f, 1.90f);
        float gainG = Math.Clamp(targetWhite / MathF.Max(70f, meanG), 0.85f, 1.90f);
        float gainB = Math.Clamp(targetWhite / MathF.Max(55f, meanB), 0.85f, 2.25f);
        return (gainR, gainG, gainB);
    }

    private static void ApplySharpen(
        byte[] buffer,
        int w,
        int h,
        int rowBytes,
        CancellationToken cancellationToken)
    {
        if (w < 3 || h < 3)
        {
            return;
        }

        byte[] copy = (byte[])buffer.Clone();
        const float k = 0.45f;

        for (int y = 1; y < h - 1; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int row = y * rowBytes;
            int upRow = (y - 1) * rowBytes;
            int dnRow = (y + 1) * rowBytes;

            for (int x = 1; x < w - 1; x++)
            {
                int px = row + (x * 4);
                int left = px - 4;
                int right = px + 4;
                int up = upRow + (x * 4);
                int down = dnRow + (x * 4);

                for (int c = 0; c < 3; c++)
                {
                    float center = copy[px + c];
                    float neighbors = copy[left + c] + copy[right + c] + copy[up + c] + copy[down + c];
                    float lap = (4f * center) - neighbors;
                    if (MathF.Abs(lap) >= 6f)
                    {
                        buffer[px + c] = ClampByte(center + (k * lap));
                    }
                }
            }
        }
    }

    private static float[] ExtractLuminance(byte[] buffer, int w, int h, int rowBytes)
    {
        float[] luma = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * rowBytes;
            int outRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int px = rowOffset + (x * 4);
                luma[outRow + x] = (0.299f * buffer[px]) + (0.587f * buffer[px + 1]) + (0.114f * buffer[px + 2]);
            }
        }

        return luma;
    }

    private static float[] BoxBlurLuminance(float[] src, int w, int h, int radius)
    {
        int stride = w + 1;
        double[] integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0.0;
            int srcRow = y * w;
            int intRow = (y + 1) * stride;
            int prevRow = y * stride;
            for (int x = 0; x < w; x++)
            {
                rowSum += src[srcRow + x];
                integral[intRow + x + 1] = integral[prevRow + x + 1] + rowSum;
            }
        }

        float[] dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(h - 1, y + radius);
            int r0 = y0 * stride;
            int r1 = (y1 + 1) * stride;
            int dstRow = y * w;

            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(w - 1, x + radius);
                double area = (x1 - x0 + 1) * (y1 - y0 + 1);
                double sum = integral[r1 + x1 + 1] - integral[r0 + x1 + 1] - integral[r1 + x0] + integral[r0 + x0];
                dst[dstRow + x] = (float)(sum / area);
            }
        }

        return dst;
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
