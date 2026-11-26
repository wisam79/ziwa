using SkiaSharp;

namespace Ziwa.Infrastructure.Ai;

public static class HairDetailAlphaMatting
{
    public readonly record struct BackdropStatistics(float MeanR, float MeanG, float MeanB, float StdDev);

    public static BackdropStatistics EstimateBorderBackdrop(ReadOnlySpan<byte> rgbaPixels, int width, int height)
    {
        int stripX = Math.Max(1, width / 20);
        int stripY = Math.Max(1, height / 20);

        double sumR = 0;
        double sumG = 0;
        double sumB = 0;
        int count = 0;

        for (int y = 0; y < height; y++)
        {
            bool isTop = y < stripY;
            bool isUpperSide = y < (int)(height * 0.72);
            if (!isTop && !isUpperSide)
            {
                continue;
            }

            int rowOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                if (!isTop && x >= stripX && x < width - stripX)
                {
                    continue;
                }

                int idx = rowOffset + (x * 4);
                if (rgbaPixels[idx + 3] < 16)
                {
                    continue;
                }

                sumR += rgbaPixels[idx];
                sumG += rgbaPixels[idx + 1];
                sumB += rgbaPixels[idx + 2];
                count++;
            }
        }

        if (count == 0)
        {
            return new BackdropStatistics(255f, 255f, 255f, 18f);
        }

        float mr = (float)(sumR / count);
        float mg = (float)(sumG / count);
        float mb = (float)(sumB / count);

