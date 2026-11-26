using Ziwa.Application.Session;
using Ziwa.Domain.Models;

namespace Ziwa.Application.Persistence;

public sealed class ProjectWorkspace : IDisposable
{
    private readonly DocumentSession _session;
    private readonly IProjectStore _store;
    private readonly IProjectAssetStore _assets;
    private readonly IProjectLibrary _library;
    private readonly ICrashGuard _crashGuard;
    private readonly IProjectDialogs _dialogs;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeSpan _autosaveDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _autosave;
    private CancellationTokenSource? _operation;
    private ZiwaDocument _observedDocument;
    private string? _savedFingerprint;
    private long _observedGeneration;
    private string? _restoredRecoveryId;
    private bool _started;
    private bool _replacing;
    private bool _disposed;

    public ProjectWorkspace(
        DocumentSession session,
        IProjectStore store,
        IProjectAssetStore assets,
        IProjectLibrary library,
        ICrashGuard crashGuard,
        IProjectDialogs dialogs,
        TimeSpan? autosaveDelay = null)
    {
        _session = session;
        _store = store;
        _assets = assets;
        _library = library;
        _crashGuard = crashGuard;
        _dialogs = dialogs;
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromSeconds(5);
        _observedDocument = session.CommittedDocument;
        _observedGeneration = session.Generation;
        _savedFingerprint = Fingerprint(_observedDocument);
        session.SessionChanged += OnSessionChanged;
    }

    public event Action? Changed;

    public string? CurrentPath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsStarted => _started;
    public string StatusMessage { get; private set; } = string.Empty;
    public IReadOnlyList<ProjectLibraryEntry> Projects { get; private set; } = [];
    public IReadOnlyList<RecoveryEntry> Recoveries { get; private set; } = [];

    public Task<bool> InitializeAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        if (_started)
        {
            return true;
        }

        Recoveries = await _crashGuard.StartAsync(token);
        _started = true;
        Projects = await _library.ListAsync(token);
        StatusMessage = Recoveries.Count > 0 ? "توجد مشاريع قابلة للتعافي — اختر استعادة أو الاحتفاظ للاحق" : "مكتبة المشاريع جاهزة";
        if (IsDirty)
        {
            ScheduleAutosave();
        }

