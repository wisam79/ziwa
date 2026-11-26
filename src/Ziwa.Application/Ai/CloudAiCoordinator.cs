using Ziwa.Application.Abstractions;
using Ziwa.Application.Collage;
using Ziwa.Application.Interaction;
using Ziwa.Application.Session;
using Ziwa.Domain.Ai;

namespace Ziwa.Application.Ai;

public sealed class CloudAiCoordinator : IDisposable
{
    private readonly DocumentSession _session;
    private readonly IModalAiUpscaler _upscaler;
    private readonly ICloudAuthSessionProvider _authProvider;
    private readonly OfflineAiCoordinator? _offlineCoordinator;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _quotaSync = new();

    private CancellationTokenSource? _activeOperationCts;
    private CloudAiQuotaSnapshot? _serverQuotaSnapshot;
    private string _localUtcDayKey = string.Empty;
    private string _localUserIdentifier = string.Empty;
    private string _localPlan = string.Empty;
    private int _localUsedTodayUtc;
    private bool _disposed;

    public CloudAiCoordinator(
        DocumentSession session,
        IModalAiUpscaler upscaler,
        ICloudAuthSessionProvider authProvider,
        OfflineAiCoordinator? offlineCoordinator = null,
        Func<DateTimeOffset>? clock = null)
    {
        _session = session;
        _upscaler = upscaler;
        _authProvider = authProvider;
        _offlineCoordinator = offlineCoordinator;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        SeedAuthIdentity(_authProvider.GetCurrentSession());
        _authProvider.SessionChanged += OnAuthSessionChanged;
    }

    public event Action? StateChanged;

    public bool IsBusy { get; private set; }
    public string BusyStatusAr { get; private set; } = string.Empty;

    public bool IsAuthenticated
    {
        get
        {
            CloudAuthSessionSnapshot? s = _authProvider.GetCurrentSession();
            return s is not null && !string.IsNullOrWhiteSpace(s.AccessToken);
        }
    }

    public ResolvedCloudAiQuota CurrentQuota
    {
        get
        {
            CloudAuthSessionSnapshot? session = _authProvider.GetCurrentSession();
            DateTimeOffset now = _clock();
            string utcDay = CloudAiQuotaPolicy.GetUtcDayKey(now);

            lock (_quotaSync)
            {
                if (!string.Equals(_localUtcDayKey, utcDay, StringComparison.Ordinal))
                {
                    _localUtcDayKey = utcDay;
                    _localUsedTodayUtc = 0;
                    _serverQuotaSnapshot = null;
                }

                return CloudAiQuotaPolicy.ResolveQuota(
                    _serverQuotaSnapshot,
                    session?.Plan,
                    session?.UserIdentifier,
                    _localUsedTodayUtc,
                    now);
            }
        }
    }

    public string QuotaBadgeTextAr
    {
        get
        {
            if (!IsAuthenticated)
            {
                return "تسجيل الدخول مطلوب للترميم السحابي";
            }

            ResolvedCloudAiQuota q = CurrentQuota;
            string planLabel = (_authProvider.GetCurrentSession()?.Plan ?? "free").Trim().ToLowerInvariant() switch
            {
                "enterprise" => "Enterprise",
                "pro" => "Pro",
                _ => "Free",
            };
            return $"الحصة ({planLabel}): متبقٍ {q.Remaining}/{q.Limit}";
        }
    }

