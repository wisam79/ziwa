using System.Runtime.InteropServices;
using SkiaSharp;
using Ziwa.Domain.Ai;

namespace Ziwa.Rendering.Ai;

public sealed class EditableAlphaMask
{
    private const int MaxUndoSteps = 50;

    private readonly byte[] _alpha;
    private readonly byte[] _initialAiAlpha;
    private readonly Stack<MaskPatchDelta> _undoStack = new();
    private readonly Stack<MaskPatchDelta> _redoStack = new();
    private byte[]? _strokeBeforeSnapshot;
    private SKRectI _strokeDirtyBounds;
    private bool _hasStrokeChanges;

    public EditableAlphaMask(int width, int height, ReadOnlySpan<byte> initialAlpha, ReadOnlySpan<byte> autoAiBaselineAlpha = default)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Mask width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Mask height must be positive.");
        }

        int expectedLen = checked(width * height);
        if (initialAlpha.Length != expectedLen)
        {
            throw new ArgumentException($"Alpha length ({initialAlpha.Length}) must equal width*height ({expectedLen}).", nameof(initialAlpha));
        }

        Width = width;
        Height = height;
        _alpha = initialAlpha.ToArray();

        if (!autoAiBaselineAlpha.IsEmpty)
        {
            if (autoAiBaselineAlpha.Length != expectedLen)
            {
                throw new ArgumentException($"Baseline alpha length ({autoAiBaselineAlpha.Length}) must equal width*height ({expectedLen}).", nameof(autoAiBaselineAlpha));
            }

            _initialAiAlpha = autoAiBaselineAlpha.ToArray();
        }
        else
        {
            _initialAiAlpha = initialAlpha.ToArray();
        }
    }

    public int Width { get; }
    public int Height { get; }
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public ReadOnlySpan<byte> AlphaSpan => _alpha;

    public byte[] GetAlphaCopy() => (byte[])_alpha.Clone();

    public void BeginStroke()
    {
        _strokeBeforeSnapshot = (byte[])_alpha.Clone();
        _strokeDirtyBounds = SKRectI.Empty;
        _hasStrokeChanges = false;
    }

    public SKRectI ApplyBrushSegment(
        float x0,
        float y0,
        float x1,
        float y1,
        float radiusPx,
        float softness,
        MaskBrushMode mode)
    {
        float r = Math.Clamp(radiusPx, 1f, Math.Max(Width, Height));
        float soft = Math.Clamp(softness, 0.05f, 1.0f);
        float innerR = r * (1.0f - soft);
        float targetAlpha = mode == MaskBrushMode.Erase ? 0f : 255f;

        float dx = x1 - x0;
        float dy = y1 - y0;
        float dist = MathF.Sqrt((dx * dx) + (dy * dy));
        float step = Math.Max(1.0f, r * 0.20f);
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / step));

        SKRectI totalDirty = SKRectI.Empty;
        for (int s = 0; s <= steps; s++)
        {
            float t = steps == 0 ? 0f : (float)s / steps;
            float cx = x0 + (dx * t);
            float cy = y0 + (dy * t);

            int minX = Math.Max(0, (int)MathF.Floor(cx - r));
            int maxX = Math.Min(Width - 1, (int)MathF.Ceiling(cx + r));
            int minY = Math.Max(0, (int)MathF.Floor(cy - r));
            int maxY = Math.Min(Height - 1, (int)MathF.Ceiling(cy + r));

            if (minX > maxX || minY > maxY)
            {
                continue;
            }

            float rSq = r * r;
            bool dabModified = false;

            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f - cy;
                int rowOffset = y * Width;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f - cx;
                    float dSq = (px * px) + (py * py);
                    if (dSq > rSq)
                    {
                        continue;
                    }

                    float d = MathF.Sqrt(dSq);
                    float weight;
                    if (d <= innerR)
                    {
                        weight = 1.0f;
                    }
                    else
                    {
                        float u = Math.Clamp((r - d) / Math.Max(0.001f, r - innerR), 0f, 1f);
                        weight = u * u * (3f - (2f * u));
                    }

                    int idx = rowOffset + x;
                    byte current = _alpha[idx];
                    byte updated = mode == MaskBrushMode.Erase
                        ? (byte)Math.Clamp((int)MathF.Round(Math.Min(current, 255f * (1f - weight))), 0, 255)
                        : (byte)Math.Clamp((int)MathF.Round(Math.Max(current, targetAlpha * weight)), 0, 255);

                    if (updated != current)
                    {
                        _alpha[idx] = updated;
                        dabModified = true;
                        _hasStrokeChanges = true;
                    }
                }
            }

            if (dabModified)
            {
                var dabRect = new SKRectI(minX, minY, maxX + 1, maxY + 1);
                totalDirty = totalDirty.IsEmpty ? dabRect : SKRectI.Union(totalDirty, dabRect);
            }
        }

        if (!totalDirty.IsEmpty)
        {
            _strokeDirtyBounds = _strokeDirtyBounds.IsEmpty ? totalDirty : SKRectI.Union(_strokeDirtyBounds, totalDirty);
        }

        return totalDirty;
    }

    public bool EndStroke()
    {
        byte[]? beforeFull = _strokeBeforeSnapshot;
        _strokeBeforeSnapshot = null;

        if (!_hasStrokeChanges || beforeFull is null || _strokeDirtyBounds.IsEmpty)
        {
            _strokeDirtyBounds = SKRectI.Empty;
            _hasStrokeChanges = false;
            return false;
        }

        MaskPatchDelta delta = CapturePatchDelta(_strokeDirtyBounds, beforeFull, _alpha);
        _strokeDirtyBounds = SKRectI.Empty;
        _hasStrokeChanges = false;

        PushUndoDelta(delta);
        return true;
    }

    public SKRectI? Undo()
    {
        if (_undoStack.Count == 0)
        {
            return null;
        }

        MaskPatchDelta delta = _undoStack.Pop();
        ApplyPatch(delta.Bounds, delta.BeforePatch);
        _redoStack.Push(delta);
        return delta.Bounds;
    }

    public SKRectI? Redo()
    {
        if (_redoStack.Count == 0)
        {
            return null;
        }

        MaskPatchDelta delta = _redoStack.Pop();
        ApplyPatch(delta.Bounds, delta.AfterPatch);
        _undoStack.Push(delta);
        return delta.Bounds;
    }

    public SKRectI? ResetToInitialAiMask()
    {
        int minX = Width;
        int minY = Height;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int idx = row + x;
                if (_alpha[idx] != _initialAiAlpha[idx])
                {
                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        var bounds = new SKRectI(minX, minY, maxX + 1, maxY + 1);
        byte[] beforeFull = (byte[])_alpha.Clone();
        Array.Copy(_initialAiAlpha, _alpha, _alpha.Length);
        MaskPatchDelta delta = CapturePatchDelta(bounds, beforeFull, _alpha);
        PushUndoDelta(delta);
        return bounds;
    }

    private void PushUndoDelta(MaskPatchDelta delta)
    {
        if (_undoStack.Count >= MaxUndoSteps)
        {
            MaskPatchDelta[] retained = _undoStack.Take(MaxUndoSteps - 1).Reverse().ToArray();
            _undoStack.Clear();
            foreach (MaskPatchDelta item in retained)
            {
                _undoStack.Push(item);
            }
        }

        _undoStack.Push(delta);
        _redoStack.Clear();
    }

    private MaskPatchDelta CapturePatchDelta(SKRectI bounds, byte[] beforeFull, byte[] afterFull)
    {
        int patchW = bounds.Width;
        int patchH = bounds.Height;
        byte[] beforePatch = new byte[patchW * patchH];
        byte[] afterPatch = new byte[patchW * patchH];

        for (int r = 0; r < patchH; r++)
        {
            int srcOffset = ((bounds.Top + r) * Width) + bounds.Left;
            int dstOffset = r * patchW;
            Array.Copy(beforeFull, srcOffset, beforePatch, dstOffset, patchW);
            Array.Copy(afterFull, srcOffset, afterPatch, dstOffset, patchW);
        }

        return new MaskPatchDelta(bounds, beforePatch, afterPatch);
    }

    private void ApplyPatch(SKRectI bounds, byte[] patch)
    {
        int patchW = bounds.Width;
        for (int r = 0; r < bounds.Height; r++)
        {
            int dstOffset = ((bounds.Top + r) * Width) + bounds.Left;
            int srcOffset = r * patchW;
            Array.Copy(patch, srcOffset, _alpha, dstOffset, patchW);
        }
    }

    private sealed record MaskPatchDelta(SKRectI Bounds, byte[] BeforePatch, byte[] AfterPatch);
}