        return true;
    }, ct);

    public Task<bool> SaveAsync(bool saveAs = false, CancellationToken ct = default) =>
        RunAsync(token => SaveCoreAsync(saveAs, token), ct);

    public Task<bool> OpenAsync(string? path = null, CancellationToken ct = default) => RunAsync(async token =>
    {
        path ??= await _dialogs.PickOpenProjectAsync(token);
        if (path is null || !await CanLeaveCoreAsync(token))
        {
            return false;
        }

        long generation = _session.Generation;
        long revision = _session.Revision;
        ProjectSnapshot snapshot = await _store.LoadAsync(path, token);
        token.ThrowIfCancellationRequested();
        if (generation != _session.Generation || revision != _session.Revision)
        {
            StatusMessage = "لم يُفتح الملف لأن المشروع تغير أثناء القراءة";
            return false;
        }

        _assets.Restore(snapshot);
        Replace(snapshot.Document, path, saved: true);
        await _crashGuard.ClearCurrentAsync(token);
        await RecordCoreAsync(snapshot.Document, path, token);
        StatusMessage = string.IsNullOrEmpty(StatusMessage) ? "تم فتح المشروع بأصوله الأصلية" : StatusMessage;
        return true;
    }, ct);

    public Task<bool> ReplaceAsync(ZiwaDocument document, CancellationToken ct = default) => RunAsync(async token =>
    {
        if (!await CanLeaveCoreAsync(token))
        {
            return false;
        }

        long generation = _session.Generation;
        long revision = _session.Revision;
        await _crashGuard.ClearCurrentAsync(token);
        token.ThrowIfCancellationRequested();
        if (generation != _session.Generation || revision != _session.Revision)
        {
            StatusMessage = "تغير المشروع أثناء التحضير — لم يُستبدل العمل";
            return false;
        }

        Replace(document, null, saved: false);
        StatusMessage = "مشروع جديد — احفظه بصيغة .ziwa";
        return true;
    }, ct);

    public Task<bool> RestoreAsync(RecoveryEntry entry, CancellationToken ct = default) => RunAsync(async token =>
    {
        if (!await CanLeaveCoreAsync(token))
        {
            return false;
        }

        long generation = _session.Generation;
        long revision = _session.Revision;
        ProjectSnapshot snapshot = await _crashGuard.LoadRecoveryAsync(entry.Id, token);
        token.ThrowIfCancellationRequested();
        if (generation != _session.Generation || revision != _session.Revision)
        {
            StatusMessage = "أُلغيت الاستعادة لأن المشروع تغير";
            return false;
        }

        _assets.Restore(snapshot);
        Replace(snapshot.Document, null, saved: false);
        _restoredRecoveryId = entry.Id;
        await _crashGuard.CaptureAsync(_assets.Capture(_session.CommittedDocument), null, token);
        StatusMessage = "تمت استعادة المشروع — احفظه باسم جديد؛ لقطة التعافي محفوظة حتى نجاح الحفظ";
        return true;
    }, ct);

    public Task<bool> DiscardRecoveryAsync(RecoveryEntry entry, CancellationToken ct = default) => RunAsync(async token =>
    {
        await _crashGuard.DiscardRecoveryAsync(entry.Id, token);
        Recoveries = Recoveries.Where(r => r.Id != entry.Id).ToArray();
        StatusMessage = "تم تجاهل لقطة التعافي المختارة";
        return true;
    }, ct);

    public Task<bool> RefreshLibraryAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        Projects = await _library.ListAsync(token);
        return true;
    }, ct);

    public Task<bool> ExportBackupAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        string? path = await _dialogs.PickSaveBackupAsync(token);
        if (path is null)
        {
            return false;
        }

        await _library.ExportBackupAsync(path, token);
        StatusMessage = "تم تصدير نسخة احتياطية من المكتبة والمشاريع";
        return true;
    }, ct);

    public Task<bool> ImportBackupAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        string? path = await _dialogs.PickOpenBackupAsync(token);
        if (path is null)
        {
            return false;
        }

        int count = await _library.ImportBackupAsync(path, token);
        Projects = await _library.ListAsync(token);
        StatusMessage = $"تم استيراد {count} مشروع إلى مجلد مستقل دون استبدال مشاريعك";
        return true;
    }, ct);

    public Task<bool> RequestCloseAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        if (!await CanLeaveCoreAsync(token))
        {
            return false;
        }

        _autosave?.Cancel();
        await _crashGuard.CompleteAsync(token);
        _started = false;
        return true;
    }, ct);

    public Task<bool> CaptureRecoveryAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        if (!_started)
        {
            return true;
        }

        if (!IsDirty)
        {
            await _crashGuard.ClearCurrentAsync(token);
            return true;
        }

        long generation = _session.Generation;
        ProjectSnapshot snapshot = _assets.Capture(_session.CommittedDocument);
        await _crashGuard.CaptureAsync(snapshot, CurrentPath, token);
        if (generation == _session.Generation)
        {
            StatusMessage = "حُفظت لقطة التعافي تلقائياً — ملف المشروع الأصلي لم يتغير";
        }

        return true;
    }, ct, showBusy: false);

    public void CancelOperation() => _operation?.Cancel();

    private async Task<bool> SaveCoreAsync(bool saveAs, CancellationToken token)
    {
        _session.CommitInteraction();
        string? path = !saveAs ? CurrentPath : null;
        path ??= await _dialogs.PickSaveProjectAsync(_session.CommittedDocument.Title, CurrentPath, token);
        if (path is null)
        {
            return false;
        }

        long generation = _session.Generation;
        ProjectSnapshot snapshot = _assets.Capture(_session.CommittedDocument);
        string fingerprint = Fingerprint(snapshot.Document);
        await _store.SaveAsync(path, snapshot, token);
        if (generation != _session.Generation)
        {
            StatusMessage = "حُفظت اللقطة السابقة؛ المشروع الحالي لم يتغير";
            return false;
        }

        CurrentPath = path;
        _savedFingerprint = fingerprint;
        UpdateDirty();
        StatusMessage = "تم حفظ المشروع وجميع أصوله بأمان";
        // The file is committed. Index/recovery maintenance must not turn a successful save into a failure.
        try
        {
            await RecordCoreAsync(snapshot.Document, path, CancellationToken.None);
            if (!IsDirty)
            {
                await _crashGuard.ClearCurrentAsync(CancellationToken.None);
                if (_restoredRecoveryId is { } recoveryId)
                {
                    await _crashGuard.DiscardRecoveryAsync(recoveryId, CancellationToken.None);
                    Recoveries = Recoveries.Where(r => r.Id != recoveryId).ToArray();
                    _restoredRecoveryId = null;
                }
            }
            else
            {
                ScheduleAutosave();
                StatusMessage = "حُفظت اللقطة؛ توجد تعديلات أحدث لم تُحفظ بعد";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"تم حفظ الملف؛ تعذرت صيانة المكتبة أو التعافي: {ex.Message}";
        }

        return true;
    }

    private async Task<bool> CanLeaveCoreAsync(CancellationToken token)
    {
        _session.CommitInteraction();
        if (!IsDirty)
        {
            return true;
        }

        long generation = _session.Generation;
        long revision = _session.Revision;
        UnsavedChangesChoice choice = await _dialogs.ConfirmUnsavedChangesAsync(token);
        token.ThrowIfCancellationRequested();
        if (generation != _session.Generation || revision != _session.Revision)
        {
            StatusMessage = "تغير المشروع أثناء التأكيد — لم يُستبدل العمل";
            return false;
        }

        if (choice == UnsavedChangesChoice.Cancel)
        {
            return false;
        }

        if (choice == UnsavedChangesChoice.Save)
        {
            return await SaveCoreAsync(saveAs: false, token) && !IsDirty;
        }

        return true;
    }

    private async Task RecordCoreAsync(ZiwaDocument document, string path, CancellationToken token)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            await _library.RecordAsync(new ProjectLibraryEntry(path, document.Id, document.Title, now, now), token);
            Projects = await _library.ListAsync(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"الملف صالح؛ تعذر تحديث مكتبة المشاريع: {ex.Message}";
        }
    }

    private void Replace(ZiwaDocument document, string? path, bool saved)
    {
        _autosave?.Cancel();
        _restoredRecoveryId = null;
        CurrentPath = path;
        _savedFingerprint = saved ? Fingerprint(document) : null;
        _replacing = true;
        try
        {
            _session.ReplaceDocument(document);
        }
        finally
        {
            _replacing = false;
        }

        _observedGeneration = _session.Generation;
        _observedDocument = document;
        UpdateDirty();
        if (IsDirty && _started)
        {
            ScheduleAutosave();
        }
    }

    private void OnSessionChanged()
    {
        if (_replacing || (ReferenceEquals(_observedDocument, _session.CommittedDocument) && _observedGeneration == _session.Generation))
        {
            return;
        }

        if (_observedGeneration != _session.Generation)
        {
            CurrentPath = null;
            _savedFingerprint = null;
            _restoredRecoveryId = null;
        }

        _observedGeneration = _session.Generation;
        _observedDocument = _session.CommittedDocument;
        UpdateDirty();
        Changed?.Invoke();
        if (_started)
        {
            ScheduleAutosave();
        }
    }

    private void UpdateDirty() => IsDirty = _savedFingerprint is null || _savedFingerprint != Fingerprint(_session.CommittedDocument);

    private static string Fingerprint(ZiwaDocument document) =>
        ProjectDocumentSerializer.CanonicalFingerprint(document with { ActivePageIndex = 0, UpdatedAtUtc = default });

    private void ScheduleAutosave()
    {
        _autosave?.Cancel();
        _autosave?.Dispose();
        _autosave = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = AutosaveAfterDelayAsync(_autosave.Token);
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_autosaveDelay, token);
            while (!token.IsCancellationRequested && _started)
            {
                if (await CaptureRecoveryAsync(token))
                {
                    break;
                }

                await Task.Delay(_autosaveDelay, token);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer committed change or shutdown superseded this autosave.
        }
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task<bool>> action, CancellationToken ct, bool showBusy = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationGate.WaitAsync(0, ct))
        {
            return false;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        _operation = operation;
        IsBusy = showBusy;
        StatusMessage = string.Empty;
        Changed?.Invoke();
        try
        {
            return await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "أُلغيت العملية — لم يُستبدل العمل الحالي";
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"تعذرت العملية: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            _operation = null;
            _operationGate.Release();
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.SessionChanged -= OnSessionChanged;
        _lifetime.Cancel();
        _autosave?.Cancel();
        _autosave?.Dispose();
        _lifetime.Dispose();
    }
}