        double varSum = 0;
        for (int y = 0; y < height; y++)
        {
            bool isTop = y < stripY;
            bool isUpperSide = y < (int)(height * 0.72);
            if (!isTop && !isUpperSide)
            {
                continue;
            }

            int rowOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                if (!isTop && x >= stripX && x < width - stripX)
                {
                    continue;
                }

                int idx = rowOffset + (x * 4);
                if (rgbaPixels[idx + 3] < 16)
                {
                    continue;
                }

                float dr = rgbaPixels[idx] - mr;
                float dg = rgbaPixels[idx + 1] - mg;
                float db = rgbaPixels[idx + 2] - mb;
                varSum += (dr * dr) + (dg * dg) + (db * db);
            }
        }

        float std = MathF.Sqrt((float)(varSum / (count * 3.0)));
        return new BackdropStatistics(mr, mg, mb, Math.Clamp(std, 6f, 65f));
    }

    public static byte[] RefineAlphaWithHairMatting(
        ReadOnlySpan<byte> fullRgba,
        int fullW,
        int fullH,
        ReadOnlySpan<float> coarseProb,
        int coarseW,
        int coarseH,
        BackdropStatistics backdrop,
        CancellationToken cancellationToken)
    {
        int n = checked(fullW * fullH);
        float[] upsampled = new float[n];
        float[] guideLuma = new float[n];

        for (int y = 0; y < fullH; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            float gy = coarseH > 1 ? ((y + 0.5f) * coarseH / fullH) - 0.5f : 0f;
            int y0 = Math.Clamp((int)MathF.Floor(gy), 0, coarseH - 1);
            int y1 = Math.Clamp(y0 + 1, 0, coarseH - 1);
            float fy = Math.Clamp(gy - y0, 0f, 1f);

            int rowOffset = y * fullW;
            for (int x = 0; x < fullW; x++)
            {
                float gx = coarseW > 1 ? ((x + 0.5f) * coarseW / fullW) - 0.5f : 0f;
                int x0 = Math.Clamp((int)MathF.Floor(gx), 0, coarseW - 1);
                int x1 = Math.Clamp(x0 + 1, 0, coarseW - 1);
                float fx = Math.Clamp(gx - x0, 0f, 1f);

                float p00 = coarseProb[(y0 * coarseW) + x0];
                float p10 = coarseProb[(y0 * coarseW) + x1];
                float p01 = coarseProb[(y1 * coarseW) + x0];
                float p11 = coarseProb[(y1 * coarseW) + x1];
                float p0 = p00 + ((p10 - p00) * fx);
                float p1 = p01 + ((p11 - p01) * fx);
                upsampled[rowOffset + x] = p0 + ((p1 - p0) * fy);

                int pxIdx = (rowOffset + x) * 4;
                guideLuma[rowOffset + x] = ((0.299f * fullRgba[pxIdx]) + (0.587f * fullRgba[pxIdx + 1]) + (0.114f * fullRgba[pxIdx + 2])) / 255f;
            }
        }

        // Compute boundary transition band + Two-Color Line matting for fine hair wisps.
        float[] alphaMatte = new float[n];
        int bandRadius = Math.Clamp(Math.Min(fullW, fullH) / 64, 2, 7);
        float colorThreshold = Math.Max(22f, backdrop.StdDev * 2.2f);

        for (int y = 0; y < fullH; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int rowOffset = y * fullW;
            for (int x = 0; x < fullW; x++)
            {
                int idx = rowOffset + x;
                float p = upsampled[idx];

                if (p >= 0.90f)
                {
                    alphaMatte[idx] = 1f;
                    continue;
                }

                // Check if near transition edge or intermediate confidence for hair detail preservation.
                bool nearEdge = p > 0.08f;
                if (!nearEdge)
                {
                    int xL = Math.Max(0, x - bandRadius);
                    int xR = Math.Min(fullW - 1, x + bandRadius);
                    int yT = Math.Max(0, y - bandRadius);
                    int yB = Math.Min(fullH - 1, y + bandRadius);
                    nearEdge = upsampled[(yT * fullW) + x] > 0.45f ||
                               upsampled[(yB * fullW) + x] > 0.45f ||
                               upsampled[(y * fullW) + xL] > 0.45f ||
                               upsampled[(y * fullW) + xR] > 0.45f;
                }

                if (!nearEdge)
                {
                    alphaMatte[idx] = 0f;
                    continue;
                }

                // Sample nearby high-confidence foreground color to project pixel onto [B -> F] line.
                float fgR = 0f;
                float fgG = 0f;
                float fgB = 0f;
                int fgCount = 0;

                int minNy = Math.Max(0, y - (bandRadius * 2));
                int maxNy = Math.Min(fullH - 1, y + (bandRadius * 2));
                int minNx = Math.Max(0, x - (bandRadius * 2));
                int maxNx = Math.Min(fullW - 1, x + (bandRadius * 2));
                int step = Math.Max(1, bandRadius / 2);

                for (int ny = minNy; ny <= maxNy; ny += step)
                {
                    for (int nx = minNx; nx <= maxNx; nx += step)
                    {
                        int nIdx = (ny * fullW) + nx;
                        if (upsampled[nIdx] >= 0.72f)
                        {
                            int nPx = nIdx * 4;
                            fgR += fullRgba[nPx];
                            fgG += fullRgba[nPx + 1];
                            fgB += fullRgba[nPx + 2];
                            fgCount++;
                        }
                    }
                }

                int px = idx * 4;
                float ir = fullRgba[px];
                float ig = fullRgba[px + 1];
                float ib = fullRgba[px + 2];

                float drBg = ir - backdrop.MeanR;
                float dgBg = ig - backdrop.MeanG;
                float dbBg = ib - backdrop.MeanB;
                float distToBg = MathF.Sqrt((drBg * drBg) + (dgBg * dgBg) + (dbBg * dbBg));

                if (fgCount == 0)
                {
                    alphaMatte[idx] = Math.Clamp((p - 0.15f) / 0.75f, 0f, 1f);
                    continue;
                }

                float fr = fgR / fgCount;
                float fg = fgG / fgCount;
                float fb = fgB / fgCount;

                float vr = fr - backdrop.MeanR;
                float vg = fg - backdrop.MeanG;
                float vb = fb - backdrop.MeanB;
                float lenSq = (vr * vr) + (vg * vg) + (vb * vb);

                if (lenSq < (colorThreshold * colorThreshold * 0.5f))
                {
                    alphaMatte[idx] = Math.Clamp((p - 0.10f) / 0.80f, 0f, 1f);
                    continue;
                }

                float projectedAlpha = Math.Clamp(((drBg * vr) + (dgBg * vg) + (dbBg * vb)) / (lenSq + 1e-4f), 0f, 1f);
                float bgGate = Math.Clamp((distToBg - (backdrop.StdDev * 0.65f)) / Math.Max(14f, colorThreshold), 0f, 1f);
                float combined = (0.45f * Math.Clamp((p - 0.08f) / 0.84f, 0f, 1f)) + (0.55f * projectedAlpha * bgGate);
                alphaMatte[idx] = Math.Clamp(combined, 0f, 1f);
            }
        }

        // Apply fast O(N) Guided Image Filter over the alpha matte using image luminance as guide.
        int filterRadius = Math.Clamp(Math.Min(fullW, fullH) / 96, 1, 3);
        float[] guided = ApplyFastGuidedFilter(guideLuma, alphaMatte, fullW, fullH, filterRadius, eps: 0.0025f, cancellationToken);

        byte[] result = new byte[n];
        for (int i = 0; i < n; i++)
        {
            float baseP = upsampled[i];
            float finalA = baseP >= 0.94f
                ? 1f
                : baseP <= 0.03f && guided[i] < 0.06f
                    ? 0f
                    : Math.Clamp(guided[i], 0f, 1f);

            byte quantized = (byte)Math.Clamp((int)MathF.Round(finalA * 255f), 0, 255);
            if (quantized < 8)
            {
                quantized = 0;
            }
            else if (quantized > 248)
            {
                quantized = 255;
            }

            // Respect existing source alpha channel (if already transparent).
            byte srcAlpha = fullRgba[(i * 4) + 3];
            result[i] = srcAlpha < 255 ? (byte)((quantized * srcAlpha + 127) / 255) : quantized;
        }

        return result;
    }

    private static float[] ApplyFastGuidedFilter(
        float[] guide,
        float[] src,
        int w,
        int h,
        int radius,
        float eps,
        CancellationToken cancellationToken)
    {
        int n = checked(w * h);
        float[] guideSq = new float[n];
        float[] guideSrc = new float[n];
        for (int i = 0; i < n; i++)
        {
            float g = guide[i];
            guideSq[i] = g * g;
            guideSrc[i] = g * src[i];
        }

        float[] meanI = BoxFilter(guide, w, h, radius);
        float[] meanP = BoxFilter(src, w, h, radius);
        float[] meanII = BoxFilter(guideSq, w, h, radius);
        float[] meanIP = BoxFilter(guideSrc, w, h, radius);

        cancellationToken.ThrowIfCancellationRequested();

        float[] a = new float[n];
        float[] b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float varI = meanII[i] - (meanI[i] * meanI[i]);
            float covIP = meanIP[i] - (meanI[i] * meanP[i]);
            float ai = covIP / (varI + eps);
            a[i] = ai;
            b[i] = meanP[i] - (ai * meanI[i]);
        }

        float[] meanA = BoxFilter(a, w, h, radius);
        float[] meanB = BoxFilter(b, w, h, radius);

        float[] output = new float[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = Math.Clamp((meanA[i] * guide[i]) + meanB[i], 0f, 1f);
        }

        return output;
    }

    private static float[] BoxFilter(float[] src, int w, int h, int radius)
    {
        int stride = w + 1;
        double[] integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
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

    public static SKColor ToSkColor(BackdropStatistics stats) =>
        new((byte)Math.Clamp((int)MathF.Round(stats.MeanR), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(stats.MeanG), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(stats.MeanB), 0, 255),
            255);
}
