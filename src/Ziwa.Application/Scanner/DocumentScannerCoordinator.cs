using System.Collections.Immutable;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Session;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Domain.Scanner;

namespace Ziwa.Application.Scanner;

public sealed record ScannerDetectionOutcome(
    bool Succeeded,
    ScanDetectionResult? Detection,
    string MessageAr,
    bool WasCancelled = false);

public sealed record ScannerInsertionOutcome(
    bool Succeeded,
    int InsertedCount,
    string MessageAr,
    bool WasCancelled = false);

public sealed class DocumentScannerCoordinator : IDisposable
{
    private readonly DocumentSession _session;
    private readonly IDocumentScannerService _scanner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeCts;
    private bool _disposed;

    public DocumentScannerCoordinator(DocumentSession session, IDocumentScannerService scanner)
    {
        _session = session;
        _scanner = scanner;
    }

    public event Action? StateChanged;

    public bool IsProcessing { get; private set; }
    public string ProcessingStatusAr { get; private set; } = string.Empty;

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

    public async Task<ScannerDetectionOutcome> DetectAsync(
        AssetId sourceAssetId,
        ScanDetectionMode mode = ScanDetectionMode.AutoSingleOrMulti,
        CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceAssetId.IsEmpty)
        {
            return new ScannerDetectionOutcome(false, null, "لم يتم تحديد صورة صالحة للمسح الضوئي");
        }

        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new ScannerDetectionOutcome(false, null, "توجد عملية مسح ضوئي قيد التنفيذ حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeCts = linkedCts;
        SetBusy(true, "جارٍ الكشف التلقائي عن حواف المستندات والبطاقات...");

        try
        {
            ScanDetectionResult result = await _scanner.DetectDocumentsAsync(sourceAssetId, mode, linkedCts.Token);
            linkedCts.Token.ThrowIfCancellationRequested();
            return new ScannerDetectionOutcome(
                true,
                result,
                $"تم اكتشاف {result.Regions.Length} إطار مستند/بطاقة");
        }
        catch (OperationCanceledException)
        {
            return new ScannerDetectionOutcome(false, null, "تم إلغاء كشف الحواف فوراً", WasCancelled: true);
        }
        catch (Exception ex)
        {
            return new ScannerDetectionOutcome(false, null, $"تعذر الكشف عن الحواف: {ex.Message}");
        }
        finally
        {
            _activeCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    public async Task<ScannerInsertionOutcome> ExtractAndInsertToFreeformAsync(
        AssetId sourceAssetId,
        IReadOnlyList<ScanExtractionRequestItem> items,
        CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (items is null || items.Count == 0)
        {
            return new ScannerInsertionOutcome(false, 0, "لا توجد إطارات محددة للاستخراج");
        }

        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new ScannerInsertionOutcome(false, 0, "توجد عملية مسح ضوئي قيد التنفيذ حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeCts = linkedCts;
        SetBusy(true, "جارٍ استعدال المنظور وتطبيق فلاتر المستندات...");

        try
        {
            PageModel targetPage = _session.CommittedDocument.ActivePage;
            OperationTicket ticket = _session.CreateOperationTicket(
                targetNodeId: null,
                token: linkedCts.Token,
                targetSlotId: null,
                targetPageId: targetPage.Id);

            ImmutableArray<ExtractedScanDocument> extracted = await _scanner.ExtractDocumentsAsync(
                sourceAssetId,
                items,
                linkedCts.Token);
            linkedCts.Token.ThrowIfCancellationRequested();

            ImmutableArray<NodeId> insertedIds = [];
            bool applied = _session.TryApplyAsyncResult(
                ticket,
                $"إدراج {extracted.Length} مستند مستعدل في الوضع الحر",
                doc => ScannerDocumentOperations.InsertExtractedIntoFreeform(doc, extracted, out insertedIds, ticket.TargetPageId));

            if (applied && !insertedIds.IsDefaultOrEmpty)
            {
                _session.SetSelection(insertedIds);
            }

            return applied
                ? new ScannerInsertionOutcome(true, extracted.Length, $"تم إدراج {extracted.Length} مستند مستعدل في خطوة تراجع واحدة")
                : new ScannerInsertionOutcome(false, 0, "لم تُدرج المستندات لأن المشروع أو الصفحة تغيرت أثناء المعالجة");
        }
        catch (OperationCanceledException)
        {
            return new ScannerInsertionOutcome(false, 0, "تم إلغاء استعدال المستندات", WasCancelled: true);
        }
        catch (Exception ex)
        {
            return new ScannerInsertionOutcome(false, 0, $"تعذر استخراج المستندات: {ex.Message}");
        }
        finally
        {
            _activeCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    public async Task<ScannerInsertionOutcome> ExtractAndInsertToCollageAsync(
        AssetId sourceAssetId,
        IReadOnlyList<ScanExtractionRequestItem> items,
        SlotId? preferredSlotId = null,
        CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (items is null || items.Count == 0)
        {
            return new ScannerInsertionOutcome(false, 0, "لا توجد إطارات محددة للاستخراج");
        }

        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new ScannerInsertionOutcome(false, 0, "توجد عملية مسح ضوئي قيد التنفيذ حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeCts = linkedCts;
        SetBusy(true, "جارٍ استعدال البطاقات وإدراجها في خانات الكولاج...");

        try
        {
            PageModel targetPage = _session.CommittedDocument.ActivePage;
            SlotId? resolvedPreferred = preferredSlotId ?? _session.SelectedCollageSlotId;
            OperationTicket ticket = _session.CreateOperationTicket(
                targetNodeId: null,
                token: linkedCts.Token,
                targetSlotId: null,
                targetPageId: targetPage.Id);

            ImmutableArray<ExtractedScanDocument> extracted = await _scanner.ExtractDocumentsAsync(
                sourceAssetId,
                items,
                linkedCts.Token);
            linkedCts.Token.ThrowIfCancellationRequested();

            bool applied = _session.TryApplyAsyncResult(
                ticket,
                $"إدراج {extracted.Length} بطاقة مستعدلة في الكولاج",
                doc => ScannerDocumentOperations.InsertExtractedIntoCollage(doc, extracted, resolvedPreferred, ticket.TargetPageId));

            return applied
                ? new ScannerInsertionOutcome(true, extracted.Length, $"تم إدراج {extracted.Length} بطاقة مستعدلة في خانات الكولاج بخطوة تراجع واحدة")
                : new ScannerInsertionOutcome(false, 0, "لم تُدرج البطاقات لأن المشروع أو الصفحة تغيرت أثناء المعالجة");
        }
        catch (OperationCanceledException)
        {
            return new ScannerInsertionOutcome(false, 0, "تم إلغاء استعدال البطاقات", WasCancelled: true);
        }
        catch (Exception ex)
        {
            return new ScannerInsertionOutcome(false, 0, $"تعذر استخراج البطاقات: {ex.Message}");
        }
        finally
        {
            _activeCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    private void SetBusy(bool busy, string statusAr)
    {
        IsProcessing = busy;
        ProcessingStatusAr = statusAr;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelActiveOperation();
        _gate.Dispose();
    }
}
