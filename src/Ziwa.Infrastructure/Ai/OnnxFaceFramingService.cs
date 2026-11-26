using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Ai;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Ai;

public sealed class OnnxFaceFramingService : IFaceFramingService, IDisposable
{
    private readonly AssetStore _assetStore;
    private readonly OfflineAiOptions _options;
    private readonly object _sessionLock = new();
    private InferenceSession? _embeddedSession;
    private InferenceSession? _externalSession;
    private string? _externalSessionPath;
    private bool _disposed;

    public OnnxFaceFramingService(AssetStore assetStore, OfflineAiOptions options)
    {
        _assetStore = assetStore;
        _options = options;
    }

    public async Task<FaceDetectionOutcome> DetectPrimaryFaceAsync(
        AssetId assetId,
        int quarterTurns = 0,
        bool flipHorizontal = false,
        bool flipVertical = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_assetStore.TryGetEncodedBytes(assetId, out byte[]? encodedBytes) || encodedBytes is null)
        {
            throw new InvalidDataException($"Asset '{assetId.Value}' was not found in AssetStore.");
        }

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.InferenceStepDelayMs > 0)
            {
                await Task.Delay(_options.InferenceStepDelayMs, cancellationToken).ConfigureAwait(false);
            }

            using SKBitmap decoded = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(encodedBytes);
            using SKBitmap logicalBitmap = ApplySlotOrientation(decoded, quarterTurns, flipHorizontal, flipVertical);
            int fullW = logicalBitmap.Width;
            int fullH = logicalBitmap.Height;

            (InferenceSession session, bool usedExternal) = ResolveSession();
            string inputName = session.InputMetadata.Keys.First();
            NodeMetadata inputMeta = session.InputMetadata[inputName];
            int[] dims = inputMeta.Dimensions;
            int channels = dims.Length >= 2 && dims[1] > 0 ? dims[1] : (usedExternal ? 3 : 4);

            int infW;
            int infH;
            if (dims.Length >= 4 && dims[2] > 0 && dims[3] > 0)
            {
                infW = dims[3];
                infH = dims[2];
            }
            else
            {
                const int targetMaxEdge = 160;
                double scale = Math.Max(fullW, fullH) > targetMaxEdge
                    ? (double)targetMaxEdge / Math.Max(fullW, fullH)
                    : 1.0;
                infW = Math.Max(24, (int)Math.Round(fullW * scale));
                infH = Math.Max(24, (int)Math.Round(fullH * scale));
            }

            using SKBitmap resized = logicalBitmap.Resize(
                new SKImageInfo(infW, infH, SKColorType.Rgba8888, SKAlphaType.Unpremul),
                SKFilterQuality.Medium)
                ?? throw new InvalidOperationException("Failed to resize image for face detection.");

            byte[] infRgba = new byte[checked(infW * infH * 4)];
            AlphaMaskCompositor.ReadRgbaPixels(resized, infRgba);

            var inputTensor = new DenseTensor<float>([1, channels, infH, infW]);
            if (usedExternal && channels == 3)
            {
                FillExternalRgbTensor(inputTensor, infRgba, infW, infH);
            }
            else
            {
                FillFaceFeatureTensor(inputTensor, infRgba, infW, infH);
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var runOptions = new RunOptions();
            using CancellationTokenRegistration cancelReg = cancellationToken.Register(() =>
            {
                try
                {
                    runOptions.Terminate = true;
                }
                catch (ObjectDisposedException)
                {
                }
            });

            try
            {
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(
                    [NamedOnnxValue.CreateFromTensor(inputName, inputTensor)],
                    session.OutputMetadata.Keys.ToArray(),
                    runOptions);

                cancellationToken.ThrowIfCancellationRequested();

                Tensor<float> heatmapTensor = outputs[0].AsTensor<float>();
                int outH = heatmapTensor.Dimensions.Length >= 4 ? heatmapTensor.Dimensions[2] : infH;
                int outW = heatmapTensor.Dimensions.Length >= 4 ? heatmapTensor.Dimensions[3] : infW;
                FaceRectPx? faceBox = ExtractPrimaryFaceBox(heatmapTensor, outW, outH, fullW, fullH);

                return new FaceDetectionOutcome(
                    FaceFound: faceBox is not null,
                    FaceBox: faceBox,
                    ImageWidthPx: fullW,
                    ImageHeightPx: fullH,
                    UsedExternalModel: usedExternal);
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private (InferenceSession Session, bool UsedExternal) ResolveSession()
    {
        string externalPath = _options.FaceDetectorModelPath;
        lock (_sessionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (File.Exists(externalPath))
            {
                if (_externalSession is null || !string.Equals(_externalSessionPath, externalPath, StringComparison.Ordinal))
                {
                    _externalSession?.Dispose();
                    using var opts = CreateSessionOptions();
                    _externalSession = new InferenceSession(externalPath, opts);
                    _externalSessionPath = externalPath;
                }

                return (_externalSession, true);
            }

            if (_embeddedSession is null)
            {
                using var opts = CreateSessionOptions();
                _embeddedSession = new InferenceSession(EmbeddedOnnxModelBuilder.BuildFaceDetectorModelBytes(), opts);
            }

            return (_embeddedSession, false);
        }
    }

    private static void FillExternalRgbTensor(DenseTensor<float> tensor, ReadOnlySpan<byte> rgba, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int px = (row + x) * 4;
                tensor[0, 0, y, x] = rgba[px] / 255f;
                tensor[0, 1, y, x] = rgba[px + 1] / 255f;
                tensor[0, 2, y, x] = rgba[px + 2] / 255f;
            }
        }
    }

    private static void FillFaceFeatureTensor(DenseTensor<float> tensor, ReadOnlySpan<byte> rgba, int w, int h)
    {
        HairDetailAlphaMatting.BackdropStatistics backdrop = HairDetailAlphaMatting.EstimateBorderBackdrop(rgba, w, h);
        float[] luma = new float[w * h];
        float maxChroma = 0f;

        for (int i = 0; i < w * h; i++)
        {
            int px = i * 4;
            float r = rgba[px];
            float g = rgba[px + 1];
            float b = rgba[px + 2];
            luma[i] = (0.299f * r) + (0.587f * g) + (0.114f * b);
            float c = MathF.Max(MathF.Abs(r - g), MathF.Abs(g - b));
            if (c > maxChroma)
            {
                maxChroma = c;
            }
        }

        bool isMonochrome = maxChroma < 12f;
        int symRadius = Math.Clamp(w / 14, 2, 10);
        int eyeStepY = Math.Max(2, h / 28);

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                int px = idx * 4;
                byte a = rgba[px + 3];
                if (a < 24)
                {
                    continue;
                }

                float r = rgba[px];
                float g = rgba[px + 1];
                float b = rgba[px + 2];
                float yVal = luma[idx];

                float skinScore;
                if (isMonochrome)
                {
                    float bgLuma = (0.299f * backdrop.MeanR) + (0.587f * backdrop.MeanG) + (0.114f * backdrop.MeanB);
                    float fgContrast = Math.Clamp(MathF.Abs(yVal - bgLuma) / 36f, 0f, 1.5f);
                    skinScore = yVal is >= 50f and <= 235f ? fgContrast : 0f;
                }
                else
                {
                    float cb = 128f - (0.168736f * r) - (0.331264f * g) + (0.5f * b);
                    float cr = 128f + (0.5f * r) - (0.418688f * g) - (0.081312f * b);
                    bool inSkinBox = yVal > 35f && cb is >= 72f and <= 136f && cr is >= 128f and <= 185f;
                    float distSkin = MathF.Sqrt((((cb - 106f) / 24f) * ((cb - 106f) / 24f)) + (((cr - 150f) / 24f) * ((cr - 150f) / 24f)));
                    skinScore = inSkinBox ? MathF.Exp(-distSkin * 0.65f) * 1.45f : 0f;
                }

                // Facial feature contrast: brow/eye darkness vs cheek/forehead luminance.
                int yAbove = Math.Max(0, y - eyeStepY);
                int yBelow = Math.Min(h - 1, y + eyeStepY);
                float eyeContrast = Math.Clamp(((luma[(yBelow * w) + x] - yVal) + (luma[(yAbove * w) + x] - yVal)) / 32f, 0f, 2f);
                if (eyeContrast < 0.15f)
                {
                    float localRange = MathF.Abs(luma[(yBelow * w) + x] - luma[(yAbove * w) + x]);
                    eyeContrast = Math.Clamp(localRange / 42f, 0f, 1.2f);
                }

                // Local horizontal bilateral symmetry + edge detail.
                float symDiff = 0f;
                for (int dx = 1; dx <= symRadius; dx++)
                {
                    int xL = Math.Max(0, x - dx);
                    int xR = Math.Min(w - 1, x + dx);
                    symDiff += MathF.Abs(luma[row + xL] - luma[row + xR]);
                }

                float avgSymDiff = symDiff / symRadius;
                float symmetry = Math.Clamp(1f - (avgSymDiff / 45f), 0f, 1f);

                int x1 = Math.Max(0, x - 1);
                int x2 = Math.Min(w - 1, x + 1);
                float detail = Math.Clamp((MathF.Abs(luma[row + x2] - luma[row + x1]) +
                                           MathF.Abs(luma[(yBelow * w) + x] - luma[(yAbove * w) + x])) / 24f, 0f, 1.8f);

                // Gate detail & symmetry by skin/portrait likelihood so bright background stripes don't fire.
                float faceMaskGate = skinScore > 0.15f ? 1f : 0f;
                tensor[0, 0, y, x] = skinScore;
                tensor[0, 1, y, x] = eyeContrast * faceMaskGate;
                tensor[0, 2, y, x] = symmetry * faceMaskGate;
                tensor[0, 3, y, x] = detail * faceMaskGate;
            }
        }
    }

    private static FaceRectPx? ExtractPrimaryFaceBox(
        Tensor<float> heatmap,
        int mapW,
        int mapH,
        int fullW,
        int fullH)
    {
        bool is4D = heatmap.Dimensions.Length >= 4;
        float maxScore = 0f;
        int peakX = -1;
        int peakY = -1;

        for (int y = 0; y < mapH; y++)
        {
            for (int x = 0; x < mapW; x++)
            {
                float raw = is4D ? heatmap[0, 0, y, x] : heatmap[0, y, x];
                float prob = raw is >= 0f and <= 1f ? raw : 1f / (1f + MathF.Exp(-Math.Clamp(raw, -20f, 20f)));
                if (prob > maxScore)
                {
                    maxScore = prob;
                    peakX = x;
                    peakY = y;
                }
            }
        }

        if (maxScore < 0.62f || peakX < 0 || peakY < 0)
        {
            return null;
        }

        float threshold = Math.Max(0.45f, maxScore * 0.68f);
        bool[] visited = new bool[mapW * mapH];
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((peakX, peakY));
        visited[(peakY * mapW) + peakX] = true;

        int minX = peakX;
        int maxX = peakX;
        int minY = peakY;
        int maxY = peakY;
        int count = 0;

        while (queue.Count > 0)
        {
            (int cx, int cy) = queue.Dequeue();
            count++;
            if (cx < minX) { minX = cx; }
            if (cx > maxX) { maxX = cx; }
            if (cy < minY) { minY = cy; }
            if (cy > maxY) { maxY = cy; }

            ReadOnlySpan<(int Dx, int Dy)> dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];
            foreach ((int dx, int dy) in dirs)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= mapW || ny < 0 || ny >= mapH)
                {
                    continue;
                }

                int idx = (ny * mapW) + nx;
                if (visited[idx])
                {
                    continue;
                }

                float raw = is4D ? heatmap[0, 0, ny, nx] : heatmap[0, ny, nx];
                float prob = raw is >= 0f and <= 1f ? raw : 1f / (1f + MathF.Exp(-Math.Clamp(raw, -20f, 20f)));
                if (prob >= threshold)
                {
                    visited[idx] = true;
                    queue.Enqueue((nx, ny));
                }
            }
        }

        int minArea = Math.Max(4, (mapW * mapH) / 400);
        if (count < minArea)
        {
            return null;
        }

        double scaleX = (double)fullW / mapW;
        double scaleY = (double)fullH / mapH;
        double boxX = minX * scaleX;
        double boxY = minY * scaleY;
        double boxW = Math.Max(1.0, (maxX - minX + 1) * scaleX);
        double boxH = Math.Max(1.0, (maxY - minY + 1) * scaleY);

        // Enforce realistic portrait face aspect ratio if activation focuses on eyes/center of face.
        if (boxH < boxW * 1.10)
        {
            double targetH = boxW * 1.25;
            double deltaH = targetH - boxH;
            boxY = Math.Max(0.0, boxY - (deltaH * 0.28));
            boxH = Math.Min(fullH - boxY, targetH);
        }

        return new FaceRectPx(boxX, boxY, boxW, boxH, maxScore);
    }

    private static SKBitmap ApplySlotOrientation(SKBitmap src, int quarterTurns, bool flipH, bool flipV)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0 && !flipH && !flipV)
        {
            return src.Copy() ?? throw new InvalidOperationException("Failed to copy bitmap.");
        }

        bool swaps = turns is 1 or 3;
        int outW = swaps ? src.Height : src.Width;
        int outH = swaps ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(outW * 0.5f, outH * 0.5f);
        if (flipH || flipV)
        {
            canvas.Scale(flipH ? -1f : 1f, flipV ? -1f : 1f);
        }

        if (turns != 0)
        {
            canvas.RotateDegrees(turns * 90f);
        }

        canvas.Translate(-src.Width * 0.5f, -src.Height * 0.5f);
        canvas.DrawBitmap(src, 0f, 0f);
        return dst;
    }

    private static SessionOptions CreateSessionOptions() =>
        new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _embeddedSession?.Dispose();
            _embeddedSession = null;
            _externalSession?.Dispose();
            _externalSession = null;
        }
    }
}
