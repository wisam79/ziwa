using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Application.Auth;
using Ziwa.Application.Commands;
using Ziwa.Domain.Auth;

namespace Ziwa.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private AccountSessionCoordinator? _accountCoordinator;

    [ObservableProperty]
    private bool _isAccountDialogOpen;

    [ObservableProperty]
    private AccountAuthFlowMode _accountFlowMode = AccountAuthFlowMode.Login;

    [ObservableProperty]
    private string _accountNameInput = string.Empty;

    [ObservableProperty]
    private string _accountEmailInput = string.Empty;

    [ObservableProperty]
    private string _accountPasswordInput = string.Empty;

    [ObservableProperty]
    private string _accountOtpInput = string.Empty;

    [ObservableProperty]
    private string _licenseKeyInput = string.Empty;

    [ObservableProperty]
    private string _accountFeedbackMessage = string.Empty;

    public UserAccountProfile CurrentAccountProfile =>
        _accountCoordinator?.CurrentProfile ?? UserAccountProfile.Anonymous;

    public bool IsUserAuthenticated => _accountCoordinator?.IsAuthenticated == true;
    public bool IsUserAnonymous => !IsUserAuthenticated;
    public bool IsAccountBusy => _accountCoordinator?.IsBusy == true;
    public bool IsAccountIdle => !IsAccountBusy;
    public string AccountBusyStatusText => _accountCoordinator?.BusyStatusAr ?? string.Empty;
    public bool HasAccountFeedback => !string.IsNullOrWhiteSpace(AccountFeedbackMessage);
    public bool HasSessionSupersededWarning => _accountCoordinator?.WasSessionSuperseded == true;
    public string SessionSupersededWarningText => _accountCoordinator?.SupersededBannerTextAr ?? string.Empty;

    public bool IsLoginFlowMode => AccountFlowMode == AccountAuthFlowMode.Login;
    public bool IsRegisterFlowMode => AccountFlowMode == AccountAuthFlowMode.Register;
    public bool IsVerifyOtpFlowMode => AccountFlowMode == AccountAuthFlowMode.VerifyOtp;
    public bool IsRecoverPasswordFlowMode => AccountFlowMode == AccountAuthFlowMode.RecoverPassword;

    public string AccountHeaderBadgeText => IsUserAuthenticated
        ? $"{AccountDisplayName} ({AccountPlanBadgeText})"
        : "الحساب";

    public string AccountDisplayName =>
        !string.IsNullOrWhiteSpace(CurrentAccountProfile.DisplayName)
            ? CurrentAccountProfile.DisplayName
            : CurrentAccountProfile.Email;

    public string AccountEmailDisplay => CurrentAccountProfile.Email;

    public string AccountPlanBadgeText => CurrentAccountProfile.EffectivePlan(DateTimeOffset.UtcNow) switch
    {
        "enterprise" => "Enterprise",
        "pro" => "Pro",
        _ => "Free",
    };

    public string AccountStatusDisplayText => CurrentAccountProfile.Status switch
    {
        "active" => "اشتراك نشط",
        "expired" => "اشتراك منتهٍ",
        "pending_otp" => "بانتظار رمز التحقق",
        "pending" => "بانتظار مزامنة الملف",
        _ => "خطة مجانية",
    };

    public string AccountExpiryDisplayText => CurrentAccountProfile.ExpiresAtUtc is { } exp
        ? $"ينتهي في: {exp:yyyy-MM-dd}"
        : "دون تاريخ انتهاء";

    private void InitializeAccountIntegration(AccountSessionCoordinator? accountCoordinator)
    {
        _accountCoordinator = accountCoordinator;
        if (_accountCoordinator is not null)
        {
            _accountCoordinator.StateChanged += OnAccountCoordinatorStateChanged;
        }

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("account.open"),
            "الحساب والترخيص والحصة السحابية…",
            "Ctrl+Shift+A",
            "حساب",
            () => _accountCoordinator is not null,
            _ =>
            {
                IsAccountDialogOpen = true;
                return Task.CompletedTask;
            }));
    }

    partial void OnAccountFlowModeChanged(AccountAuthFlowMode value)
    {
        OnPropertyChanged(nameof(IsLoginFlowMode));
        OnPropertyChanged(nameof(IsRegisterFlowMode));
        OnPropertyChanged(nameof(IsVerifyOtpFlowMode));
        OnPropertyChanged(nameof(IsRecoverPasswordFlowMode));
    }

    partial void OnAccountFeedbackMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasAccountFeedback));

    [RelayCommand]
    private void OpenAccountDialog()
    {
        AccountFeedbackMessage = string.Empty;
        IsAccountDialogOpen = true;
    }

    [RelayCommand]
    private void CloseAccountDialog()
    {
        if (IsAccountBusy)
        {
            return;
        }

        IsAccountDialogOpen = false;
    }

    [RelayCommand]
    private void SelectLoginFlow()
    {
        AccountFeedbackMessage = string.Empty;
        AccountFlowMode = AccountAuthFlowMode.Login;
    }

    [RelayCommand]
    private void SelectRegisterFlow()
    {
        AccountFeedbackMessage = string.Empty;
        AccountFlowMode = AccountAuthFlowMode.Register;
    }

    [RelayCommand]
    private void SelectVerifyOtpFlow()
    {
        AccountFeedbackMessage = string.Empty;
        AccountFlowMode = AccountAuthFlowMode.VerifyOtp;
    }

    [RelayCommand]
    private void SelectRecoverPasswordFlow()
    {
        AccountFeedbackMessage = string.Empty;
        AccountFlowMode = AccountAuthFlowMode.RecoverPassword;
    }

    [RelayCommand]
    private async Task SubmitLoginAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.LoginWithPasswordAsync(
            AccountEmailInput,
            AccountPasswordInput,
            ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: true);
    }

    [RelayCommand]
    private async Task SubmitRegisterAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.RegisterAsync(
            AccountNameInput,
            AccountEmailInput,
            AccountPasswordInput,
            ct);

        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: outcome.RequiresOtpVerification);
        if (outcome.RequiresOtpVerification)
        {
            AccountFlowMode = AccountAuthFlowMode.VerifyOtp;
        }
    }

    [RelayCommand]
    private async Task SubmitVerifyOtpAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.VerifySignupOtpAsync(
            AccountEmailInput,
            AccountOtpInput,
            ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: true);
        if (outcome.Succeeded)
        {
            AccountOtpInput = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ResendOtpAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.ResendSignupOtpAsync(AccountEmailInput, ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: false);
    }

    [RelayCommand]
    private async Task LoginWithGoogleAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.LoginWithGoogleAsync(ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: true);
    }

    [RelayCommand]
    private async Task RequestPasswordRecoveryAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.RequestPasswordRecoveryAsync(AccountEmailInput, ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: false);
    }

    [RelayCommand]
    private async Task SubmitPasswordRecoveryAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.CompletePasswordRecoveryAsync(
            AccountEmailInput,
            AccountOtpInput,
            AccountPasswordInput,
            ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: true);
        if (outcome.Succeeded)
        {
            AccountOtpInput = string.Empty;
        }
    }

    [RelayCommand]
    private async Task ActivateLicenseKeyAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.ActivateLicenseKeyAsync(LicenseKeyInput, ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: false);
        if (outcome.Succeeded)
        {
            LicenseKeyInput = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RefreshAccountStatusAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.CheckSessionStatusAsync(ct);
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: false);
    }

    [RelayCommand]
    private async Task LogoutAccountAsync(CancellationToken ct)
    {
        if (_accountCoordinator is null)
        {
            return;
        }

        AccountOperationOutcome outcome = await _accountCoordinator.LogoutAsync(ct);
        AccountPasswordInput = string.Empty;
        AccountOtpInput = string.Empty;
        AccountFlowMode = AccountAuthFlowMode.Login;
        ApplyAccountOutcome(outcome, clearPasswordOnSuccess: true);
    }

    [RelayCommand]
    private void CancelAccountOperation()
    {
        _accountCoordinator?.CancelActiveOperation();
        AccountFeedbackMessage = "جارٍ إلغاء العملية...";
    }

    private void ApplyAccountOutcome(AccountOperationOutcome outcome, bool clearPasswordOnSuccess)
    {
        AccountFeedbackMessage = outcome.MessageAr;
        StatusMessage = outcome.MessageAr;
        if (outcome.Succeeded && clearPasswordOnSuccess)
        {
            AccountPasswordInput = string.Empty;
        }

        if (outcome.WasSessionSuperseded)
        {
            IsAccountDialogOpen = true;
            AccountFlowMode = AccountAuthFlowMode.Login;
        }
    }

    private void OnAccountCoordinatorStateChanged()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(CurrentAccountProfile));
            OnPropertyChanged(nameof(IsUserAuthenticated));
            OnPropertyChanged(nameof(IsUserAnonymous));
            OnPropertyChanged(nameof(IsAccountBusy));
            OnPropertyChanged(nameof(IsAccountIdle));
            OnPropertyChanged(nameof(AccountBusyStatusText));
            OnPropertyChanged(nameof(AccountHeaderBadgeText));
            OnPropertyChanged(nameof(AccountDisplayName));
            OnPropertyChanged(nameof(AccountEmailDisplay));
            OnPropertyChanged(nameof(AccountPlanBadgeText));
            OnPropertyChanged(nameof(AccountStatusDisplayText));
            OnPropertyChanged(nameof(AccountExpiryDisplayText));
            OnPropertyChanged(nameof(HasSessionSupersededWarning));
            OnPropertyChanged(nameof(SessionSupersededWarningText));
            OnPropertyChanged(nameof(CloudQuotaBadgeText));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Raise();
        }
        else
        {
            Dispatcher.UIThread.Post(Raise);
        }
    }

    private void DisposeAccountResources()
    {
        if (_accountCoordinator is not null)
        {
            _accountCoordinator.StateChanged -= OnAccountCoordinatorStateChanged;
        }
    }
}
