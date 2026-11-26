using Xunit;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Primitives;

namespace Ziwa.Domain.Tests;

public sealed class CloudAiDomainTests
{
    [Fact]
    public void CloudAiQuotaPolicy_MapsPlansAndEnforcesUtcDayAndIdentityGuards()
    {
        Assert.Equal(5, CloudAiQuotaPolicy.GetPlanDailyLimit(null));
        Assert.Equal(5, CloudAiQuotaPolicy.GetPlanDailyLimit("free"));
        Assert.Equal(15, CloudAiQuotaPolicy.GetPlanDailyLimit("PRO"));
        Assert.Equal(50, CloudAiQuotaPolicy.GetPlanDailyLimit("enterprise"));

        var utcNow = new DateTimeOffset(2026, 11, 26, 22, 15, 0, TimeSpan.Zero);
        var validSnapshot = new CloudAiQuotaSnapshot(3, 15, utcNow.AddHours(-2), "user-123");

        ResolvedCloudAiQuota resolved = CloudAiQuotaPolicy.ResolveQuota(
            validSnapshot,
            plan: "pro",
            userIdentifier: "user-123",
            localUsedTodayUtc: 1,
            nowUtc: utcNow);

        Assert.True(resolved.IsServerVerified);
        Assert.Equal(3, resolved.Used);
        Assert.Equal(15, resolved.Limit);
        Assert.Equal(12, resolved.Remaining);

        // Plan upgraded (pro -> enterprise) invalidates stale 15-limit snapshot immediately
        ResolvedCloudAiQuota upgraded = CloudAiQuotaPolicy.ResolveQuota(
            validSnapshot,
            plan: "enterprise",
            userIdentifier: "user-123",
            localUsedTodayUtc: 3,
            nowUtc: utcNow);
        Assert.False(upgraded.IsServerVerified);
        Assert.Equal(50, upgraded.Limit);
        Assert.Equal(47, upgraded.Remaining);

        // User switch invalidates previous user's snapshot immediately
        ResolvedCloudAiQuota switchedUser = CloudAiQuotaPolicy.ResolveQuota(
            validSnapshot,
            plan: "pro",
            userIdentifier: "user-999",
            localUsedTodayUtc: 0,
            nowUtc: utcNow);
        Assert.False(switchedUser.IsServerVerified);
        Assert.Equal(0, switchedUser.Used);
        Assert.Equal(15, switchedUser.Remaining);

        // Next UTC day rollover invalidates yesterday's snapshot
        var nextUtcDay = new DateTimeOffset(2026, 11, 27, 0, 5, 0, TimeSpan.Zero);
        ResolvedCloudAiQuota rolledOver = CloudAiQuotaPolicy.ResolveQuota(
            validSnapshot,
            plan: "pro",
            userIdentifier: "user-123",
            localUsedTodayUtc: 0,
            nowUtc: nextUtcDay);
        Assert.False(rolledOver.IsServerVerified);
        Assert.Equal(0, rolledOver.Used);
        Assert.Equal(15, rolledOver.Remaining);
    }

    [Fact]
    public void CropCalibrationMath_PreservesCropAcrossExact2xAndRoundedAspectChanges()
    {
        var originalCrop = new CropSpec(Zoom: 1.75, PanX: -0.35, PanY: 0.42);

        // Exact 2x upscale preserves normalized crop parameters identically
        CropSpec exact2x = CropCalibrationMath.RecalibrateForResizedAsset(
            originalCrop,
            sourceWidthPx: 600,
            sourceHeightPx: 800,
            resultWidthPx: 1200,
            resultHeightPx: 1600,
            frameWidthMm: 35.0,
            frameHeightMm: 45.0);

        Assert.Equal(1.75, exact2x.Zoom, precision: 6);
        Assert.Equal(-0.35, exact2x.PanX, precision: 6);
        Assert.Equal(0.42, exact2x.PanY, precision: 6);

        // Slight integer aspect change remains finite, clamped, and close to source zoom/pan
        CropSpec rounded = CropCalibrationMath.RecalibrateForResizedAsset(
            originalCrop,
            sourceWidthPx: 601,
            sourceHeightPx: 800,
            resultWidthPx: 1200,
            resultHeightPx: 1600,
            frameWidthMm: 35.0,
            frameHeightMm: 45.0);

        Assert.InRange(rounded.Zoom, 1.70, 1.80);
        Assert.InRange(rounded.PanX, -0.45, -0.25);
        Assert.InRange(rounded.PanY, 0.35, 0.49);
    }
}
