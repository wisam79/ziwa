using System.Globalization;
using Ziwa.Domain.Math;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Validation;

namespace Ziwa.Domain.Ai;

public enum CloudAiErrorCode
{
    None = 0,
    Unauthenticated = 1,
    InvalidInput = 2,
    DimensionsTooLarge = 3,
    PayloadTooLarge = 4,
    QuotaExceeded = 5,
    DuplicateRequestConflict = 6,
    Unauthorized = 7,
    EndpointNotFound = 8,
    ServiceUnavailable = 9,
    UnrecordedUsage = 10,
    Timeout = 11,
    NetworkOrServerError = 12,
}

public sealed record CloudAiQuotaSnapshot(
    int UsedToday,
    int DailyLimit,
    DateTimeOffset CapturedAtUtc,
    string? UserIdentifier = null);

public sealed record ResolvedCloudAiQuota(
    int Used,
    int Limit,
    int Remaining,
    bool IsServerVerified);

public static class CloudAiQuotaPolicy
{
    public const int FreeDailyLimit = 5;
    public const int ProDailyLimit = 15;
    public const int EnterpriseDailyLimit = 50;

    public static int GetPlanDailyLimit(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return FreeDailyLimit;
        }

        return plan.Trim().ToLowerInvariant() switch
        {
            "enterprise" => EnterpriseDailyLimit,
            "pro" => ProDailyLimit,
            _ => FreeDailyLimit,
        };
    }

    public static string GetUtcDayKey(DateTimeOffset timestampUtc) =>
        timestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static ResolvedCloudAiQuota ResolveQuota(
        CloudAiQuotaSnapshot? snapshot,
        string? plan,
        string? userIdentifier,
        int localUsedTodayUtc = 0,
        DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset effectiveNow = nowUtc ?? DateTimeOffset.UtcNow;
        string currentUtcDay = GetUtcDayKey(effectiveNow);
        int expectedLimit = GetPlanDailyLimit(plan);

        bool isPlanMatch = string.IsNullOrWhiteSpace(plan)
            ? true
            : snapshot is not null && snapshot.DailyLimit == expectedLimit;
        bool isUserMatch = string.IsNullOrWhiteSpace(snapshot?.UserIdentifier)
            || string.IsNullOrWhiteSpace(userIdentifier)
            || string.Equals(snapshot.UserIdentifier, userIdentifier, StringComparison.OrdinalIgnoreCase);

        if (snapshot is not null &&
            snapshot.DailyLimit > 0 &&
            snapshot.UsedToday >= 0 &&
            isPlanMatch &&
            isUserMatch &&
            GetUtcDayKey(snapshot.CapturedAtUtc) == currentUtcDay)
        {
            int used = System.Math.Max(0, snapshot.UsedToday);
            int limit = snapshot.DailyLimit;
            return new ResolvedCloudAiQuota(
                Used: used,
                Limit: limit,
                Remaining: System.Math.Max(0, limit - used),
                IsServerVerified: true);
        }

        int fallbackUsed = System.Math.Max(0, localUsedTodayUtc);
        return new ResolvedCloudAiQuota(
            Used: fallbackUsed,
            Limit: expectedLimit,
            Remaining: System.Math.Max(0, expectedLimit - fallbackUsed),
            IsServerVerified: false);
    }
}

public static class CropCalibrationMath
{
    public static CropSpec RecalibrateForResizedAsset(
        CropSpec currentCrop,
        int sourceWidthPx,
        int sourceHeightPx,
        int resultWidthPx,
        int resultHeightPx,
        double frameWidthMm,
        double frameHeightMm)
    {
        if (sourceWidthPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidthPx), "Source width must be positive.");
        }

        if (sourceHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceHeightPx), "Source height must be positive.");
        }

        if (resultWidthPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultWidthPx), "Result width must be positive.");
        }

        if (resultHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultHeightPx), "Result height must be positive.");
        }

        SchemaValidator.AssertFinitePositive(frameWidthMm, nameof(frameWidthMm));
        SchemaValidator.AssertFinitePositive(frameHeightMm, nameof(frameHeightMm));

        double zoom = DomainMath.Clamp(currentCrop.Zoom, 1.0, 20.0);
        double panX = DomainMath.Clamp(currentCrop.PanX, -1.0, 1.0);
        double panY = DomainMath.Clamp(currentCrop.PanY, -1.0, 1.0);

        if ((long)sourceWidthPx * resultHeightPx == (long)resultWidthPx * sourceHeightPx)
        {
            return new CropSpec(zoom, panX, panY);
        }

        double slotAspect = frameWidthMm / frameHeightMm;
        (double srcBaseW, double srcBaseH) = ComputeBaseCoverWindow(sourceWidthPx, sourceHeightPx, slotAspect);
        double srcCropW = srcBaseW / zoom;
        double srcCropH = srcBaseH / zoom;
        double srcMaxPanX = System.Math.Max(0.0, (sourceWidthPx - srcCropW) * 0.5);
        double srcMaxPanY = System.Math.Max(0.0, (sourceHeightPx - srcCropH) * 0.5);
        double centerXNorm = ((sourceWidthPx * 0.5) + (panX * srcMaxPanX)) / sourceWidthPx;
        double centerYNorm = ((sourceHeightPx * 0.5) + (panY * srcMaxPanY)) / sourceHeightPx;
        double cropAreaFrac = (srcCropW / sourceWidthPx) * (srcCropH / sourceHeightPx);

        (double dstBaseW, double dstBaseH) = ComputeBaseCoverWindow(resultWidthPx, resultHeightPx, slotAspect);
        double baseAreaFrac = (dstBaseW / resultWidthPx) * (dstBaseH / resultHeightPx);
        double nextZoom = cropAreaFrac > 1e-9
            ? DomainMath.Clamp(System.Math.Sqrt(baseAreaFrac / cropAreaFrac), 1.0, 20.0)
            : zoom;

        double dstCropW = dstBaseW / nextZoom;
        double dstCropH = dstBaseH / nextZoom;
        double dstMaxPanX = System.Math.Max(0.0, (resultWidthPx - dstCropW) * 0.5);
        double dstMaxPanY = System.Math.Max(0.0, (resultHeightPx - dstCropH) * 0.5);

        double nextPanX = dstMaxPanX > 1e-6
            ? DomainMath.Clamp(((centerXNorm * resultWidthPx) - (resultWidthPx * 0.5)) / dstMaxPanX, -1.0, 1.0)
            : 0.0;
        double nextPanY = dstMaxPanY > 1e-6
            ? DomainMath.Clamp(((centerYNorm * resultHeightPx) - (resultHeightPx * 0.5)) / dstMaxPanY, -1.0, 1.0)
            : 0.0;

        return new CropSpec(nextZoom, nextPanX, nextPanY);
    }

    private static (double BaseW, double BaseH) ComputeBaseCoverWindow(int imageW, int imageH, double slotAspect)
    {
        double imageAspect = (double)imageW / imageH;
        return imageAspect > slotAspect
            ? (imageH * slotAspect, imageH)
            : (imageW, imageW / slotAspect);
    }
}
