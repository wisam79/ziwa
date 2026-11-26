using Ziwa.Domain.Ai;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Abstractions;

public sealed record CloudAuthSessionSnapshot(
    string AccessToken,
    string? UserId = null,
    string? Email = null,
    string Plan = "free")
{
    public string UserIdentifier => !string.IsNullOrWhiteSpace(UserId)
        ? UserId!
        : Email ?? string.Empty;
}

public interface ICloudAuthSessionProvider
{
    event Action? SessionChanged;
    CloudAuthSessionSnapshot? GetCurrentSession();
}

public interface ICloudAuthSessionStore : ICloudAuthSessionProvider
{
    void SetSession(CloudAuthSessionSnapshot? session);
    void ClearSession();
}

public sealed record CloudUpscaleResult(
    AssetId UpscaledAssetId,
    int SourceWidthPx,
    int SourceHeightPx,
    int OutputWidthPx,
    int OutputHeightPx,
    double ExecutionSeconds,
    string RequestId,
    CloudAiQuotaSnapshot? ServerQuotaSnapshot);

public sealed class CloudAiException : Exception
{
    public CloudAiException(CloudAiErrorCode errorCode, string messageAr, Exception? innerException = null)
        : base(messageAr, innerException)
    {
        ErrorCode = errorCode;
    }

    public CloudAiErrorCode ErrorCode { get; }
}

public interface IModalAiUpscaler
{
    Task<CloudUpscaleResult> EnhanceImageAsync(
        AssetId sourceAssetId,
        CloudAuthSessionSnapshot session,
        double fidelity = 0.7,
        CancellationToken cancellationToken = default);
}
