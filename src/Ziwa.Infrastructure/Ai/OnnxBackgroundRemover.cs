using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Ai;
using Ziwa.Rendering.Assets;

namespace Ziwa.Infrastructure.Ai;

public sealed class OnnxBackgroundRemover : IBackgroundRemovalService, IDisposable
{
    private readonly AssetStore _assetStore;
    private readonly OfflineAiOptions _options;
    private readonly object _sessionLock = new();
    private InferenceSession? _embeddedSession;
    private InferenceSession? _externalSession;
    private string? _externalSessionPath;
    private bool _disposed;

    public OnnxBackgroundRemover(AssetStore assetStore, OfflineAiOptions options)
    {
        _assetStore = assetStore;
        _options = options;
    }

    public async Task<MaskEstimationResult> RemoveBackgroundAsync(
        AssetId originalAssetId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_assetStore.TryGetEncodedBytes(originalAssetId, out byte[]? encodedBytes) || encodedBytes is null)
        {
            throw new InvalidDataException($"Original asset '{originalAssetId.Value}' was not found in AssetStore.");
        }

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.InferenceStepDelayMs > 0)
            {
                await Task.Delay(_options.InferenceStepDelayMs, cancellationToken).ConfigureAwait(false);
            }

            using SKBitmap originalFull = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(encodedBytes);
            int fullW = originalFull.Width;
            int fullH = originalFull.Height;
            byte[] fullRgba = new byte[checked(fullW * fullH * 4)];
            AlphaMaskCompositor.ReadRgbaPixels(originalFull, fullRgba);

            HairDetailAlphaMatting.BackdropStatistics backdrop = HairDetailAlphaMatting.EstimateBorderBackdrop(fullRgba, fullW, fullH);

            (InferenceSession session, bool usedExternal) = ResolveSession();
            (float[] coarseProb, int infW, int infH) = RunInference(session, usedExternal, originalFull, backdrop, cancellationToken);

            byte[] refinedAlpha = HairDetailAlphaMatting.RefineAlphaWithHairMatting(
                fullRgba,
                fullW,
                fullH,
                coarseProb,
                infW,
                infH,
                backdrop,
                cancellationToken);

            byte[] pngBytes = AlphaMaskCompositor.EncodeCutoutPngBytes(
                originalFull,
                refinedAlpha,
                HairDetailAlphaMatting.ToSkColor(backdrop),
                cancellationToken);

            AssetId cutoutAssetId = _assetStore.RegisterAsset(pngBytes);
            return new MaskEstimationResult(cutoutAssetId, fullW, fullH, refinedAlpha, usedExternal);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MaskEditorPreparedSession> PrepareMaskEditorAsync(
        AssetId originalAssetId,
        AssetId currentAssetId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        MaskEstimationResult autoAi = await RemoveBackgroundAsync(originalAssetId, cancellationToken).ConfigureAwait(false);

        if (currentAssetId == originalAssetId ||
            !_assetStore.TryGetEncodedBytes(currentAssetId, out byte[]? currentBytes) ||
            currentBytes is null)
        {
            return new MaskEditorPreparedSession(
                originalAssetId,
                autoAi.CutoutAssetId,
                autoAi.WidthPx,
                autoAi.HeightPx,
                autoAi.AlphaMask,
                (byte[])autoAi.AlphaMask.Clone(),
                autoAi.UsedExternalModel);
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SKBitmap currentBmp = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(currentBytes);
            byte[] extractedAlpha = AlphaMaskCompositor.ExtractAlphaChannel(currentBmp);

            bool hasTransparency = AlphaMaskCompositor.HasTransparentPixels(extractedAlpha);
            if (!hasTransparency || currentBmp.Width != autoAi.WidthPx || currentBmp.Height != autoAi.HeightPx)
            {
                return new MaskEditorPreparedSession(
                    originalAssetId,
                    autoAi.CutoutAssetId,
                    autoAi.WidthPx,
                    autoAi.HeightPx,
                    autoAi.AlphaMask,
                    (byte[])autoAi.AlphaMask.Clone(),
                    autoAi.UsedExternalModel);
            }

            return new MaskEditorPreparedSession(
                originalAssetId,
                currentAssetId,
                autoAi.WidthPx,
                autoAi.HeightPx,
                extractedAlpha,
                autoAi.AlphaMask,
                autoAi.UsedExternalModel);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetId> CommitEditedMaskAsync(
        AssetId originalAssetId,
        int widthPx,
        int heightPx,
        ReadOnlyMemory<byte> editedAlphaMask,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_assetStore.TryGetEncodedBytes(originalAssetId, out byte[]? originalBytes) || originalBytes is null)
        {
            throw new InvalidDataException($"Original asset '{originalAssetId.Value}' was not found in AssetStore.");
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SKBitmap originalFull = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(originalBytes);
            if (originalFull.Width != widthPx || originalFull.Height != heightPx)
            {
                throw new InvalidDataException(
                    $"Mask dimensions ({widthPx}x{heightPx}) do not match original asset ({originalFull.Width}x{originalFull.Height}).");
            }

            byte[] cutoutBytes = AlphaMaskCompositor.EncodeCutoutPngBytes(
                originalFull,
                editedAlphaMask.Span,
                cancellationToken: cancellationToken);

            return _assetStore.RegisterAsset(cutoutBytes);
        }, cancellationToken).ConfigureAwait(false);
    }

    private (InferenceSession Session, bool UsedExternal) ResolveSession()
    {
        string externalPath = _options.BackgroundModelPath;
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
                _embeddedSession = new InferenceSession(EmbeddedOnnxModelBuilder.BuildForegroundSegmentationModelBytes(), opts);
            }

            return (_embeddedSession, false);
        }
    }

    private (float[] Probabilities, int Width, int Height) RunInference(
        InferenceSession session,
        bool usedExternal,
        SKBitmap originalFull,
        HairDetailAlphaMatting.BackdropStatistics backdrop,
        CancellationToken cancellationToken)
    {
        string inputName = session.InputMetadata.Keys.First();
        NodeMetadata inputMeta = session.InputMetadata[inputName];
        int[] dims = inputMeta.Dimensions;

        int channels = dims.Length >= 2 && dims[1] > 0 ? dims[1] : (usedExternal ? 3 : 5);
        int maxEdge = _options.MaxInferenceEdgePx > 0 ? _options.MaxInferenceEdgePx : 384;

        int infW;
        int infH;
        if (dims.Length >= 4 && dims[2] > 0 && dims[3] > 0)
        {
            infH = dims[2];
            infW = dims[3];
        }
        else
        {
            int longest = Math.Max(originalFull.Width, originalFull.Height);
            double scale = longest > maxEdge ? (double)maxEdge / longest : 1.0;
            infW = Math.Max(16, (int)Math.Round(originalFull.Width * scale));
            infH = Math.Max(16, (int)Math.Round(originalFull.Height * scale));
        }

        using SKBitmap resized = originalFull.Resize(
            new SKImageInfo(infW, infH, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            SKFilterQuality.Medium)
            ?? throw new InvalidOperationException("Failed to resize portrait for ONNX segmentation.");

        byte[] infRgba = new byte[checked(infW * infH * 4)];
        AlphaMaskCompositor.ReadRgbaPixels(resized, infRgba);

        var inputTensor = new DenseTensor<float>([1, channels, infH, infW]);
        if (usedExternal && channels == 3)
        {
            FillExternalRgbTensor(inputTensor, infRgba, infW, infH);
        }
        else
        {
            FillSegmentationFeatureTensor(inputTensor, infRgba, infW, infH, backdrop);
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

            Tensor<float> outTensor = outputs[0].AsTensor<float>();
            int outH = outTensor.Dimensions.Length >= 4 ? outTensor.Dimensions[2] : infH;
            int outW = outTensor.Dimensions.Length >= 4 ? outTensor.Dimensions[3] : infW;
            float[] probs = ExtractNormalizedProbabilityMap(outTensor, outW, outH);
            return (probs, outW, outH);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
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

    private static void FillSegmentationFeatureTensor(
        DenseTensor<float> tensor,
        ReadOnlySpan<byte> rgba,
        int w,
        int h,
        HairDetailAlphaMatting.BackdropStatistics backdrop)
    {
        float bgLuma = (0.299f * backdrop.MeanR) + (0.587f * backdrop.MeanG) + (0.114f * backdrop.MeanB);
        float bgCb = 128f - (0.168736f * backdrop.MeanR) - (0.331264f * backdrop.MeanG) + (0.5f * backdrop.MeanB);
        float bgCr = 128f + (0.5f * backdrop.MeanR) - (0.418688f * backdrop.MeanG) - (0.081312f * backdrop.MeanB);
        float distDenom = Math.Max(32f, backdrop.StdDev * 2.2f);

        for (int y = 0; y < h; y++)
        {
            float ny = h > 1 ? (float)y / (h - 1) : 0.5f;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float nx = w > 1 ? (float)x / (w - 1) : 0.5f;
                int px = (row + x) * 4;
                float r = rgba[px];
                float g = rgba[px + 1];
                float b = rgba[px + 2];

                float dr = r - backdrop.MeanR;
                float dg = g - backdrop.MeanG;
                float db = b - backdrop.MeanB;
                float rgbDist = MathF.Sqrt((dr * dr) + (dg * dg) + (db * db));

                float luma = (0.299f * r) + (0.587f * g) + (0.114f * b);
                float cb = 128f - (0.168736f * r) - (0.331264f * g) + (0.5f * b);
                float cr = 128f + (0.5f * r) - (0.418688f * g) - (0.081312f * b);
                float chromaDist = MathF.Sqrt(((cb - bgCb) * (cb - bgCb)) + ((cr - bgCr) * (cr - bgCr)));

                int xL = Math.Max(0, x - 1);
                int xR = Math.Min(w - 1, x + 1);
                int yU = Math.Max(0, y - 1);
                int yD = Math.Min(h - 1, y + 1);
                int pxL = ((y * w) + xL) * 4;
                int pxR = ((y * w) + xR) * 4;
                int pxU = ((yU * w) + x) * 4;
                int pxD = ((yD * w) + x) * 4;
                float grad = (MathF.Abs(rgba[pxR] - rgba[pxL]) +
                              MathF.Abs(rgba[pxR + 1] - rgba[pxL + 1]) +
                              MathF.Abs(rgba[pxR + 2] - rgba[pxL + 2]) +
                              MathF.Abs(rgba[pxD] - rgba[pxU]) +
                              MathF.Abs(rgba[pxD + 1] - rgba[pxU + 1]) +
                              MathF.Abs(rgba[pxD + 2] - rgba[pxU + 2])) / 6f;

                // Studio portrait spatial prior: zero on top/side outer margins, stronger inside center/lower torso.
                float dxCenter = (nx - 0.5f) / 0.36f;
                float dyCenter = (ny - 0.58f) / 0.44f;
                float portraitPrior = MathF.Exp(-((dxCenter * dxCenter) + (dyCenter * dyCenter)));
                if (ny < 0.04f || nx < 0.04f || nx > 0.96f)
                {
                    portraitPrior *= 0.1f;
                }

                tensor[0, 0, y, x] = Math.Clamp(rgbDist / distDenom, 0f, 3.0f);
                tensor[0, 1, y, x] = Math.Clamp(chromaDist / 26f, 0f, 3.0f);
                tensor[0, 2, y, x] = Math.Clamp(MathF.Abs(luma - bgLuma) / 34f, 0f, 3.0f);
                tensor[0, 3, y, x] = Math.Clamp(grad / 38f, 0f, 2.0f);
                tensor[0, 4, y, x] = portraitPrior;
            }
        }
    }

    private static float[] ExtractNormalizedProbabilityMap(Tensor<float> tensor, int w, int h)
    {
        float[] result = new float[checked(w * h)];
        float minV = float.MaxValue;
        float maxV = float.MinValue;
        bool is4D = tensor.Dimensions.Length >= 4;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float val = is4D ? tensor[0, 0, y, x] : tensor[0, y, x];
                if (!float.IsFinite(val))
                {
                    val = 0f;
                }

                result[row + x] = val;
                if (val < minV)
                {
                    minV = val;
                }

                if (val > maxV)
                {
                    maxV = val;
                }
            }
        }

        if (minV < 0f || maxV > 1f)
        {
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = 1f / (1f + MathF.Exp(-Math.Clamp(result[i], -20f, 20f)));
            }
        }

        return result;
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