    public void CancelActiveOperation()
    {
        try
        {
            _activeOperationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task<AiOperationOutcome> EnhanceSelectedPhotoAsync(
        double fidelity = 0.7,
        CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CloudAuthSessionSnapshot? authSession = _authProvider.GetCurrentSession();
        if (authSession is null || string.IsNullOrWhiteSpace(authSession.AccessToken))
        {
            return new AiOperationOutcome(false, "تسجيل الدخول مطلوب لاستخدام الترميم السحابي المزدوج (Modal 2x)");
        }

        if (_offlineCoordinator?.IsBusy == true)
        {
            return new AiOperationOutcome(false, "توجد عملية ذكاء اصطناعي محلية قيد التنفيذ حالياً");
        }

        if (!AiPhotoTargetResolver.TryResolveSelectedPhotoTarget(_session, out AiPhotoTarget target))
        {
            return new AiOperationOutcome(false, "يرجى تحديد صورة أو خانة كولاج تحتوي على صورة لترميمها سحابياً");
        }

        ResolvedCloudAiQuota quotaBefore = CurrentQuota;
        if (quotaBefore.IsServerVerified && quotaBefore.Remaining <= 0)
        {
            return new AiOperationOutcome(
                false,
                $"تم الوصول للحد اليومي للترميم السحابي ({quotaBefore.Limit} صور/يومياً) — يتجدد غداً بتوقيت UTC");
        }

        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new AiOperationOutcome(false, "توجد عملية ترميم سحابي جارية حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeOperationCts = linkedCts;
        SetBusy(true, "جارٍ رفع الصورة وترميم الوجوه ومضاعفة الدقة 2x عبر Modal CPU...");

        try
        {
            OperationTicket ticket = _session.CreateOperationTicket(
                targetNodeId: target.NodeId,
                token: linkedCts.Token,
                targetSlotId: target.SlotId,
                targetPageId: target.PageId,
                expectedSourceAssetId: target.CurrentAssetId);

            CloudUpscaleResult upscale = await _upscaler.EnhanceImageAsync(
                target.CurrentAssetId,
                authSession,
                fidelity,
                linkedCts.Token);

            CloudAuthSessionSnapshot? liveAuth = _authProvider.GetCurrentSession();
            if (liveAuth is null ||
                string.IsNullOrWhiteSpace(liveAuth.AccessToken) ||
                !string.Equals(liveAuth.UserIdentifier, authSession.UserIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return new AiOperationOutcome(false, "تم إلغاء تطبيق الترميم السحابي لأن الحساب تبدل أو تم تسجيل الخروج أثناء المعالجة");
            }

            RecordSuccessfulCloudUsage(liveAuth, upscale.ServerQuotaSnapshot);

            bool applied;
            if (target.SlotId is { } slotId)
            {
                applied = _session.TryApplyAsyncResult(
                    ticket,
                    "ترميم الوجوه ومضاعفة الدقة 2x (سحابي)",
                    doc => CollageOperations.ApplyUpscaledSlotAsset(
                        doc,
                        slotId,
                        upscale.UpscaledAssetId,
                        upscale.SourceWidthPx,
                        upscale.SourceHeightPx,
                        upscale.OutputWidthPx,
                        upscale.OutputHeightPx,
                        syncAutoRepeat: true,
                        targetPageId: ticket.TargetPageId));
            }
            else
            {
                applied = _session.TryApplyAsyncResult(
                    ticket,
                    "ترميم الوجوه ومضاعفة الدقة 2x (سحابي)",
                    doc => FreeformSceneOperations.ApplyProcessedImageAsset(
                        doc,
                        target.NodeId!.Value,
                        upscale.UpscaledAssetId,
                        targetPageId: ticket.TargetPageId));
            }

            return applied
                ? new AiOperationOutcome(
                    true,
                    $"تم ترميم الصورة ومضاعفة الدقة إلى {upscale.OutputWidthPx}×{upscale.OutputHeightPx} px مع حفظ الأصل")
                : new AiOperationOutcome(false, "تم تجاهل نتيجة الترميم لأن العنصر أو المشروع تغير أثناء المعالجة");
        }
        catch (OperationCanceledException)
        {
            return new AiOperationOutcome(false, "تم إلغاء الترميم السحابي فوراً", WasCancelled: true);
        }
        catch (CloudAiException ex)
        {
            return new AiOperationOutcome(false, ex.Message);
        }
        catch (Exception ex)
        {
            return new AiOperationOutcome(false, $"تعذر الترميم السحابي: {ex.Message}");
        }
        finally
        {
            _activeOperationCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    private void RecordSuccessfulCloudUsage(CloudAuthSessionSnapshot session, CloudAiQuotaSnapshot? serverSnapshot)
    {
        DateTimeOffset now = _clock();
        string utcDay = CloudAiQuotaPolicy.GetUtcDayKey(now);

        lock (_quotaSync)
        {
            if (!string.Equals(_localUtcDayKey, utcDay, StringComparison.Ordinal))
            {
                _localUtcDayKey = utcDay;
                _localUsedTodayUtc = 0;
            }

            _localUserIdentifier = session.UserIdentifier;
            _localPlan = session.Plan;
            _localUsedTodayUtc++;

            if (serverSnapshot is not null && serverSnapshot.DailyLimit > 0 && serverSnapshot.UsedToday >= 0)
            {
                _serverQuotaSnapshot = serverSnapshot with { UserIdentifier = session.UserIdentifier };
                _localUsedTodayUtc = Math.Max(_localUsedTodayUtc, serverSnapshot.UsedToday);
            }
        }
    }

    private void OnAuthSessionChanged()
    {
        CloudAuthSessionSnapshot? next = _authProvider.GetCurrentSession();
        bool shouldCancel = false;

        lock (_quotaSync)
        {
            if (next is null || string.IsNullOrWhiteSpace(next.AccessToken))
            {
                _serverQuotaSnapshot = null;
                _localUsedTodayUtc = 0;
                _localUserIdentifier = string.Empty;
                _localPlan = string.Empty;
                shouldCancel = true;
            }
            else if (!string.Equals(_localUserIdentifier, next.UserIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                _serverQuotaSnapshot = null;
                _localUsedTodayUtc = 0;
                _localUserIdentifier = next.UserIdentifier;
                _localPlan = next.Plan;
                shouldCancel = true;
            }
            else if (!string.Equals(_localPlan, next.Plan, StringComparison.OrdinalIgnoreCase))
            {
                _serverQuotaSnapshot = null;
                _localPlan = next.Plan;
            }
        }

        if (shouldCancel)
        {
            CancelActiveOperation();
        }

        StateChanged?.Invoke();
    }

    private void SeedAuthIdentity(CloudAuthSessionSnapshot? session)
    {
        lock (_quotaSync)
        {
            _localUtcDayKey = CloudAiQuotaPolicy.GetUtcDayKey(_clock());
            _localUserIdentifier = session?.UserIdentifier ?? string.Empty;
            _localPlan = session?.Plan ?? string.Empty;
        }
    }

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
        _authProvider.SessionChanged -= OnAuthSessionChanged;
        CancelActiveOperation();
        _gate.Dispose();
    }
}
