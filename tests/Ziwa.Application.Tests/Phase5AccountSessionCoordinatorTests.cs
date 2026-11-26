using Xunit;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Ai;
using Ziwa.Application.Auth;
using Ziwa.Application.Services;
using Ziwa.Application.Session;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Auth;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Tests;

public sealed class Phase5AccountSessionCoordinatorTests
{
    [Fact]
    public async Task LoginActivationCheckSupersededAndLogout_SynchronizeCloudAiQuotaAndStateCleanly()
    {
        ZiwaDocument doc = SampleVerticalSliceFactory.CreateShowcaseDocument(new AssetId("sample-img"));
        var session = new DocumentSession(doc);
        var authClient = new FakeSupabaseAuthClient();
        var modalStub = new NoopModalUpscaler();
        var utcNow = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        using var cloudCoordinator = new CloudAiCoordinator(session, modalStub, authClient.CloudSessionStore, clock: () => utcNow);
        using var accountCoordinator = new AccountSessionCoordinator(authClient, cloudCoordinator);

        AccountOperationOutcome loginOutcome = await accountCoordinator.LoginWithPasswordAsync("user@ziwa.app", "Pass1234");
        Assert.True(loginOutcome.Succeeded);
        Assert.True(accountCoordinator.IsAuthenticated);
        Assert.Equal(5, cloudCoordinator.CurrentQuota.Limit);
        Assert.Equal(4, cloudCoordinator.CurrentQuota.Remaining);
        Assert.True(cloudCoordinator.CurrentQuota.IsServerVerified);

        // Activating Pro license upgrades cloud quota limit from 5 to 15 immediately
        AccountOperationOutcome licenseOutcome = await accountCoordinator.ActivateLicenseKeyAsync("ZIWA-PRO-KEY");
        Assert.True(licenseOutcome.Succeeded);
        Assert.Equal(15, cloudCoordinator.CurrentQuota.Limit);
        Assert.Equal(14, cloudCoordinator.CurrentQuota.Remaining);

        // Supersession evicts local session & exposes warning banner
        authClient.TriggerSupersedeOnNextCheck = true;
        AccountOperationOutcome checkOutcome = await accountCoordinator.CheckSessionStatusAsync();
        Assert.False(checkOutcome.Succeeded);
        Assert.True(checkOutcome.WasSessionSuperseded);
        Assert.True(accountCoordinator.WasSessionSuperseded);
        Assert.False(accountCoordinator.IsAuthenticated);
        Assert.False(cloudCoordinator.CurrentQuota.IsServerVerified);
        Assert.Equal(5, cloudCoordinator.CurrentQuota.Limit);
    }

    private sealed class FakeSupabaseAuthClient : ISupabaseAuthClient
    {
        private readonly DateTimeOffset _now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        public FakeCloudSessionStore CloudSessionStore { get; } = new();
        public UserAccountProfile CurrentProfile { get; private set; } = UserAccountProfile.Anonymous;
        public bool IsAuthenticated => CurrentProfile.IsAuthenticated;
        public bool IsConfigured => true;
        public bool TriggerSupersedeOnNextCheck { get; set; }

        public event Action? StateChanged;

        public Task<UserAccountProfile> InitializeAsync(CancellationToken ct = default) => Task.FromResult(CurrentProfile);

        public Task<UserAccountProfile> RegisterAsync(string name, string email, string password, CancellationToken ct = default) =>
            Task.FromResult(CurrentProfile);

        public Task<UserAccountProfile> VerifySignupOtpAsync(string email, string otpToken, CancellationToken ct = default) =>
            Task.FromResult(CurrentProfile);

        public Task ResendSignupOtpAsync(string email, CancellationToken ct = default) => Task.CompletedTask;

        public Task<UserAccountProfile> LoginWithPasswordAsync(string email, string password, CancellationToken ct = default)
        {
            CurrentProfile = new UserAccountProfile("u-1", email, "Studio User", "free", "active", null, null, _now);
            CloudSessionStore.SetSession(new CloudAuthSessionSnapshot("jwt-1", "u-1", email, "free"));
            StateChanged?.Invoke();
            return Task.FromResult(CurrentProfile);
        }

        public Task<UserAccountProfile> LoginWithGoogleAsync(CancellationToken ct = default) => Task.FromResult(CurrentProfile);
        public Task RequestPasswordRecoveryAsync(string email, CancellationToken ct = default) => Task.CompletedTask;

        public Task<UserAccountProfile> CompletePasswordRecoveryAsync(string email, string recoveryOtp, string newPassword, CancellationToken ct = default) =>
            Task.FromResult(CurrentProfile);

        public Task<UserAccountProfile> ActivateLicenseKeyAsync(string licenseKey, CancellationToken ct = default)
        {
            CurrentProfile = CurrentProfile with { Plan = "pro", Status = "active", ExpiresAtUtc = _now.AddMonths(12), LicenseKey = licenseKey };
            CloudSessionStore.SetSession(new CloudAuthSessionSnapshot("jwt-1", CurrentProfile.UserId, CurrentProfile.Email, "pro"));
            StateChanged?.Invoke();
            return Task.FromResult(CurrentProfile);
        }

        public Task<UserAccountProfile> CheckSessionStatusAsync(CancellationToken ct = default)
        {
            if (TriggerSupersedeOnNextCheck)
            {
                TriggerSupersedeOnNextCheck = false;
                CurrentProfile = UserAccountProfile.Anonymous;
                CloudSessionStore.ClearSession();
                StateChanged?.Invoke();
                throw new AuthException(AuthErrorCode.SessionSuperseded, $"{SupabaseAuthErrorMapper.SessionSupersededMarker}: تم تسجيل الدخول من جهاز آخر");
            }

            return Task.FromResult(CurrentProfile);
        }

        public Task<CloudAiQuotaSnapshot?> FetchServerDailyQuotaAsync(CancellationToken ct = default)
        {
            int limit = CloudAiQuotaPolicy.GetPlanDailyLimit(CurrentProfile.EffectivePlan(_now));
            return Task.FromResult<CloudAiQuotaSnapshot?>(new CloudAiQuotaSnapshot(1, limit, _now, CurrentProfile.UserId));
        }

        public Task LogoutAsync(CancellationToken ct = default)
        {
            CurrentProfile = UserAccountProfile.Anonymous;
            CloudSessionStore.ClearSession();
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCloudSessionStore : ICloudAuthSessionStore
    {
        private CloudAuthSessionSnapshot? _session;
        public event Action? SessionChanged;
        public CloudAuthSessionSnapshot? GetCurrentSession() => _session;

        public void SetSession(CloudAuthSessionSnapshot? session)
        {
            _session = session;
            SessionChanged?.Invoke();
        }

        public void ClearSession() => SetSession(null);
    }

    private sealed class NoopModalUpscaler : IModalAiUpscaler
    {
        public Task<CloudUpscaleResult> EnhanceImageAsync(
            AssetId sourceAssetId,
            CloudAuthSessionSnapshot session,
            double fidelity = 0.7,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
