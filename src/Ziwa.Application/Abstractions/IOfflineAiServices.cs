using Ziwa.Domain.Ai;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Abstractions;

public sealed record MaskEstimationResult(
    AssetId CutoutAssetId,
    int WidthPx,
    int HeightPx,
    byte[] AlphaMask,
    bool UsedExternalModel);

public sealed record MaskEditorPreparedSession(
    AssetId OriginalAssetId,
    AssetId CurrentAssetId,
    int WidthPx,
    int HeightPx,
    byte[] CurrentAlphaMask,
    byte[] AutoAiAlphaMask,
    bool UsedExternalModel);

public interface IBackgroundRemovalService
{
    Task<MaskEstimationResult> RemoveBackgroundAsync(
        AssetId originalAssetId,
        CancellationToken cancellationToken = default);

    Task<MaskEditorPreparedSession> PrepareMaskEditorAsync(
        AssetId originalAssetId,
        AssetId currentAssetId,
        CancellationToken cancellationToken = default);

    Task<AssetId> CommitEditedMaskAsync(
        AssetId originalAssetId,
        int widthPx,
        int heightPx,
        ReadOnlyMemory<byte> editedAlphaMask,
        CancellationToken cancellationToken = default);
}

public sealed record FaceDetectionOutcome(
    bool FaceFound,
    FaceRectPx? FaceBox,
    int ImageWidthPx,
    int ImageHeightPx,
    bool UsedExternalModel);

public interface IFaceFramingService
{
    Task<FaceDetectionOutcome> DetectPrimaryFaceAsync(
        AssetId assetId,
        int quarterTurns = 0,
        bool flipHorizontal = false,
        bool flipVertical = false,
        CancellationToken cancellationToken = default);
}

public interface IImageAssetImporter
{
    Task<AssetId> ImportImageFileAsync(string filePath, CancellationToken cancellationToken = default);
}
