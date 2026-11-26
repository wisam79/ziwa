using Ziwa.Application.Abstractions;
using Ziwa.Application.Collage;
using Ziwa.Application.Interaction;
using Ziwa.Application.Session;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.Application.Ai;

public sealed record AiOperationOutcome(bool Succeeded, string MessageAr, bool WasCancelled = false);

public sealed record PreparedMaskEditorContext(
    OperationTicket Ticket,
    MaskEditorPreparedSession Prepared,
    string TargetDisplayName);

public sealed class OfflineAiCoordinator : IDisposable
{
    private readonly DocumentSession _session;
    private readonly IBackgroundRemovalService _backgroundRemover;
    private readonly IFaceFramingService _faceFramer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeOperationCts;
    private bool _disposed;

    public OfflineAiCoordinator(
        DocumentSession session,
        IBackgroundRemovalService backgroundRemover,
        IFaceFramingService faceFramer)
    {
        _session = session;
        _backgroundRemover = backgroundRemover;
        _faceFramer = faceFramer;
    }

    public event Action? StateChanged;

    public bool IsBusy { get; private set; }
    public string BusyStatusAr { get; private set; } = string.Empty;

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

    public async Task<AiOperationOutcome> RemoveBackgroundForSelectionAsync(CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryResolveSelectedPhotoTarget(out AiPhotoTarget target))
        {
            return new AiOperationOutcome(false, "يرجى تحديد صورة أو خانة كولاج تحتوي على صورة أولاً");
        }

        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new AiOperationOutcome(false, "توجد عملية ذكاء اصطناعي جارية حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeOperationCts = linkedCts;
        SetBusy(true, "جارٍ عزل الخلفية محلياً عبر ONNX...");

        try
        {
            OperationTicket ticket = CreateTicket(target, linkedCts.Token, target.OriginalAssetId);
            MaskEstimationResult result = await _backgroundRemover
                .RemoveBackgroundAsync(target.OriginalAssetId, linkedCts.Token);

            bool applied = ApplyCutoutAsset(ticket, target, result.CutoutAssetId, "عزل الخلفية تلقائياً (أوفلاين)");
            return applied
                ? new AiOperationOutcome(true, "تم عزل الخلفية محلياً مع حفظ الأصل للاستعادة")
                : new AiOperationOutcome(false, "تم تجاهل النتيجة لأن العنصر أو المشروع تغير أثناء المعالجة");
        }
        catch (OperationCanceledException)
        {
            return new AiOperationOutcome(false, "تم إلغاء عزل الخلفية فوراً", WasCancelled: true);
        }
        catch (Exception ex)
        {
            return new AiOperationOutcome(false, $"تعذر عزل الخلفية محلياً: {ex.Message}");
        }
        finally
        {
            _activeOperationCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    public async Task<PreparedMaskEditorContext?> PrepareMaskEditorForSelectionAsync(CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryResolveSelectedPhotoTarget(out AiPhotoTarget target))
        {
            return null;
        }

        if (!await _gate.WaitAsync(0, externalCt))
        {
            return null;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeOperationCts = linkedCts;
        SetBusy(true, "جارٍ تجهيز قناع العزل للتحرير اليدوي...");

        try
        {
            OperationTicket ticket = CreateTicket(target, linkedCts.Token, target.OriginalAssetId);
            MaskEditorPreparedSession prepared = await _backgroundRemover
                .PrepareMaskEditorAsync(target.OriginalAssetId, target.CurrentAssetId, linkedCts.Token);

            // Ticket returned to editor stays valid across UI painting until committed (detach linkedCts token).
            OperationTicket editorTicket = ticket with { Token = CancellationToken.None };
            return new PreparedMaskEditorContext(editorTicket, prepared, target.DisplayName);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _activeOperationCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    public async Task<AiOperationOutcome> CommitEditedMaskAsync(
        OperationTicket editorTicket,
        AssetId originalAssetId,
        int widthPx,
        int heightPx,
        ReadOnlyMemory<byte> alphaMask,
        CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new AiOperationOutcome(false, "توجد عملية ذكاء اصطناعي جارية حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeOperationCts = linkedCts;
        SetBusy(true, "جارٍ اعتماد قناع العزل المعدّل...");

        try
        {
            OperationTicket commitTicket = editorTicket with { Token = linkedCts.Token };
            AssetId cutoutAssetId = await _backgroundRemover
                .CommitEditedMaskAsync(originalAssetId, widthPx, heightPx, alphaMask, linkedCts.Token);

            bool applied;
            if (commitTicket.TargetSlotId is { } slotId)
            {
                applied = _session.TryApplyAsyncResult(
                    commitTicket,
                    "تعديل قناع العزل بالفرشاة",
                    doc => CollageOperations.ApplyProcessedSlotAsset(
                        doc,
                        slotId,
                        cutoutAssetId,
                        syncAutoRepeat: true,
                        targetPageId: commitTicket.TargetPageId));
            }
            else if (commitTicket.TargetNodeId is { } nodeId)
            {
                applied = _session.TryApplyAsyncResult(
                    commitTicket,
                    "تعديل قناع العزل بالفرشاة",
                    doc => FreeformSceneOperations.ApplyProcessedImageAsset(
                        doc,
                        nodeId,
                        cutoutAssetId,
                        targetPageId: commitTicket.TargetPageId));
            }
            else
            {
                applied = false;
            }

            return applied
                ? new AiOperationOutcome(true, "تم اعتماد قناع العزل المعدّل في خطوة تراجع واحدة")
                : new AiOperationOutcome(false, "لم يُطبّق القناع لأن العنصر أو المشروع تغير");
        }
        catch (OperationCanceledException)
        {
            return new AiOperationOutcome(false, "تم إلغاء اعتماد القناع", WasCancelled: true);
        }
        catch (Exception ex)
        {
            return new AiOperationOutcome(false, $"تعذر حفظ القناع المعدّل: {ex.Message}");
        }
        finally
        {
            _activeOperationCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    public async Task<AiOperationOutcome> AutoFrameFaceForSelectionAsync(
        IdPhotoFramingPreset preset = IdPhotoFramingPreset.AutoByAspect,
        CancellationToken externalCt = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryResolveSelectedPhotoTarget(out AiPhotoTarget target))
        {
            return new AiOperationOutcome(false, "يرجى تحديد صورة أو خانة كولاج تحتوي على صورة لتأطير الوجه");
        }

        if (!await _gate.WaitAsync(0, externalCt))
        {
            return new AiOperationOutcome(false, "توجد عملية ذكاء اصطناعي جارية حالياً");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _activeOperationCts = linkedCts;
        SetBusy(true, "جارٍ اكتشاف الوجه وضبط كادر الهوية...");

        try
        {
            OperationTicket ticket = CreateTicket(target, linkedCts.Token, target.CurrentAssetId);
            FaceDetectionOutcome detection = await _faceFramer.DetectPrimaryFaceAsync(
                target.CurrentAssetId,
                target.QuarterTurns,
                target.FlipHorizontal,
                target.FlipVertical,
                linkedCts.Token);

            if (!detection.FaceFound || detection.FaceBox is not { } faceBox)
            {
                return new AiOperationOutcome(false, "لم يُكتشف وجه واضح في الصورة — يمكنك ضبط الكادر يدوياً");
            }

            CropSpec framingCrop = IdPhotoFramingCatalog.ComputeFramingCrop(
                detection.ImageWidthPx,
                detection.ImageHeightPx,
                target.FrameWidthMm,
                target.FrameHeightMm,
                faceBox,
                preset);

            bool applied;
            if (target.SlotId is { } slotId)
            {
                applied = _session.TryApplyAsyncResult(
                    ticket,
                    "تأطير الوجه التلقائي لصور الهوية",
                    doc => CollageOperations.ApplyFaceCropToSlot(
                        doc,
                        slotId,
                        framingCrop,
                        syncAutoRepeat: true,
                        targetPageId: ticket.TargetPageId));
            }
            else
            {
                applied = _session.TryApplyAsyncResult(
                    ticket,
                    "تأطير الوجه التلقائي لصور الهوية",
                    doc => FreeformSceneOperations.ApplyAutoFaceFraming(
                        doc,
                        target.NodeId!.Value,
                        framingCrop,
                        targetPageId: ticket.TargetPageId));
            }

            return applied
                ? new AiOperationOutcome(true, "تم توسيط الرأس وتأطير الوجه وفق مقاس الهوية")
                : new AiOperationOutcome(false, "تم تجاهل التأطير لأن العنصر أو المشروع تغير");
        }
        catch (OperationCanceledException)
        {
            return new AiOperationOutcome(false, "تم إلغاء تأطير الوجه", WasCancelled: true);
        }
        catch (Exception ex)
        {
            return new AiOperationOutcome(false, $"تعذر تأطير الوجه: {ex.Message}");
        }
        finally
        {
            _activeOperationCts = null;
            SetBusy(false, string.Empty);
            _gate.Release();
        }
    }

    public bool TryResolveSelectedPhotoTarget(out AiPhotoTarget target)
    {
        PageModel page = _session.CommittedDocument.ActivePage;

        if (_session.SelectedCollageSlotId is { } slotId &&
            page.CollageLayout?.Slots.FirstOrDefault(s => s.Id == slotId) is { AssignedAssetId: { IsEmpty: false } slotAsset } slot)
        {
            bool swapsAxes = slot.QuarterTurns is 1 or 3;
            double logicalW = swapsAxes ? slot.BoundsMm.Height : slot.BoundsMm.Width;
            double logicalH = swapsAxes ? slot.BoundsMm.Width : slot.BoundsMm.Height;
            target = new AiPhotoTarget(
                PageId: page.Id,
                NodeId: null,
                SlotId: slot.Id,
                CurrentAssetId: slotAsset,
                OriginalAssetId: slot.OriginalAssetId ?? slotAsset,
                FrameWidthMm: logicalW,
                FrameHeightMm: logicalH,
                QuarterTurns: slot.QuarterTurns,
                FlipHorizontal: slot.FlipHorizontal,
                FlipVertical: slot.FlipVertical,
                DisplayName: $"خانة كولاج #{slot.Index + 1}");
            return true;
        }

        if (_session.SelectedNodeIds.Length == 1)
        {
            NodeId id = _session.SelectedNodeIds[0];
            if (page.Nodes.FirstOrDefault(n => n.Id == id) is ImageNode img && !img.IsLocked)
            {
                target = new AiPhotoTarget(
                    PageId: page.Id,
                    NodeId: img.Id,
                    SlotId: null,
                    CurrentAssetId: img.CurrentAssetId,
                    OriginalAssetId: img.OriginalAssetId,
                    FrameWidthMm: img.BoundsMm.Width,
                    FrameHeightMm: img.BoundsMm.Height,
                    QuarterTurns: 0,
                    FlipHorizontal: false,
                    FlipVertical: false,
                    DisplayName: img.Name);
                return true;
            }
        }

        target = default;
        return false;
    }

    private OperationTicket CreateTicket(AiPhotoTarget target, CancellationToken ct, AssetId expectedAssetId) =>
        _session.CreateOperationTicket(
            targetNodeId: target.NodeId,
            token: ct,
            targetSlotId: target.SlotId,
            targetPageId: target.PageId,
            expectedSourceAssetId: expectedAssetId);

    private bool ApplyCutoutAsset(OperationTicket ticket, AiPhotoTarget target, AssetId cutoutAssetId, string descriptionAr)
    {
        if (target.SlotId is { } slotId)
        {
            return _session.TryApplyAsyncResult(
                ticket,
                descriptionAr,
                doc => CollageOperations.ApplyProcessedSlotAsset(
                    doc,
                    slotId,
                    cutoutAssetId,
                    syncAutoRepeat: true,
                    targetPageId: ticket.TargetPageId));
        }

        return _session.TryApplyAsyncResult(
            ticket,
            descriptionAr,
            doc => FreeformSceneOperations.ApplyProcessedImageAsset(
                doc,
                target.NodeId!.Value,
                cutoutAssetId,
                targetPageId: ticket.TargetPageId));
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
        CancelActiveOperation();
        _gate.Dispose();
    }

    public readonly record struct AiPhotoTarget(
        PageId PageId,
        NodeId? NodeId,
        SlotId? SlotId,
        AssetId CurrentAssetId,
        AssetId OriginalAssetId,
        double FrameWidthMm,
        double FrameHeightMm,
        int QuarterTurns,
        bool FlipHorizontal,
        bool FlipVertical,
        string DisplayName);
}
