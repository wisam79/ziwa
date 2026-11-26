using Ziwa.Application.Abstractions;
using Ziwa.Application.Ai;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Auth;

namespace Ziwa.Application.Auth;

public sealed record AccountOperationOutcome(
    bool Succeeded,
    string MessageAr,
    bool RequiresOtpVerification = false,
    bool WasSessionSuperseded = false,
    bool WasCancelled = false);

public sealed class AccountSessionCoordinator : IDisposable
{
    private readonly ISupabaseAuthClient _authClient;
    private readonly CloudAiCoordinator? _cloudAiCoordinator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeCts;
    private bool _disposed;

    public AccountSessionCoordinator(
        ISupabaseAuthClient authClient,
        CloudAiCoordinator? cloudAiCoordinator = null)
    {
        _authClient = authClient;
        _cloudAiCoordinator = cloudAiCoordinator;
        _authClient.StateChanged += OnAuthClientStateChanged;
    }

    public event Action? StateChanged;

    public UserAccountProfile CurrentProfile => _authClient.CurrentProfile;
    public bool IsAuthenticated => _authClient.IsAuthenticated;
    public bool IsConfigured => _authClient.IsConfigured;
    public bool IsBusy { get; private set; }
    public string BusyStatusAr { get; private set; } = string.Empty;
    public bool WasSessionSuperseded { get; private set; }
    public string SupersededBannerTextAr { get; private set; } = string.Empty;

