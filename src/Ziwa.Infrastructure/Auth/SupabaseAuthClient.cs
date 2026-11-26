using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ziwa.Application.Abstractions;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Auth;
using Ziwa.Infrastructure.Ai;

namespace Ziwa.Infrastructure.Auth;

public sealed class SupabaseAuthClient : ISupabaseAuthClient, IDisposable
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan[] TransportRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(600),
    ];

    private readonly SupabaseAuthOptions _options;
    private readonly ILocalAuthSessionStore _localStore;
    private readonly ICloudAuthSessionStore _cloudSessionStore;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _sessionMutex = new(1, 1);

    private long _transitionGeneration;
    private UserAccountProfile _currentProfile = UserAccountProfile.Anonymous;
    private bool _disposed;

    public SupabaseAuthClient(
        SupabaseAuthOptions options,
        ILocalAuthSessionStore localStore,
        ICloudAuthSessionStore cloudSessionStore,
        IBrowserLauncher? browserLauncher = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _cloudSessionStore = cloudSessionStore ?? throw new ArgumentNullException(nameof(cloudSessionStore));
        _browserLauncher = browserLauncher ?? new SystemBrowserLauncher();
        _clock = _options.Clock ?? (() => DateTimeOffset.UtcNow);

        if (_options.CustomHttpHandler is not null)
        {
            _httpClient = new HttpClient(_options.CustomHttpHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });
            _ownsHttpClient = true;
        }

        _httpClient.Timeout = _options.HttpTimeout > TimeSpan.Zero ? _options.HttpTimeout : TimeSpan.FromSeconds(15);
    }

    public event Action? StateChanged;

    public UserAccountProfile CurrentProfile => _currentProfile;
    public bool IsAuthenticated => _currentProfile.IsAuthenticated;
    public bool IsConfigured => _options.ResolveValidatedBaseUri() is not null;

    public async Task<UserAccountProfile> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PersistedAuthSession? cached = await _localStore.LoadAsync(cancellationToken);
        if (cached is null || string.IsNullOrWhiteSpace(cached.AccessToken))
        {
            SetProfileAndCloudSession(UserAccountProfile.Anonymous, accessToken: null);
            return _currentProfile;
        }

        SetProfileAndCloudSession(ApplyLocalExpiryCheck(cached.Profile), cached.AccessToken);
        return await CheckSessionStatusAsync(cancellationToken);
    }

    public async Task<UserAccountProfile> RegisterAsync(
        string name,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        string normalizedEmail = PasswordPolicyValidator.NormalizeEmail(email);
        string cleanName = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalizedEmail) || !normalizedEmail.Contains('@', StringComparison.Ordinal) ||
            string.IsNullOrEmpty(password))
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, "البريد الإلكتروني وكلمة المرور مطلوبة");
        }

        (bool strongOk, string? strongErr) = PasswordPolicyValidator.ValidateStrongPassword(password);
        if (!strongOk)
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, strongErr!);
        }

        long generation = BeginTransition();

        var body = new Dictionary<string, object?>
        {
            ["email"] = normalizedEmail,
            ["password"] = password,
            ["data"] = new Dictionary<string, string> { ["name"] = cleanName },
        };

        (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
            HttpMethod.Post,
            new Uri(baseUri, "/auth/v1/signup"),
            body,
            bearerToken: null,
            retryTransport: false,
            cancellationToken);

        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            throw MapRawAuthHttpFailure(status, respBytes, "فشل إنشاء الحساب");
        }

        ParsedAuthResponse auth = ParseAuthResponse(respBytes);
        if (string.IsNullOrWhiteSpace(auth.UserId) || string.IsNullOrWhiteSpace(auth.AccessToken))
        {
            UserAccountProfile pending = UserAccountProfile.Anonymous with
            {
                Email = normalizedEmail,
                DisplayName = string.IsNullOrWhiteSpace(cleanName) ? normalizedEmail : cleanName,
                Status = "pending_otp",
                UpdatedAtUtc = _clock(),
            };
            SetProfileAndCloudSession(pending, accessToken: null);
            return pending;
        }

        UserAccountProfile fetched = await FetchProfileWithFallbackAsync(
            baseUri,
            auth.AccessToken,
            auth.UserId,
            normalizedEmail,
            ResolveDisplayName(auth.DisplayName, cleanName, normalizedEmail),
            keepPendingOnNetworkError: true,
            cancellationToken);

        await CommitAuthenticatedSessionAsync(
            generation,
            baseUri,
            fetched,
            auth.AccessToken,
            auth.RefreshToken,
            cancellationToken);

        return _currentProfile;
    }

    public async Task<UserAccountProfile> VerifySignupOtpAsync(
        string email,
        string otpToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        string normalizedEmail = PasswordPolicyValidator.NormalizeEmail(email);
        string cleanToken = PasswordPolicyValidator.ExtractOtpToken(otpToken);
        if (string.IsNullOrEmpty(normalizedEmail) || string.IsNullOrEmpty(cleanToken))
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, "البريد الإلكتروني ورمز التحقق مطلوبان");
        }

        long generation = BeginTransition();

        ParsedAuthResponse auth = await VerifyOtpAcrossTypesAsync(
            baseUri,
            normalizedEmail,
            cleanToken,
            ["signup", "email", "magiclink"],
            requireAccessToken: true,
            "رمز التحقق غير صحيح أو منتهي الصلاحية",
            cancellationToken);

        UserAccountProfile fetched = await FetchProfileWithFallbackAsync(
            baseUri,
            auth.AccessToken,
            auth.UserId,
            normalizedEmail,
            ResolveDisplayName(auth.DisplayName, string.Empty, normalizedEmail),
            keepPendingOnNetworkError: false,
            cancellationToken);

        await CommitAuthenticatedSessionAsync(
            generation,
            baseUri,
            fetched,
            auth.AccessToken,
            auth.RefreshToken,
            cancellationToken);

        return _currentProfile;
    }

    public async Task ResendSignupOtpAsync(string email, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        string normalizedEmail = PasswordPolicyValidator.NormalizeEmail(email);
        if (string.IsNullOrEmpty(normalizedEmail))
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, "البريد الإلكتروني مطلوب لإعادة الإرسال");
        }

        (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
            HttpMethod.Post,
            new Uri(baseUri, "/auth/v1/resend"),
            new { type = "signup", email = normalizedEmail },
            bearerToken: null,
            retryTransport: false,
            cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            throw MapRawAuthHttpFailure(status, respBytes, "فشل إعادة إرسال رمز التحقق");
        }
    }

    public async Task<UserAccountProfile> LoginWithPasswordAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        (bool valid, string normalizedEmail, string? errMsg) = PasswordPolicyValidator.ValidateLoginInput(email, password);
        if (!valid)
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, errMsg!);
        }

        long generation = BeginTransition();

        (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
            HttpMethod.Post,
            new Uri(baseUri, "/auth/v1/token?grant_type=password"),
            new { email = normalizedEmail, password },
            bearerToken: null,
            retryTransport: false,
            cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            throw MapRawAuthHttpFailure(status, respBytes, "البريد الإلكتروني أو كلمة المرور غير صحيحة");
        }

        ParsedAuthResponse auth = ParseAuthResponse(respBytes);
        if (string.IsNullOrWhiteSpace(auth.UserId) || string.IsNullOrWhiteSpace(auth.AccessToken))
        {
            throw new AuthException(AuthErrorCode.ServerError, "استجابة تسجيل الدخول تفتقر لبيانات الجلسة");
        }

        UserAccountProfile fetched = await FetchProfileWithFallbackAsync(
            baseUri,
            auth.AccessToken,
            auth.UserId,
            string.IsNullOrWhiteSpace(auth.Email) ? normalizedEmail : auth.Email,
            ResolveDisplayName(auth.DisplayName, string.Empty, normalizedEmail),
            keepPendingOnNetworkError: false,
            cancellationToken);

        await CommitAuthenticatedSessionAsync(
            generation,
            baseUri,
            fetched,
            auth.AccessToken,
            auth.RefreshToken,
            cancellationToken);

        return _currentProfile;
    }

    public async Task<UserAccountProfile> LoginWithGoogleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        long generation = BeginTransition();

        await using var receiver = new LoopbackOAuthReceiver();
        Uri authorizeUri = LoopbackOAuthReceiver.BuildSupabaseGoogleAuthorizeUri(baseUri, receiver.CallbackUri);
        await _browserLauncher.OpenUrlAsync(authorizeUri, cancellationToken);

        OAuthTokenResult tokens = await receiver.WaitForTokensAsync(_options.OAuthCallbackTimeout, cancellationToken);
        ParsedAuthUserInfo userInfo = await FetchAuthUserAsync(baseUri, tokens.AccessToken, cancellationToken);

        UserAccountProfile fetched = await FetchProfileWithFallbackAsync(
            baseUri,
            tokens.AccessToken,
            userInfo.UserId,
            userInfo.Email,
            ResolveDisplayName(userInfo.FullName, string.Empty, userInfo.Email),
            keepPendingOnNetworkError: false,
            cancellationToken);

        await CommitAuthenticatedSessionAsync(
            generation,
            baseUri,
            fetched,
            tokens.AccessToken,
            tokens.RefreshToken,
            cancellationToken);

        return _currentProfile;
    }

    public async Task RequestPasswordRecoveryAsync(string email, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        string normalizedEmail = PasswordPolicyValidator.NormalizeEmail(email);
        if (string.IsNullOrEmpty(normalizedEmail))
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, "البريد الإلكتروني مطلوب لاستعادة كلمة المرور");
        }

        (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
            HttpMethod.Post,
            new Uri(baseUri, "/auth/v1/recover"),
            new { email = normalizedEmail },
            bearerToken: null,
            retryTransport: false,
            cancellationToken);

        if (status != HttpStatusCode.OK)
        {
            throw MapRawAuthHttpFailure(status, respBytes, "فشل إرسال رمز استعادة كلمة المرور");
        }
    }

    public async Task<UserAccountProfile> CompletePasswordRecoveryAsync(
        string email,
        string recoveryOtp,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        string normalizedEmail = PasswordPolicyValidator.NormalizeEmail(email);
        string cleanOtp = PasswordPolicyValidator.ExtractOtpToken(recoveryOtp);
        string cleanPassword = (newPassword ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(normalizedEmail) || string.IsNullOrEmpty(cleanOtp) || string.IsNullOrEmpty(cleanPassword))
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, "جميع حقول استعادة كلمة المرور مطلوبة");
        }

        (bool strongOk, string? strongErr) = PasswordPolicyValidator.ValidateStrongPassword(cleanPassword);
        if (!strongOk)
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, strongErr!);
        }

        long generation = BeginTransition();

        ParsedAuthResponse auth = await VerifyOtpAcrossTypesAsync(
            baseUri,
            normalizedEmail,
            cleanOtp,
            ["recovery", "magiclink", "signup", "email"],
            requireAccessToken: true,
            "رمز الاستعادة غير صحيح أو منتهي الصلاحية",
            cancellationToken);

        (HttpStatusCode updateStatus, byte[] updateBytes) = await SendAuthJsonAsync(
            HttpMethod.Put,
            new Uri(baseUri, "/auth/v1/user"),
            new { password = cleanPassword },
            bearerToken: auth.AccessToken,
            retryTransport: false,
            cancellationToken);

        if (updateStatus != HttpStatusCode.OK)
        {
            throw MapRawAuthHttpFailure(updateStatus, updateBytes, "تعذر تحديث كلمة المرور الجديدة");
        }

        UserAccountProfile fetched = await FetchProfileWithFallbackAsync(
            baseUri,
            auth.AccessToken,
            auth.UserId,
            normalizedEmail,
            ResolveDisplayName(auth.DisplayName, string.Empty, normalizedEmail),
            keepPendingOnNetworkError: true,
            cancellationToken);

        await CommitAuthenticatedSessionAsync(
            generation,
            baseUri,
            fetched,
            auth.AccessToken,
            auth.RefreshToken,
            cancellationToken);

        return _currentProfile;
    }

    public async Task<UserAccountProfile> ActivateLicenseKeyAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Uri baseUri = RequireConfiguredBaseUri();

        string trimmedKey = (licenseKey ?? string.Empty).Trim();
        if (trimmedKey.Length == 0)
        {
            throw new AuthException(AuthErrorCode.ValidationFailed, "مفتاح الترخيص لا يمكن أن يكون فارغاً");
        }

        await _sessionMutex.WaitAsync(cancellationToken);
        try
        {
            long generationSnapshot = Interlocked.Read(ref _transitionGeneration);
            PersistedAuthSession? local = await _localStore.LoadAsync(cancellationToken);
            if (local is null || string.IsNullOrWhiteSpace(local.AccessToken) || !local.Profile.IsAuthenticated)
            {
                throw new AuthException(AuthErrorCode.SessionExpired, "يرجى تسجيل الدخول أولاً قبل تفعيل الترخيص");
            }

            string deviceId = ComputeDeviceHash();
            (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
                HttpMethod.Post,
                new Uri(baseUri, "/rest/v1/rpc/activate_license"),
                new { p_key = trimmedKey, p_device_id = deviceId },
                bearerToken: local.AccessToken,
                retryTransport: false,
                cancellationToken);

            if (status != HttpStatusCode.OK)
            {
                string? msg = TryExtractErrorMessage(respBytes);
                throw new AuthException(
                    AuthErrorCode.LicenseInvalidOrUsed,
                    string.IsNullOrWhiteSpace(msg) ? "مفتاح الترخيص غير صالح أو تم استخدامه مسبقاً" : msg);
            }

            UserAccountProfile refreshedProfile = await FetchProfileWithFallbackAsync(
                baseUri,
                local.AccessToken,
                local.Profile.UserId,
                local.Profile.Email,
                local.Profile.DisplayName,
                keepPendingOnNetworkError: false,
                cancellationToken);

            if (Interlocked.Read(ref _transitionGeneration) != generationSnapshot)
            {
                throw new AuthException(AuthErrorCode.SessionTransitionStale, "تم تسجيل الخروج أثناء تفعيل الترخيص");
            }

            PersistedAuthSession updated = local with { Profile = refreshedProfile };
            await _localStore.SaveAsync(updated, cancellationToken);
            SetProfileAndCloudSession(refreshedProfile, updated.AccessToken);
            return refreshedProfile;
        }
        finally
        {
            _sessionMutex.Release();
        }
    }

    public async Task<UserAccountProfile> CheckSessionStatusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _sessionMutex.WaitAsync(cancellationToken);
        try
        {
            long generationSnapshot = Interlocked.Read(ref _transitionGeneration);
            PersistedAuthSession? local = await _localStore.LoadAsync(cancellationToken);
            if (local is null || string.IsNullOrWhiteSpace(local.AccessToken) || !local.Profile.IsAuthenticated)
            {
                SetProfileAndCloudSession(UserAccountProfile.Anonymous, accessToken: null);
                return _currentProfile;
            }

            DateTimeOffset nowUtc = _clock();
            UserAccountProfile locallyChecked = ApplyLocalExpiryCheck(local.Profile, nowUtc);
            if (!Equals(locallyChecked, local.Profile))
            {
                PersistedAuthSession expiredLocal = local with { Profile = locallyChecked };
                await _localStore.SaveAsync(expiredLocal, cancellationToken);
                SetProfileAndCloudSession(locallyChecked, expiredLocal.AccessToken);
                return locallyChecked;
            }

            Uri? baseUri = _options.ResolveValidatedBaseUri();
            if (baseUri is null)
            {
                SetProfileAndCloudSession(local.Profile, local.AccessToken);
                return local.Profile;
            }

            (ProfileFetchOutcome fetchOutcome, SupabaseProfileDto? profileDto) = await TryFetchProfileOnceAsync(
                baseUri,
                local.AccessToken,
                local.Profile.UserId,
                cancellationToken);

            if (fetchOutcome == ProfileFetchOutcome.Unauthorized)
            {
                RefreshOutcome refreshOutcome = await RefreshAccessTokenLockedAsync(
                    baseUri,
                    local,
                    generationSnapshot,
                    cancellationToken);

                if (refreshOutcome.Status is RefreshStatus.Succeeded or RefreshStatus.AdoptedConcurrentRotation &&
                    refreshOutcome.UpdatedSession is not null)
                {
                    local = refreshOutcome.UpdatedSession;
                    (fetchOutcome, profileDto) = await TryFetchProfileOnceAsync(
                        baseUri,
                        local.AccessToken,
                        local.Profile.UserId,
                        cancellationToken);
                }
                else if (refreshOutcome.Status == RefreshStatus.InvalidOrRevoked)
                {
                    await ClearSessionStateLockedAsync(cancellationToken);
                    return UserAccountProfile.Anonymous;
                }
                else
                {
                    // Transient network failure on refresh -> keep cached local session (Offline Grace).
                    SetProfileAndCloudSession(local.Profile, local.AccessToken);
                    return local.Profile;
                }
            }

            if (Interlocked.Read(ref _transitionGeneration) != generationSnapshot)
            {
                throw new AuthException(AuthErrorCode.SessionTransitionStale, "تم تغيير حالة الجلسة أثناء المزامنة");
            }

            if (fetchOutcome != ProfileFetchOutcome.Succeeded || profileDto is null)
            {
                // Transient network or server issue during profile check -> retain cached session (Offline Grace).
                SetProfileAndCloudSession(local.Profile, local.AccessToken);
                return local.Profile;
            }

            UserAccountProfile syncedProfile = local.Profile with
            {
                Plan = string.IsNullOrWhiteSpace(profileDto.Plan) ? "free" : profileDto.Plan.Trim().ToLowerInvariant(),
                Status = string.IsNullOrWhiteSpace(profileDto.Status) ? "none" : profileDto.Status.Trim().ToLowerInvariant(),
                ExpiresAtUtc = profileDto.ExpiresAtUtc,
                LicenseKey = profileDto.LicenseKey,
                UpdatedAtUtc = _clock(),
            };

            string? activeSessionId = local.ActiveSessionId;
            if (string.IsNullOrWhiteSpace(activeSessionId))
            {
                activeSessionId = await TryClaimSessionAsync(baseUri, local.AccessToken, cancellationToken);
            }
            else
            {
                SessionCheckOutcome sessionCheck = await VerifyActiveSessionAsync(
                    baseUri,
                    local.AccessToken,
                    activeSessionId,
                    cancellationToken);

                if (sessionCheck == SessionCheckOutcome.Superseded)
                {
                    await ClearSessionStateLockedAsync(cancellationToken);
                    throw new AuthException(
                        AuthErrorCode.SessionSuperseded,
                        $"{SupabaseAuthErrorMapper.SessionSupersededMarker}: تم تسجيل الدخول إلى حسابك من جهاز آخر — هذه الجلسة لم تعد نشطة");
                }
            }

            if (Interlocked.Read(ref _transitionGeneration) != generationSnapshot)
            {
                throw new AuthException(AuthErrorCode.SessionTransitionStale, "تم تسجيل الخروج أثناء مزامنة الجلسة");
            }

            PersistedAuthSession finalSession = local with
            {
                Profile = syncedProfile,
                ActiveSessionId = activeSessionId,
            };

            await _localStore.SaveAsync(finalSession, cancellationToken);
            SetProfileAndCloudSession(syncedProfile, finalSession.AccessToken);
            return syncedProfile;
        }
        finally
        {
            _sessionMutex.Release();
        }
    }

    public async Task<CloudAiQuotaSnapshot?> FetchServerDailyQuotaAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Uri? baseUri = _options.ResolveValidatedBaseUri();
        CloudAuthSessionSnapshot? cloudSession = _cloudSessionStore.GetCurrentSession();
        if (baseUri is null || cloudSession is null || string.IsNullOrWhiteSpace(cloudSession.AccessToken) ||
            !Guid.TryParse(cloudSession.UserId, out Guid userGuid))
        {
            return null;
        }

        try
        {
            (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
                HttpMethod.Post,
                new Uri(baseUri, "/rest/v1/rpc/check_and_record_ai_usage"),
                new
                {
                    p_user_id = userGuid.ToString("D"),
                    p_daily_limit = 0,
                    p_image_bytes = 0L,
                    p_exec_seconds = 0,
                    p_cost_usd = 0,
                    p_check_only = true,
                },
                bearerToken: cloudSession.AccessToken,
                retryTransport: false,
                cancellationToken);

            if (status != HttpStatusCode.OK)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(respBytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("used_today", out JsonElement usedEl) &&
                root.TryGetProperty("daily_limit", out JsonElement limitEl) &&
                usedEl.TryGetInt32(out int usedToday) &&
                limitEl.TryGetInt32(out int dailyLimit) &&
                dailyLimit > 0 &&
                usedToday >= 0)
            {
                return new CloudAiQuotaSnapshot(usedToday, dailyLimit, _clock(), cloudSession.UserIdentifier);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return null;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Immediately supersede any in-flight login/OAuth/status transition before waiting on lock/network.
        BeginTransition();
        SetProfileAndCloudSession(UserAccountProfile.Anonymous, accessToken: null);

        await _sessionMutex.WaitAsync(CancellationToken.None);
        try
        {
            PersistedAuthSession? local = await _localStore.LoadAsync(CancellationToken.None);
            Uri? baseUri = _options.ResolveValidatedBaseUri();
            if (baseUri is not null && local is not null && !string.IsNullOrWhiteSpace(local.AccessToken))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    await SendAuthJsonAsync(
                        HttpMethod.Post,
                        new Uri(baseUri, "/auth/v1/logout"),
                        bodyPayload: null,
                        bearerToken: local.AccessToken,
                        retryTransport: false,
                        cts.Token);
                }
                catch
                {
                }
            }

            // Do NOT delete user_sessions server row (another device may have claimed Last-Wins session).
            await ClearSessionStateLockedAsync(CancellationToken.None);
        }
        finally
        {
            _sessionMutex.Release();
        }
    }

    private long BeginTransition() => Interlocked.Increment(ref _transitionGeneration);

    private Uri RequireConfiguredBaseUri() =>
        _options.ResolveValidatedBaseUri()
        ?? throw new AuthException(
            AuthErrorCode.NotConfigured,
            "بيئة المصادقة غير مهيأة (تأكد من SUPABASE_URL وSUPABASE_PUBLISHABLE_KEY)");

    private async Task CommitAuthenticatedSessionAsync(
        long generation,
        Uri baseUri,
        UserAccountProfile profile,
        string accessToken,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        await _sessionMutex.WaitAsync(cancellationToken);
        try
        {
            if (Interlocked.Read(ref _transitionGeneration) != generation)
            {
                throw new AuthException(AuthErrorCode.SessionTransitionStale, "تم إلغاء الجلسة بسبب تسجيل خروج أو دخول أحدث");
            }

            // Write local session first without holding stale state, then claim Last-Wins session.
            await _localStore.ClearAsync(cancellationToken);
            var pendingSession = new PersistedAuthSession(profile, accessToken, refreshToken, ActiveSessionId: null);
            await _localStore.SaveAsync(pendingSession, cancellationToken);

            string? claimedSessionId = await TryClaimSessionAsync(baseUri, accessToken, cancellationToken);
            if (Interlocked.Read(ref _transitionGeneration) != generation)
            {
                await ClearSessionStateLockedAsync(CancellationToken.None);
                throw new AuthException(AuthErrorCode.SessionTransitionStale, "تم إلغاء الجلسة بسبب تسجيل خروج أو دخول أحدث");
            }

            if (!string.IsNullOrWhiteSpace(claimedSessionId))
            {
                await _localStore.SaveAsync(pendingSession with { ActiveSessionId = claimedSessionId }, cancellationToken);
            }

            SetProfileAndCloudSession(profile, accessToken);
        }
        finally
        {
            _sessionMutex.Release();
        }
    }

    private async Task<ParsedAuthResponse> VerifyOtpAcrossTypesAsync(
        Uri baseUri,
        string email,
        string otpToken,
        IReadOnlyList<string> verifyTypes,
        bool requireAccessToken,
        string fallbackErrorAr,
        CancellationToken ct)
    {
        AuthException? lastError = null;
        foreach (string verifyType in verifyTypes)
        {
            (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
                HttpMethod.Post,
                new Uri(baseUri, "/auth/v1/verify"),
                new { type = verifyType, email, token = otpToken },
                bearerToken: null,
                retryTransport: false,
                ct);

            if (status == HttpStatusCode.OK)
            {
                ParsedAuthResponse parsed = ParseAuthResponse(respBytes);
                bool hasIdentity = !string.IsNullOrWhiteSpace(parsed.UserId);
                bool hasToken = !requireAccessToken || !string.IsNullOrWhiteSpace(parsed.AccessToken);
                if (hasIdentity && hasToken)
                {
                    return parsed;
                }
            }

            lastError = MapRawAuthHttpFailure(status, respBytes, fallbackErrorAr);
        }

        throw lastError ?? new AuthException(AuthErrorCode.OtpInvalidOrExpired, fallbackErrorAr);
    }

    private async Task<UserAccountProfile> FetchProfileWithFallbackAsync(
        Uri baseUri,
        string accessToken,
        string userId,
        string email,
        string displayName,
        bool keepPendingOnNetworkError,
        CancellationToken ct)
    {
        (ProfileFetchOutcome outcome, SupabaseProfileDto? dto) = await TryFetchProfileOnceAsync(
            baseUri,
            accessToken,
            userId,
            ct);

        if (outcome == ProfileFetchOutcome.Succeeded && dto is not null)
        {
            return new UserAccountProfile(
                UserId: userId,
                Email: email,
                DisplayName: displayName,
                Plan: string.IsNullOrWhiteSpace(dto.Plan) ? "free" : dto.Plan.Trim().ToLowerInvariant(),
                Status: string.IsNullOrWhiteSpace(dto.Status) ? "none" : dto.Status.Trim().ToLowerInvariant(),
                ExpiresAtUtc: dto.ExpiresAtUtc,
                LicenseKey: dto.LicenseKey,
                UpdatedAtUtc: _clock());
        }

        if (keepPendingOnNetworkError)
        {
            return new UserAccountProfile(
                UserId: userId,
                Email: email,
                DisplayName: displayName,
                Plan: "free",
                Status: "pending",
                ExpiresAtUtc: null,
                LicenseKey: null,
                UpdatedAtUtc: _clock());
        }

        if (outcome == ProfileFetchOutcome.Unauthorized)
        {
            throw new AuthException(AuthErrorCode.SessionExpired, "الجلسة منتهية أو غير مصرح بها");
        }

        throw new AuthException(AuthErrorCode.NetworkUnavailable, "فشل جلب بيانات الحساب الشخصي من السيرفر");
    }

    private async Task<(ProfileFetchOutcome Outcome, SupabaseProfileDto? Profile)> TryFetchProfileOnceAsync(
        Uri baseUri,
        string accessToken,
        string userId,
        CancellationToken ct)
    {
        try
        {
            string encodedUserId = Uri.EscapeDataString(userId);
            Uri profileUri = new(baseUri, $"/rest/v1/profiles?select=plan,expires_at,license_key,status&id=eq.{encodedUserId}");

            (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
                HttpMethod.Get,
                profileUri,
                bodyPayload: null,
                bearerToken: accessToken,
                retryTransport: true,
                ct);

            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (ProfileFetchOutcome.Unauthorized, null);
            }

            if (status != HttpStatusCode.OK)
            {
                return (ProfileFetchOutcome.TransientError, null);
            }

            using JsonDocument doc = JsonDocument.Parse(respBytes);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                return (ProfileFetchOutcome.Succeeded, ParseProfileDto(doc.RootElement[0]));
            }

            // Fallback RPC if profile row has not yet been seeded
            (HttpStatusCode rpcStatus, byte[] rpcBytes) = await SendAuthJsonAsync(
                HttpMethod.Post,
                new Uri(baseUri, "/rest/v1/rpc/ensure_profile_exists"),
                new { },
                bearerToken: accessToken,
                retryTransport: false,
                ct);

            if (rpcStatus == HttpStatusCode.OK)
            {
                using JsonDocument rpcDoc = JsonDocument.Parse(rpcBytes);
                if (rpcDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return (ProfileFetchOutcome.Succeeded, ParseProfileDto(rpcDoc.RootElement));
                }
            }

            return (ProfileFetchOutcome.TransientError, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (ProfileFetchOutcome.TransientError, null);
        }
    }

    private async Task<RefreshOutcome> RefreshAccessTokenLockedAsync(
        Uri baseUri,
        PersistedAuthSession local,
        long generationSnapshot,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(local.RefreshToken))
        {
            return new RefreshOutcome(RefreshStatus.InvalidOrRevoked, null);
        }

        string usedRefreshToken = local.RefreshToken;

        try
        {
            (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
                HttpMethod.Post,
                new Uri(baseUri, "/auth/v1/token?grant_type=refresh_token"),
                new { refresh_token = usedRefreshToken },
                bearerToken: null,
                retryTransport: true,
                ct);

            if (status != HttpStatusCode.OK)
            {
                if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // Defense-in-depth (SEC-08): check if another caller rotated refresh_token to disk first.
                    PersistedAuthSession? stored = await _localStore.LoadAsync(ct);
                    if (stored is not null &&
                        !string.IsNullOrWhiteSpace(stored.RefreshToken) &&
                        !string.Equals(stored.RefreshToken, usedRefreshToken, StringComparison.Ordinal))
                    {
                        return new RefreshOutcome(RefreshStatus.AdoptedConcurrentRotation, stored);
                    }

                    return new RefreshOutcome(RefreshStatus.InvalidOrRevoked, null);
                }

                return new RefreshOutcome(RefreshStatus.TransientError, null);
            }

            if (Interlocked.Read(ref _transitionGeneration) != generationSnapshot)
            {
                return new RefreshOutcome(RefreshStatus.InvalidOrRevoked, null);
            }

            ParsedAuthResponse auth = ParseAuthResponse(respBytes);
            if (string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                return new RefreshOutcome(RefreshStatus.TransientError, null);
            }

            string nextRefresh = !string.IsNullOrWhiteSpace(auth.RefreshToken) ? auth.RefreshToken : usedRefreshToken;
            PersistedAuthSession rotated = local with
            {
                AccessToken = auth.AccessToken,
                RefreshToken = nextRefresh,
            };
            await _localStore.SaveAsync(rotated, ct);
            return new RefreshOutcome(RefreshStatus.Succeeded, rotated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new RefreshOutcome(RefreshStatus.TransientError, null);
        }
    }

    private async Task<string?> TryClaimSessionAsync(Uri baseUri, string accessToken, CancellationToken ct)
    {
        try
        {
            string sid = ModalAiUpscaler.GenerateCryptographicUuidV4();
            (HttpStatusCode status, _) = await SendAuthJsonAsync(
                HttpMethod.Post,
                new Uri(baseUri, "/rest/v1/rpc/claim_session"),
                new { p_session_id = sid, p_app_version = _options.AppVersion },
                bearerToken: accessToken,
                retryTransport: true,
                ct);

            return status == HttpStatusCode.OK ? sid : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<SessionCheckOutcome> VerifyActiveSessionAsync(
        Uri baseUri,
        string accessToken,
        string activeSessionId,
        CancellationToken ct)
    {
        try
        {
            (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
                HttpMethod.Post,
                new Uri(baseUri, "/rest/v1/rpc/check_session"),
                new { p_session_id = activeSessionId },
                bearerToken: accessToken,
                retryTransport: true,
                ct);

            if (status != HttpStatusCode.OK)
            {
                return SessionCheckOutcome.ActiveOrNetworkUnknown;
            }

            using JsonDocument doc = JsonDocument.Parse(respBytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SessionCheckOutcome.ActiveOrNetworkUnknown;
            }

            bool superseded = root.TryGetProperty("superseded", out JsonElement supEl) && supEl.ValueKind == JsonValueKind.True;
            bool active = !root.TryGetProperty("active", out JsonElement actEl) || actEl.ValueKind != JsonValueKind.False;
            return (superseded || !active) ? SessionCheckOutcome.Superseded : SessionCheckOutcome.ActiveOrNetworkUnknown;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return SessionCheckOutcome.ActiveOrNetworkUnknown;
        }
    }

    private async Task<ParsedAuthUserInfo> FetchAuthUserAsync(
        Uri baseUri,
        string accessToken,
        CancellationToken ct)
    {
        (HttpStatusCode status, byte[] respBytes) = await SendAuthJsonAsync(
            HttpMethod.Get,
            new Uri(baseUri, "/auth/v1/user"),
            bodyPayload: null,
            bearerToken: accessToken,
            retryTransport: true,
            ct);

        if (status != HttpStatusCode.OK)
        {
            throw new AuthException(AuthErrorCode.OAuthFailed, "تعذر قراءة بيانات الحساب بعد تسجيل الدخول عبر Google");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(respBytes);
            JsonElement root = doc.RootElement;
            string userId = GetJsonString(root, "id") ?? string.Empty;
            string email = GetJsonString(root, "email") ?? string.Empty;
            string fullName = string.Empty;
            if (root.TryGetProperty("user_metadata", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
            {
                fullName = GetJsonString(meta, "full_name") ?? GetJsonString(meta, "name") ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new AuthException(AuthErrorCode.OAuthFailed, "لم يتضمن رد المصادقة معرّف المستخدم");
            }

            return new ParsedAuthUserInfo(userId, email, fullName);
        }
        catch (JsonException ex)
        {
            throw new AuthException(AuthErrorCode.OAuthFailed, "استجابة بيانات المستخدم غير صالحة", ex);
        }
    }

    private async Task<(HttpStatusCode StatusCode, byte[] Body)> SendAuthJsonAsync(
        HttpMethod method,
        Uri uri,
        object? bodyPayload,
        string? bearerToken,
        bool retryTransport,
        CancellationToken ct)
    {
        string? jsonBody = bodyPayload is null ? null : JsonSerializer.Serialize(bodyPayload);
        string publishableKey = _options.SupabasePublishableKey ?? string.Empty;
        int maxAttempts = retryTransport ? TransportRetryDelays.Length + 1 : 1;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.TryAddWithoutValidation("apikey", publishableKey);
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
            }

            if (jsonBody is not null)
            {
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            try
            {
                using HttpResponseMessage resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                byte[] bytes = await ReadBoundedBytesAsync(resp, ct);
                return (resp.StatusCode, bytes);
            }
            catch (HttpRequestException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(TransportRetryDelays[attempt], ct);
            }
            catch (HttpRequestException ex)
            {
                throw new AuthException(
                    AuthErrorCode.NetworkUnavailable,
                    "تعذر الاتصال بخوادم المصادقة. يرجى التحقق من اتصال الإنترنت",
                    ex);
            }
        }

        throw new AuthException(AuthErrorCode.NetworkUnavailable, "تعذر الاتصال بخوادم المصادقة");
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0)
            {
                break;
            }

            int take = Math.Min(n, MaxResponseBytes - total);
            if (take > 0)
            {
                ms.Write(buffer, 0, take);
                total += take;
            }

            if (total >= MaxResponseBytes)
            {
                break;
            }
        }

        return ms.ToArray();
    }

    private async Task ClearSessionStateLockedAsync(CancellationToken ct)
    {
        await _localStore.ClearAsync(ct);
        SetProfileAndCloudSession(UserAccountProfile.Anonymous, accessToken: null);
    }

    private void SetProfileAndCloudSession(UserAccountProfile profile, string? accessToken)
    {
        _currentProfile = profile;
        if (profile.IsAuthenticated && !string.IsNullOrWhiteSpace(accessToken))
        {
            _cloudSessionStore.SetSession(new CloudAuthSessionSnapshot(
                AccessToken: accessToken,
                UserId: profile.UserId,
                Email: profile.Email,
                Plan: profile.EffectivePlan(_clock())));
        }
        else
        {
            _cloudSessionStore.ClearSession();
        }

        StateChanged?.Invoke();
    }

    private UserAccountProfile ApplyLocalExpiryCheck(UserAccountProfile profile, DateTimeOffset? nowOverride = null)
    {
        DateTimeOffset now = nowOverride ?? _clock();
        if (!string.Equals(profile.Plan, "free", StringComparison.OrdinalIgnoreCase) &&
            profile.ExpiresAtUtc is { } exp &&
            now >= exp)
        {
            return profile with
            {
                Plan = "free",
                Status = "expired",
                UpdatedAtUtc = now,
            };
        }

        return profile;
    }

    private static ParsedAuthResponse ParseAuthResponse(byte[] jsonBytes)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            JsonElement root = doc.RootElement;
            string accessToken = GetJsonString(root, "access_token") ?? string.Empty;
            string refreshToken = GetJsonString(root, "refresh_token") ?? string.Empty;
            string userId = GetJsonString(root, "id") ?? string.Empty;
            string email = string.Empty;
            string displayName = string.Empty;

            if (root.TryGetProperty("user", out JsonElement userEl) && userEl.ValueKind == JsonValueKind.Object)
            {
                userId = GetJsonString(userEl, "id") ?? userId;
                email = GetJsonString(userEl, "email") ?? string.Empty;
                if (userEl.TryGetProperty("user_metadata", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    displayName = GetJsonString(meta, "name") ?? GetJsonString(meta, "full_name") ?? string.Empty;
                }
            }

            return new ParsedAuthResponse(userId, email, displayName, accessToken, refreshToken);
        }
        catch
        {
            return new ParsedAuthResponse(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private static SupabaseProfileDto ParseProfileDto(JsonElement obj)
    {
        string plan = GetJsonString(obj, "plan") ?? "free";
        string status = GetJsonString(obj, "status") ?? "none";
        string? licenseKey = GetJsonString(obj, "license_key");
        DateTimeOffset? expiresAt = null;
        string? rawExp = GetJsonString(obj, "expires_at");
        if (!string.IsNullOrWhiteSpace(rawExp) && DateTimeOffset.TryParse(rawExp, out DateTimeOffset parsedExp))
        {
            expiresAt = parsedExp;
        }

        return new SupabaseProfileDto(plan, status, expiresAt, licenseKey);
    }

    private static AuthException MapRawAuthHttpFailure(HttpStatusCode statusCode, byte[] respBytes, string fallbackArabic)
    {
        string? raw = TryExtractErrorMessage(respBytes);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AuthException(
                statusCode == HttpStatusCode.TooManyRequests ? AuthErrorCode.RateLimited : AuthErrorCode.ServerError,
                fallbackArabic);
        }

        (AuthErrorCode code, string mappedAr) = SupabaseAuthErrorMapper.MapRawError(raw);
        return new AuthException(code, string.IsNullOrWhiteSpace(mappedAr) ? fallbackArabic : mappedAr);
    }

    private static string? TryExtractErrorMessage(byte[] jsonBytes)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return GetJsonString(root, "msg")
                ?? GetJsonString(root, "message")
                ?? GetJsonString(root, "error_description")
                ?? GetJsonString(root, "error");
        }
        catch
        {
            return null;
        }
    }

    private static string? GetJsonString(JsonElement obj, string propName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propName, out JsonElement val) &&
        val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static string ResolveDisplayName(string primary, string fallback, string email)
    {
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback.Trim();
        }

        return email;
    }

    private static string ComputeDeviceHash()
    {
        string raw = $"{Environment.MachineName}:{Environment.UserName}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionMutex.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private enum ProfileFetchOutcome
    {
        Succeeded,
        Unauthorized,
        TransientError,
    }

    private enum RefreshStatus
    {
        Succeeded,
        AdoptedConcurrentRotation,
        InvalidOrRevoked,
        TransientError,
    }

    private enum SessionCheckOutcome
    {
        ActiveOrNetworkUnknown,
        Superseded,
    }

    private sealed record RefreshOutcome(RefreshStatus Status, PersistedAuthSession? UpdatedSession);
    private sealed record ParsedAuthResponse(string UserId, string Email, string DisplayName, string AccessToken, string RefreshToken);
    private sealed record ParsedAuthUserInfo(string UserId, string Email, string FullName);
    private sealed record SupabaseProfileDto(string Plan, string Status, DateTimeOffset? ExpiresAtUtc, string? LicenseKey);
}
