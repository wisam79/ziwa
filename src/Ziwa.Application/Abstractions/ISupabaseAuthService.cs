using Ziwa.Domain.Ai;
using Ziwa.Domain.Auth;

namespace Ziwa.Application.Abstractions;

public sealed class AuthException : Exception
{
    public AuthException(AuthErrorCode errorCode, string messageAr, Exception? innerException = null)
        : base(messageAr, innerException)
    {
        ErrorCode = errorCode;
    }

    public AuthErrorCode ErrorCode { get; }

    public bool IsSessionSuperseded => ErrorCode == AuthErrorCode.SessionSuperseded;
}

public sealed record PersistedAuthSession(
    UserAccountProfile Profile,
    string AccessToken,
    string RefreshToken,
    string? ActiveSessionId = null);

public interface ILocalAuthSessionStore
{
    Task<PersistedAuthSession?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PersistedAuthSession session, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IBrowserLauncher
{
    Task OpenUrlAsync(Uri uri, CancellationToken cancellationToken = default);
}

public interface ISupabaseAuthClient
{
    event Action? StateChanged;

    UserAccountProfile CurrentProfile { get; }
    bool IsAuthenticated { get; }
    bool IsConfigured { get; }

    Task<UserAccountProfile> InitializeAsync(CancellationToken cancellationToken = default);
    Task<UserAccountProfile> RegisterAsync(string name, string email, string password, CancellationToken cancellationToken = default);
    Task<UserAccountProfile> VerifySignupOtpAsync(string email, string otpToken, CancellationToken cancellationToken = default);
    Task ResendSignupOtpAsync(string email, CancellationToken cancellationToken = default);
    Task<UserAccountProfile> LoginWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<UserAccountProfile> LoginWithGoogleAsync(CancellationToken cancellationToken = default);
    Task RequestPasswordRecoveryAsync(string email, CancellationToken cancellationToken = default);
    Task<UserAccountProfile> CompletePasswordRecoveryAsync(string email, string recoveryOtp, string newPassword, CancellationToken cancellationToken = default);
    Task<UserAccountProfile> ActivateLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default);
    Task<UserAccountProfile> CheckSessionStatusAsync(CancellationToken cancellationToken = default);
    Task<CloudAiQuotaSnapshot?> FetchServerDailyQuotaAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}