public static class AlphaMaskCompositor
{
    public static SKColor EstimateBorderBackdropColor(SKBitmap sourceBitmap)
    {
        ArgumentNullException.ThrowIfNull(sourceBitmap);

        int w = sourceBitmap.Width;
        int h = sourceBitmap.Height;
        if (w <= 0 || h <= 0)
        {
            return SKColors.White;
        }

        long sumR = 0;
        long sumG = 0;
        long sumB = 0;
        int count = 0;

        int stripX = Math.Max(1, w / 24);
        int stripY = Math.Max(1, h / 24);
        int step = Math.Max(1, Math.Min(w, h) / 64);

        for (int y = 0; y < h; y += step)
        {
            bool isTopStrip = y < stripY;
            for (int x = 0; x < w; x += step)
            {
                bool isSideUpper = y < (int)(h * 0.75) && (x < stripX || x >= w - stripX);
                if (!isTopStrip && !isSideUpper)
                {
                    continue;
                }

                SKColor c = sourceBitmap.GetPixel(x, y);
                if (c.Alpha < 16)
                {
                    continue;
                }

                sumR += c.Red;
                sumG += c.Green;
                sumB += c.Blue;
                count++;
            }
        }

        if (count == 0)
        {
            return SKColors.White;
        }

        return new SKColor(
            (byte)(sumR / count),
            (byte)(sumG / count),
            (byte)(sumB / count),
            255);
    }

