using Xunit;
using Ziwa.Domain.Auth;

namespace Ziwa.Domain.Tests;

public sealed class AuthAndLicensingDomainTests
{
    [Fact]
    public void PasswordPolicyValidator_EnforcesLetterAndDigitRulesAndExtractsOtpFromUrl()
    {
        Assert.False(PasswordPolicyValidator.ValidateStrongPassword("short1").IsValid);
        Assert.False(PasswordPolicyValidator.ValidateStrongPassword("onlyletters").IsValid);
        Assert.False(PasswordPolicyValidator.ValidateStrongPassword("12345678").IsValid);
        Assert.False(PasswordPolicyValidator.ValidateStrongPassword(new string('a', 129) + "1").IsValid);
        Assert.True(PasswordPolicyValidator.ValidateStrongPassword("ZiwaPass123").IsValid);

        Assert.Equal("987654", PasswordPolicyValidator.ExtractOtpToken("  987654 "));
        Assert.Equal("abc123token", PasswordPolicyValidator.ExtractOtpToken("https://grido.app/verify?token=abc123token&type=signup"));
        Assert.Equal("hash_xyz_77", PasswordPolicyValidator.ExtractOtpToken("https://grido.app/#token_hash=hash_xyz_77&type=recovery"));
    }

    [Fact]
    public void UserAccountProfile_EntitlementRespectsStatusPlanAndExpiryUtc()
    {
        DateTimeOffset now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        var activePro = new UserAccountProfile(
            "u1",
            "pro@ziwa.app",
            "Pro Studio",
            "pro",
            "active",
            now.AddDays(30),
            "ZIWA-PRO-1",
            now);
        Assert.True(activePro.IsAuthenticated);
        Assert.True(activePro.IsEntitled(now));
        Assert.Equal("pro", activePro.EffectivePlan(now));

        var expiredPro = activePro with { ExpiresAtUtc = now.AddMinutes(-1) };
        Assert.False(expiredPro.IsEntitled(now));
        Assert.Equal("free", expiredPro.EffectivePlan(now));

        var inactivePro = activePro with { Status = "none" };
        Assert.False(inactivePro.IsEntitled(now));
        Assert.Equal("free", inactivePro.EffectivePlan(now));

        var activeTrial = new UserAccountProfile(
            "u2",
            "trial@ziwa.app",
            "Trial Studio",
            "trial",
            "active",
            now.AddDays(7),
            null,
            now);
        Assert.True(activeTrial.IsEntitled(now));
        Assert.Equal("pro", activeTrial.EffectivePlan(now));

        Assert.False(UserAccountProfile.Anonymous.IsAuthenticated);
    }
}
