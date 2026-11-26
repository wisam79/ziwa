namespace Ziwa.Domain.Auth;

public enum AuthErrorCode
{
    None = 0,
    NotConfigured = 1,
    ValidationFailed = 2,
    InvalidCredentials = 3,
    EmailNotConfirmed = 4,
    UserAlreadyExists = 5,
    OtpInvalidOrExpired = 6,
    RateLimited = 7,
    SessionExpired = 8,
    SessionSuperseded = 9,
    SessionTransitionStale = 10,
    LicenseInvalidOrUsed = 11,
    OAuthFailed = 12,
    OAuthTimeout = 13,
    NetworkUnavailable = 14,
    ServerError = 15,
}

public enum AccountAuthFlowMode
{
    Login = 0,
    Register = 1,
    VerifyOtp = 2,
    RecoverPassword = 3,
}

public sealed record UserAccountProfile(
    string UserId,
    string Email,
    string DisplayName,
    string Plan,
    string Status,
    DateTimeOffset? ExpiresAtUtc,
    string? LicenseKey,
    DateTimeOffset UpdatedAtUtc)
{
    public static UserAccountProfile Anonymous { get; } = new(
        UserId: string.Empty,
        Email: string.Empty,
        DisplayName: string.Empty,
        Plan: "free",
        Status: "none",
        ExpiresAtUtc: null,
        LicenseKey: null,
        UpdatedAtUtc: DateTimeOffset.MinValue);

    public bool IsPendingOtp => string.Equals(Status, "pending_otp", StringComparison.OrdinalIgnoreCase);

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId) && !IsPendingOtp;

    public bool IsEntitled(DateTimeOffset nowUtc)
    {
        string normalizedPlan = (Plan ?? "free").Trim().ToLowerInvariant();
        if (normalizedPlan is "pro" or "enterprise")
        {
            return string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase) &&
                (ExpiresAtUtc is null || nowUtc < ExpiresAtUtc.Value);
        }

        if (normalizedPlan == "trial")
        {
            return ExpiresAtUtc is not null && nowUtc < ExpiresAtUtc.Value;
        }

        return false;
    }

    public string EffectivePlan(DateTimeOffset nowUtc)
    {
        if (!IsEntitled(nowUtc))
        {
            return "free";
        }

        string normalizedPlan = (Plan ?? "free").Trim().ToLowerInvariant();
        return normalizedPlan switch
        {
            "enterprise" => "enterprise",
            "pro" => "pro",
            "trial" => "pro",
            _ => "free",
        };
    }
}

public static class PasswordPolicyValidator
{
    public const int MinStrongLength = 8;
    public const int MinLoginLength = 6;
    public const int MaxLength = 128;

    public static (bool IsValid, string? ErrorMessageAr) ValidateStrongPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinStrongLength)
        {
            return (false, "كلمة المرور يجب أن تكون 8 أحرف على الأقل");
        }

        if (password.Length > MaxLength)
        {
            return (false, "كلمة المرور يجب ألا تتجاوز 128 حرفاً");
        }

        bool hasLetter = false;
        bool hasDigit = false;
        foreach (char ch in password)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
            }
            else if (char.IsDigit(ch))
            {
                hasDigit = true;
            }
        }

        if (!hasLetter || !hasDigit)
        {
            return (false, "كلمة المرور يجب أن تتضمن حروفاً وأرقاماً معاً");
        }

        return (true, null);
    }

    public static (bool IsValid, string NormalizedEmail, string? ErrorMessageAr) ValidateLoginInput(
        string? email,
        string? password)
    {
        string normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrEmpty(normalizedEmail) || !normalizedEmail.Contains('@', StringComparison.Ordinal) ||
            string.IsNullOrEmpty(password))
        {
            return (false, normalizedEmail, "البريد الإلكتروني وكلمة المرور مطلوبة");
        }

        if (password.Length < MinLoginLength)
        {
            return (false, normalizedEmail, "كلمة المرور يجب أن تكون 6 أحرف على الأقل");
        }

        if (password.Length > MaxLength)
        {
            return (false, normalizedEmail, "كلمة المرور يجب ألا تتجاوز 128 حرفاً");
        }

        return (true, normalizedEmail, null);
    }

    public static string NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();

    public static string ExtractOtpToken(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return string.Empty;
        }

        string trimmed = rawInput.Trim();
        foreach (string key in new[] { "token_hash=", "token=" })
        {
            int idx = trimmed.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            string rest = trimmed[(idx + key.Length)..];
            int stopIdx = rest.IndexOfAny(['&', '#', '?', '/']);
            return (stopIdx >= 0 ? rest[..stopIdx] : rest).Trim();
        }

        return trimmed;
    }
}