    public static void ApplyAlphaToBitmapInPlace(
        SKBitmap targetPremulRgba,
        SKBitmap originalBitmap,
        ReadOnlySpan<byte> alphaMask,
        SKRectI dirtyRect,
        SKColor? estimatedBackdrop = null,
        bool decontaminateEdges = true)
    {
        ArgumentNullException.ThrowIfNull(targetPremulRgba);
        ArgumentNullException.ThrowIfNull(originalBitmap);

        int w = originalBitmap.Width;
        int h = originalBitmap.Height;
        if (targetPremulRgba.Width != w || targetPremulRgba.Height != h)
        {
            throw new ArgumentException("Target bitmap dimensions must match original bitmap.", nameof(targetPremulRgba));
        }

        if (alphaMask.Length != checked(w * h))
        {
            throw new ArgumentException("Alpha mask length must match bitmap pixel count.", nameof(alphaMask));
        }

        int minX = Math.Clamp(dirtyRect.Left, 0, w);
        int minY = Math.Clamp(dirtyRect.Top, 0, h);
        int maxX = Math.Clamp(dirtyRect.Right, 0, w);
        int maxY = Math.Clamp(dirtyRect.Bottom, 0, h);
        if (minX >= maxX || minY >= maxY)
        {
            return;
        }

        SKColor bg = estimatedBackdrop ?? EstimateBorderBackdropColor(originalBitmap);
        SKBitmap tempOriginalRgba = EnsureUnpremulRgba8888(originalBitmap, out bool ownsOriginalCopy);
        try
        {
            ReadOnlySpan<byte> srcBytes = tempOriginalRgba.GetPixelSpan();
            using SKPixmap dstPixmap = targetPremulRgba.PeekPixels()
                ?? throw new InvalidOperationException("Target bitmap pixels are not accessible.");
            IntPtr dstPtr = dstPixmap.GetPixels();
            bool targetPremul = targetPremulRgba.AlphaType == SKAlphaType.Premul;

            int srcRowBytes = tempOriginalRgba.RowBytes;
            int dstRowBytes = dstPixmap.RowBytes;
            int patchCols = maxX - minX;
            byte[] rowBuffer = new byte[patchCols * 4];

            for (int y = minY; y < maxY; y++)
            {
                int srcRow = y * srcRowBytes;
                int alphaRow = y * w;

                for (int i = 0; i < patchCols; i++)
                {
                    int x = minX + i;
                    int srcIdx = srcRow + (x * 4);
                    int dstIdx = i * 4;
                    byte maskAlpha = alphaMask[alphaRow + x];
                    byte srcAlpha = srcBytes[srcIdx + 3];
                    byte a = (byte)((maskAlpha * srcAlpha + 127) / 255);

                    byte r = srcBytes[srcIdx];
                    byte g = srcBytes[srcIdx + 1];
                    byte b = srcBytes[srcIdx + 2];

                    if (decontaminateEdges && a is >= 8 and <= 245)
                    {
                        // Pull color slightly away from border backdrop on hair fringe pixels.
                        float af = a / 255f;
                        float inv = 1f - af;
                        float pull = Math.Clamp(inv * 0.45f / Math.Max(0.25f, af), 0f, 0.55f);
                        r = (byte)Math.Clamp((int)MathF.Round(r + ((r - bg.Red) * pull)), 0, 255);
                        g = (byte)Math.Clamp((int)MathF.Round(g + ((g - bg.Green) * pull)), 0, 255);
                        b = (byte)Math.Clamp((int)MathF.Round(b + ((b - bg.Blue) * pull)), 0, 255);
                    }

                    if (a == 0)
                    {
                        rowBuffer[dstIdx] = 0;
                        rowBuffer[dstIdx + 1] = 0;
                        rowBuffer[dstIdx + 2] = 0;
                        rowBuffer[dstIdx + 3] = 0;
                    }
                    else if (targetPremul && a < 255)
                    {
                        rowBuffer[dstIdx] = (byte)((r * a + 127) / 255);
                        rowBuffer[dstIdx + 1] = (byte)((g * a + 127) / 255);
                        rowBuffer[dstIdx + 2] = (byte)((b * a + 127) / 255);
                        rowBuffer[dstIdx + 3] = a;
                    }
                    else
                    {
                        rowBuffer[dstIdx] = r;
                        rowBuffer[dstIdx + 1] = g;
                        rowBuffer[dstIdx + 2] = b;
                        rowBuffer[dstIdx + 3] = a;
                    }
                }

                IntPtr rowDest = IntPtr.Add(dstPtr, (y * dstRowBytes) + (minX * 4));
                Marshal.Copy(rowBuffer, 0, rowDest, rowBuffer.Length);
            }

            targetPremulRgba.NotifyPixelsChanged();
        }
        finally
        {
            if (ownsOriginalCopy)
            {
                tempOriginalRgba.Dispose();
            }
        }
    }