    public void CancelActiveOperation()
    {
        try
        {
            _activeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public Task<AccountOperationOutcome> InitializeAsync(CancellationToken externalCt = default) =>
        RunProfileActionAsync(
            "جارٍ التحقق من حالة الجلسة والترخيص...",
            ct => _authClient.InitializeAsync(ct),
            static p => p.IsAuthenticated ? $"مرحباً {p.DisplayName}" : "جاهز — غير مسجل الدخول",
            clearSupersededBefore: false,
            externalCt);

    public async Task<AccountOperationOutcome> RegisterAsync(
        string name,
        string email,
        string password,
        CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new AccountOperationOutcome(false, "توجد عملية حساب قيد التنفيذ حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeCts = linkedCts;
        ClearSupersededWarning();
        SetBusy(true, "جارٍ إنشاء الحساب...");

        try
        {
            UserAccountProfile profile = await _authClient.RegisterAsync(name, email, password, linkedCts.Token);
            if (profile.IsPendingOtp)
            {
                return new AccountOperationOutcome(
                    Succeeded: true,
                    MessageAr: "تم إرسال رمز التحقق (OTP) إلى بريدك الإلكتروني",
                    RequiresOtpVerification: true);
            }

            await SyncQuotaAfterAuthAsync(profile, linkedCts.Token);
            return new AccountOperationOutcome(true, $"تم إنشاء الحساب وتسجيل الدخول بنجاح ({profile.DisplayName})");
        }
        catch (OperationCanceledException)
        {
            return new AccountOperationOutcome(false, "تم إلغاء إنشاء الحساب", WasCancelled: true);
        }
        catch (AuthException ex)
        {
            return new AccountOperationOutcome(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new AccountOperationOutcome(false, $"تعذر إنشاء الحساب: {ex.Message}");
        }
        finally
        {
            _activeCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    public Task<AccountOperationOutcome> VerifySignupOtpAsync(
        string email,
        string otpToken,
        CancellationToken externalCt = default) =>
        RunProfileActionAsync(
            "جارٍ التحقق من رمز التفعيل (OTP)...",
            ct => _authClient.VerifySignupOtpAsync(email, otpToken, ct),
            static p => $"تم تفعيل الحساب بنجاح ({p.DisplayName})",
            clearSupersededBefore: true,
            externalCt);

    public Task<AccountOperationOutcome> ResendSignupOtpAsync(
        string email,
        CancellationToken externalCt = default) =>
        RunActionAsync(
            "جارٍ إعادة إرسال رمز التحقق...",
            async ct =>
            {
                await _authClient.ResendSignupOtpAsync(email, ct);
                return new AccountOperationOutcome(true, "تمت إعادة إرسال رمز التحقق إلى بريدك الإلكتروني", RequiresOtpVerification: true);
            },
            "تم إلغاء إعادة إرسال الرمز",
            clearSupersededBefore: false,
            externalCt);

    public Task<AccountOperationOutcome> LoginWithPasswordAsync(
        string email,
        string password,
        CancellationToken externalCt = default) =>
        RunProfileActionAsync(
            "جارٍ تسجيل الدخول ومزامنة الجلسة...",
            ct => _authClient.LoginWithPasswordAsync(email, password, ct),
            static p => $"تم تسجيل الدخول بنجاح ({p.DisplayName})",
            clearSupersededBefore: true,
            externalCt);

    public Task<AccountOperationOutcome> LoginWithGoogleAsync(CancellationToken externalCt = default) =>
        RunProfileActionAsync(
            "جارٍ انتظار إتمام تسجيل الدخول عبر Google في المتصفح...",
            ct => _authClient.LoginWithGoogleAsync(ct),
            static p => $"تم تسجيل الدخول عبر Google بنجاح ({p.DisplayName})",
            clearSupersededBefore: true,
            externalCt);

    public Task<AccountOperationOutcome> RequestPasswordRecoveryAsync(
        string email,
        CancellationToken externalCt = default) =>
        RunActionAsync(
            "جارٍ إرسال رمز استعادة كلمة المرور...",
            async ct =>
            {
                await _authClient.RequestPasswordRecoveryAsync(email, ct);
                return new AccountOperationOutcome(true, "تم إرسال رمز الاستعادة إلى بريدك الإلكتروني");
            },
            "تم إلغاء طلب استعادة كلمة المرور",
            clearSupersededBefore: false,
            externalCt);

    public Task<AccountOperationOutcome> CompletePasswordRecoveryAsync(
        string email,
        string recoveryOtp,
        string newPassword,
        CancellationToken externalCt = default) =>
        RunProfileActionAsync(
            "جارٍ التحقق من رمز الاستعادة وتحديث كلمة المرور...",
            ct => _authClient.CompletePasswordRecoveryAsync(email, recoveryOtp, newPassword, ct),
            static _ => "تم تعيين كلمة المرور الجديدة وتحديث الجلسة",
            clearSupersededBefore: true,
            externalCt);

    public Task<AccountOperationOutcome> ActivateLicenseKeyAsync(
        string licenseKey,
        CancellationToken externalCt = default) =>
        RunProfileActionAsync(
            "جارٍ تفعيل مفتاح الترخيص عبر الخادم...",
            ct => _authClient.ActivateLicenseKeyAsync(licenseKey, ct),
            static p => $"تم تفعيل الترخيص بنجاح — الباقة الحالية: {p.Plan.ToUpperInvariant()}",
            clearSupersededBefore: true,
            externalCt);

    public Task<AccountOperationOutcome> CheckSessionStatusAsync(CancellationToken externalCt = default) =>
        RunProfileActionAsync(
            "جارٍ مزامنة حالة الاشتراك والجلسة...",
            ct => _authClient.CheckSessionStatusAsync(ct),
            static p => p.IsAuthenticated
                ? $"الحساب نشط ({p.Plan.ToUpperInvariant()})"
                : "غير مسجل الدخول",
            clearSupersededBefore: false,
            externalCt);

    public async Task<AccountOperationOutcome> LogoutAsync(CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Cancel any in-flight login/status check immediately before acquiring gate so logout is never blocked.
        CancelActiveOperation();
        await _authClient.LogoutAsync(externalCt);
        ClearSupersededWarning();
        _cloudAiCoordinator?.UpdateServerQuotaSnapshot(null);
        StateChanged?.Invoke();
        return new AccountOperationOutcome(true, "تم تسجيل الخروج ومسح الجلسة المحلية والحصة");
    }

    private async Task<AccountOperationOutcome> RunProfileActionAsync(
        string busyTextAr,
        Func<CancellationToken, Task<UserAccountProfile>> operation,
        Func<UserAccountProfile, string> successMessageFactory,
        bool clearSupersededBefore,
        CancellationToken externalCt) =>
        await RunActionAsync(
            busyTextAr,
            async ct =>
            {
                UserAccountProfile profile = await operation(ct);
                await SyncQuotaAfterAuthAsync(profile, ct);
                return new AccountOperationOutcome(true, successMessageFactory(profile));
            },
            "تم إلغاء العملية",
            clearSupersededBefore,
            externalCt);

    private async Task<AccountOperationOutcome> RunActionAsync(
        string busyTextAr,
        Func<CancellationToken, Task<AccountOperationOutcome>> operation,
        string cancelledMessageAr,
        bool clearSupersededBefore,
        CancellationToken externalCt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new AccountOperationOutcome(false, "توجد عملية حساب قيد التنفيذ حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeCts = linkedCts;
        if (clearSupersededBefore)
        {
            ClearSupersededWarning();
        }

        SetBusy(true, busyTextAr);

        try
        {
            return await operation(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            return new AccountOperationOutcome(false, cancelledMessageAr, WasCancelled: true);
        }
        catch (AuthException ex) when (ex.IsSessionSuperseded)
        {
            WasSessionSuperseded = true;
            SupersededBannerTextAr = ex.Message;
            _cloudAiCoordinator?.UpdateServerQuotaSnapshot(null);
            return new AccountOperationOutcome(false, ex.Message, WasSessionSuperseded: true);
        }
        catch (AuthException ex)
        {
            return new AccountOperationOutcome(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new AccountOperationOutcome(false, $"حدث خطأ غير متوقع: {ex.Message}");
        }
        finally
        {
            _activeCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    private async Task SyncQuotaAfterAuthAsync(UserAccountProfile profile, CancellationToken ct)
    {
        if (_cloudAiCoordinator is null)
        {
            return;
        }

        if (!profile.IsAuthenticated)
        {
            _cloudAiCoordinator.UpdateServerQuotaSnapshot(null);
            return;
        }

        CloudAiQuotaSnapshot? snapshot = await _authClient.FetchServerDailyQuotaAsync(ct);
        if (snapshot is not null)
        {
            _cloudAiCoordinator.UpdateServerQuotaSnapshot(snapshot);
        }
    }

    private void ClearSupersededWarning()
    {
        WasSessionSuperseded = false;
        SupersededBannerTextAr = string.Empty;
    }

    private void OnAuthClientStateChanged() => StateChanged?.Invoke();

    private void SetBusy(bool busy, string statusAr)
    {
        IsBusy = busy;
        BusyStatusAr = statusAr;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _authClient.StateChanged -= OnAuthClientStateChanged;
        CancelActiveOperation();
        _gate.Dispose();
    }
}