public static class SupabaseAuthErrorMapper
{
    public const string SessionSupersededMarker = "ERR_SESSION_SUPERSEDED";

    public static (AuthErrorCode Code, string MessageAr) MapRawError(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return (AuthErrorCode.ServerError, "حدث خطأ غير متوقع في خادم المصادقة");
        }

        string lower = rawError.ToLowerInvariant();

        if (lower.Contains("invalid login credentials", StringComparison.Ordinal) ||
            lower.Contains("invalid_credentials", StringComparison.Ordinal))
        {
            return (AuthErrorCode.InvalidCredentials, "البريد الإلكتروني أو كلمة المرور غير صحيحة");
        }

        if (lower.Contains("user already registered", StringComparison.Ordinal) ||
            lower.Contains("already_registered", StringComparison.Ordinal) ||
            lower.Contains("user_already_exists", StringComparison.Ordinal))
        {
            return (AuthErrorCode.UserAlreadyExists, "هذا البريد الإلكتروني مسجل بالفعل. يمكنك تسجيل الدخول مباشرة");
        }

        if (lower.Contains("email not confirmed", StringComparison.Ordinal) ||
            lower.Contains("email_not_confirmed", StringComparison.Ordinal))
        {
            return (AuthErrorCode.EmailNotConfirmed, "البريد الإلكتروني بحاجة لتأكيد. يرجى إدخال رمز التحقق (OTP)");
        }

        if (lower.Contains("over_email_send_rate_limit", StringComparison.Ordinal) ||
            lower.Contains("rate limit exceeded", StringComparison.Ordinal) ||
            lower.Contains("too many requests", StringComparison.Ordinal))
        {
            return (AuthErrorCode.RateLimited, "تم تجاوز حد إرسال الطلبات المسموح به. يرجى الانتظار بضع دقائق ثم المحاولة مجدداً");
        }

        if (lower.Contains("password should be at least", StringComparison.Ordinal) ||
            lower.Contains("weak_password", StringComparison.Ordinal))
        {
            return (AuthErrorCode.ValidationFailed, "كلمة المرور يجب أن تكون 8 أحرف على الأقل وتتضمن حروفاً وأرقاماً");
        }

        if (lower.Contains("token has expired", StringComparison.Ordinal) ||
            lower.Contains("token is invalid", StringComparison.Ordinal) ||
            lower.Contains("invalid_grant", StringComparison.Ordinal) ||
            lower.Contains("otp_expired", StringComparison.Ordinal))
        {
            return (AuthErrorCode.OtpInvalidOrExpired, "رمز التحقق غير صحيح أو منتهي الصلاحية");
        }

        if (lower.Contains("signup_disabled", StringComparison.Ordinal))
        {
            return (AuthErrorCode.ServerError, "تسجيل الحسابات الجديدة متوقف مؤقتاً في الوقت الحالي");
        }

        if (lower.Contains("jwt expired", StringComparison.Ordinal) ||
            lower.Contains("token_expired", StringComparison.Ordinal))
        {
            return (AuthErrorCode.SessionExpired, "انتهت صلاحية جلسة تسجيل الدخول، يرجى إعادة تسجيل الدخول");
        }

        return (AuthErrorCode.ServerError, rawError.Trim());
    }
}