    public static byte[] EncodeCutoutPngBytes(
        SKBitmap originalBitmap,
        ReadOnlySpan<byte> alphaMask,
        SKColor? estimatedBackdrop = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalBitmap);
        int w = originalBitmap.Width;
        int h = originalBitmap.Height;
        if (alphaMask.Length != checked(w * h))
        {
            throw new ArgumentException("Alpha mask length must match bitmap pixel count.", nameof(alphaMask));
        }

        using var cutoutBitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        const int stripRows = 32;
        for (int y = 0; y < h; y += stripRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int endY = Math.Min(h, y + stripRows);
            ApplyAlphaToBitmapInPlace(
                cutoutBitmap,
                originalBitmap,
                alphaMask,
                new SKRectI(0, y, w, endY),
                estimatedBackdrop,
                decontaminateEdges: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using SKImage image = SKImage.FromBitmap(cutoutBitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] ExtractAlphaChannel(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        int w = bitmap.Width;
        int h = bitmap.Height;
        byte[] alpha = new byte[checked(w * h)];

        SKBitmap rgba = EnsureUnpremulRgba8888(bitmap, out bool ownsCopy);
        try
        {
            ReadOnlySpan<byte> bytes = rgba.GetPixelSpan();
            int rowBytes = rgba.RowBytes;
            for (int y = 0; y < h; y++)
            {
                int rowStart = y * rowBytes;
                int outRow = y * w;
                for (int x = 0; x < w; x++)
                {
                    alpha[outRow + x] = bytes[rowStart + (x * 4) + 3];
                }
            }
        }
        finally
        {
            if (ownsCopy)
            {
                rgba.Dispose();
            }
        }

        return alpha;
    }

    public static void RenderPreview(
        SKCanvas canvas,
        SKRect destRect,
        SKBitmap originalBitmap,
        SKBitmap cutoutBitmap,
        MaskPreviewMode mode,
        float splitRatio)
    {
        ArgumentNullException.ThrowIfNull(originalBitmap);
        ArgumentNullException.ThrowIfNull(cutoutBitmap);
        using SKImage originalImage = SKImage.FromBitmap(originalBitmap);
        using SKImage cutoutImage = SKImage.FromBitmap(cutoutBitmap);
        RenderPreview(canvas, destRect, originalImage, cutoutImage, mode, splitRatio);
    }

    public static void RenderPreview(
        SKCanvas canvas,
        SKRect destRect,
        SKImage originalImage,
        SKImage cutoutImage,
        MaskPreviewMode mode,
        float splitRatio)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(originalImage);
        ArgumentNullException.ThrowIfNull(cutoutImage);

        if (destRect.Width <= 0f || destRect.Height <= 0f)
        {
            return;
        }

        DrawCheckerboard(canvas, destRect);

        using var imagePaint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
        };

        switch (mode)
        {
            case MaskPreviewMode.BeforeOriginal:
                canvas.DrawImage(originalImage, destRect, imagePaint);
                break;

            case MaskPreviewMode.AfterCheckerboard:
                canvas.DrawImage(cutoutImage, destRect, imagePaint);
                break;

            case MaskPreviewMode.SplitBeforeAfter:
                {
                    float ratio = Math.Clamp(splitRatio, 0.02f, 0.98f);
                    float splitX = destRect.Left + (destRect.Width * ratio);

                    int beforeSave = canvas.Save();
                    canvas.ClipRect(new SKRect(destRect.Left, destRect.Top, splitX, destRect.Bottom));
                    canvas.DrawImage(originalImage, destRect, imagePaint);
                    canvas.RestoreToCount(beforeSave);

                    int afterSave = canvas.Save();
                    canvas.ClipRect(new SKRect(splitX, destRect.Top, destRect.Right, destRect.Bottom));
                    canvas.DrawImage(cutoutImage, destRect, imagePaint);
                    canvas.RestoreToCount(afterSave);

                    using var linePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = new SKColor(255, 255, 255, 235),
                        StrokeWidth = 2.0f,
                        IsAntialias = true,
                    };
                    canvas.DrawLine(splitX, destRect.Top, splitX, destRect.Bottom, linePaint);

                    float knobY = destRect.MidY;
                    using var knobFill = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = new SKColor(0, 102, 204, 240),
                        IsAntialias = true,
                    };
                    canvas.DrawCircle(splitX, knobY, 9f, knobFill);
                    canvas.DrawCircle(splitX, knobY, 9f, linePaint);
                    break;
                }

            case MaskPreviewMode.RubyOverlay:
                {
                    canvas.DrawImage(originalImage, destRect, imagePaint);
                    using var rubyTint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = new SKColor(215, 0, 21, 128),
                        IsAntialias = false,
                    };
                    canvas.DrawRect(destRect, rubyTint);
                    canvas.DrawImage(cutoutImage, destRect, imagePaint);
                    break;
                }
        }
    }

    private static void DrawCheckerboard(SKCanvas canvas, SKRect destRect)
    {
        int save = canvas.Save();
        try
        {
            canvas.ClipRect(destRect);
            using var light = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(228, 228, 232, 255) };
            using var dark = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(196, 196, 202, 255) };
            canvas.DrawRect(destRect, light);

            const float cell = 10f;
            int cols = (int)MathF.Ceiling(destRect.Width / cell);
            int rows = (int)MathF.Ceiling(destRect.Height / cell);
            for (int r = 0; r < rows; r++)
            {
                for (int c = (r & 1); c < cols; c += 2)
                {
                    var sq = SKRect.Create(destRect.Left + (c * cell), destRect.Top + (r * cell), cell, cell);
                    canvas.DrawRect(sq, dark);
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    private static SKBitmap EnsureUnpremulRgba8888(SKBitmap source, out bool ownsCopy)
    {
        if (source.ColorType == SKColorType.Rgba8888 && source.AlphaType == SKAlphaType.Unpremul)
        {
            ownsCopy = false;
            return source;
        }

        var copy = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!source.CopyTo(copy, SKColorType.Rgba8888))
        {
            using var canvas = new SKCanvas(copy);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0f, 0f);
        }

        ownsCopy = true;
        return copy;
    }

    public static SKBitmap CreatePremulCopy(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!source.CopyTo(copy, SKColorType.Rgba8888))
        {
            using var canvas = new SKCanvas(copy);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0f, 0f);
        }

        return copy;
    }

    public static void NormalizeToBuffer(ReadOnlySpan<byte> sourceAlpha, Span<byte> destAlpha)
    {
        sourceAlpha.CopyTo(destAlpha);
    }

    public static SKBitmap BuildCutoutPreviewBitmap(SKBitmap originalBitmap, ReadOnlySpan<byte> alphaMask, SKColor? estimatedBackdrop = null)
    {
        ArgumentNullException.ThrowIfNull(originalBitmap);
        var target = new SKBitmap(new SKImageInfo(originalBitmap.Width, originalBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        ApplyAlphaToBitmapInPlace(
            target,
            originalBitmap,
            alphaMask,
            new SKRectI(0, 0, originalBitmap.Width, originalBitmap.Height),
            estimatedBackdrop,
            decontaminateEdges: true);
        return target;
    }

    public static bool HasTransparentPixels(ReadOnlySpan<byte> alphaMask, byte threshold = 240)
    {
        for (int i = 0; i < alphaMask.Length; i++)
        {
            if (alphaMask[i] < threshold)
            {
                return true;
            }
        }

        return false;
    }

    public static SKBitmap DecodeNormalizedBytesToBitmap(ReadOnlySpan<byte> encodedBytes, int? maxEdgePx = null)
    {
        if (encodedBytes.IsEmpty)
        {
            throw new ArgumentException("Encoded bytes cannot be empty.", nameof(encodedBytes));
        }

        using SKData data = SKData.CreateCopy(encodedBytes.ToArray());
        using SKCodec codec = SKCodec.Create(data) ?? throw new InvalidDataException("Invalid raster image bytes.");
        SKImageInfo info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul);
        using var decoded = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"Failed to decode raster image ({result}).");
        }

        SKEncodedOrigin origin = codec.EncodedOrigin;
        int srcW = decoded.Width;
        int srcH = decoded.Height;
        int targetW = srcW;
        int targetH = srcH;
        if (maxEdgePx is int maxEdge && maxEdge > 0 && Math.Max(srcW, srcH) > maxEdge)
        {
            double scale = (double)maxEdge / Math.Max(srcW, srcH);
            targetW = Math.Max(1, (int)Math.Round(srcW * scale));
            targetH = Math.Max(1, (int)Math.Round(srcH * scale));
        }

        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            if (targetW == srcW && targetH == srcH)
            {
                return EnsureUnpremulRgba8888(decoded, out bool ownsCopy) is var bmp && ownsCopy
                    ? bmp
                    : decoded.Copy() ?? throw new InvalidOperationException("Failed to copy bitmap.");
            }

            return decoded.Resize(new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High)
                ?? throw new InvalidOperationException("Failed to resize bitmap.");
        }

        bool swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int outW = swaps ? targetH : targetW;
        int outH = swaps ? targetW : targetH;
        var oriented = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(oriented))
        {
            canvas.Clear(SKColors.Transparent);
            SKMatrix m = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1f, 0f, targetW, 0f, 1f, 0f, 0f, 0f, 1f),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1f, 0f, targetW, 0f, -1f, targetH, 0f, 0f, 1f),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1f, 0f, 0f, 0f, -1f, targetH, 0f, 0f, 1f),
                SKEncodedOrigin.LeftTop => new SKMatrix(0f, 1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f),
                SKEncodedOrigin.RightTop => new SKMatrix(0f, -1f, targetH, 1f, 0f, 0f, 0f, 0f, 1f),
                SKEncodedOrigin.RightBottom => new SKMatrix(0f, -1f, targetH, -1f, 0f, targetW, 0f, 0f, 1f),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0f, 1f, 0f, -1f, 0f, targetW, 0f, 0f, 1f),
                _ => SKMatrix.Identity,
            };

            if (targetW != srcW || targetH != srcH)
            {
                m = m.PreConcat(SKMatrix.CreateScale((float)targetW / srcW, (float)targetH / srcH));
            }

            canvas.SetMatrix(m);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(decoded, 0f, 0f, paint);
        }

        return oriented;
    }

    public static void ReadRgbaPixels(SKBitmap bitmap, Span<byte> destinationRgba)
    {
        SKBitmap rgba = EnsureUnpremulRgba8888(bitmap, out bool ownsCopy);
        try
        {
            ReadOnlySpan<byte> src = rgba.GetPixelSpan();
            int widthBytes = bitmap.Width * 4;
            int rowBytes = rgba.RowBytes;
            for (int y = 0; y < bitmap.Height; y++)
            {
                src.Slice(y * rowBytes, widthBytes).CopyTo(destinationRgba.Slice(y * widthBytes, widthBytes));
            }
        }
        finally
        {
            if (ownsCopy)
            {
                rgba.Dispose();
            }
        }
    }
}
