using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using Ziwa.Application.Abstractions;
using Ziwa.Application.Ai;
using Ziwa.Application.Collage;
using Ziwa.Application.Commands;
using Ziwa.Application.Interaction;
using Ziwa.Application.Persistence;
using Ziwa.Domain.Ai;
using Ziwa.Domain.Models;
using Ziwa.Domain.PrePress;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering.Ai;
using Ziwa.Rendering.Assets;

namespace Ziwa.App.ViewModels;

public sealed record MaskPreviewModeOption(MaskPreviewMode Mode, string TitleAr);

public sealed record IdPhotoFramingPresetOption(IdPhotoFramingPreset Preset, string TitleAr);

public sealed partial class MainWindowViewModel
{
    private OfflineAiCoordinator? _aiCoordinator;
    private IImageAssetImporter? _imageImporter;
    private AssetStore? _assetStore;
    private IProjectDialogs? _dialogs;

    private PreparedMaskEditorContext? _preparedMaskContext;
    private EditableAlphaMask? _activeMaskSession;
    private SKBitmap? _maskEditorOriginalBitmap;
    private SKBitmap? _maskEditorCutoutBitmap;

    [ObservableProperty]
    private bool _isMaskEditorOpen;

    [ObservableProperty]
    private string _maskEditorTargetTitle = string.Empty;

    [ObservableProperty]
    private MaskBrushMode _selectedMaskBrushMode = MaskBrushMode.Erase;

    [ObservableProperty]
    private MaskPreviewMode _selectedMaskPreviewMode = MaskPreviewMode.AfterCheckerboard;

    [ObservableProperty]
    private MaskPreviewModeOption _selectedMaskPreviewModeOption = new(MaskPreviewMode.AfterCheckerboard, "بعد العزل (خلفية شفافة)");

    [ObservableProperty]
    private double _maskBrushRadiusPx = 28.0;

    [ObservableProperty]
    private double _maskBrushSoftness = 0.55;

    [ObservableProperty]
    private double _maskSplitRatio = 0.50;

    [ObservableProperty]
    private IdPhotoFramingPresetOption _selectedFramingPresetOption = new(IdPhotoFramingPreset.AutoByAspect, IdPhotoFramingCatalog.GetDisplayTitleAr(IdPhotoFramingPreset.AutoByAspect));

    public IReadOnlyList<MaskPreviewModeOption> AvailableMaskPreviewModes { get; } =
    [
        new(MaskPreviewMode.AfterCheckerboard, "بعد العزل (خلفية شفافة)"),
        new(MaskPreviewMode.SplitBeforeAfter, "مقارنة منقسمة (قبل / بعد)"),
        new(MaskPreviewMode.RubyOverlay, "تظليل أحمر للمناطق المعزولة"),
        new(MaskPreviewMode.BeforeOriginal, "الصورة الأصلية (قبل)"),
    ];

    public IReadOnlyList<IdPhotoFramingPresetOption> AvailableFramingPresets { get; } =
        IdPhotoFramingCatalog.All
            .Select(p => new IdPhotoFramingPresetOption(p, IdPhotoFramingCatalog.GetDisplayTitleAr(p)))
            .ToArray();

    public EditableAlphaMask? ActiveMaskSession => _activeMaskSession;
    public SKBitmap? MaskEditorOriginalBitmap => _maskEditorOriginalBitmap;
    public SKBitmap? MaskEditorCutoutBitmap => _maskEditorCutoutBitmap;

    public bool IsAiBusy => _aiCoordinator?.IsBusy == true;
    public bool IsAiIdle => !IsAiBusy;
    public string AiBusyStatusText => _aiCoordinator?.BusyStatusAr ?? string.Empty;

    public bool IsCollageSlotWithImageSelected =>
        IsCollageSlotSelected && SelectedCollageSlot?.AssignedAssetId is { IsEmpty: false };

    public bool IsPhotoAiTargetVisible => (IsImageSelected || IsCollageSlotWithImageSelected) && !IsStickerSelected;
    public bool CanRestoreOriginalTarget => IsImageSelected || IsCollageSlotWithImageSelected;

    public bool CanUndoMaskStroke => _activeMaskSession?.CanUndo == true;
    public bool CanRedoMaskStroke => _activeMaskSession?.CanRedo == true;
    public bool IsSplitMaskPreview => SelectedMaskPreviewMode == MaskPreviewMode.SplitBeforeAfter;
    public string MaskBrushRadiusText => $"حجم الفرشاة: {MaskBrushRadiusPx:0} px";
    public string MaskBrushSoftnessText => $"نعومة الحافة: {MaskBrushSoftness * 100.0:0}%";
    public string MaskEditorBrushSummary =>
        SelectedMaskBrushMode == MaskBrushMode.Erase
            ? "الوضع الحالي: فرشاة المسح — اسحب لمسح الخلفية"
            : "الوضع الحالي: فرشاة الاستعادة — اسحب لإرجاع التفاصيل الأصلية";

    private void InitializeAiIntegration(
        OfflineAiCoordinator? aiCoordinator,
        IImageAssetImporter? imageImporter,
        AssetStore? assetStore,
        IProjectDialogs? dialogs)
    {
        _aiCoordinator = aiCoordinator;
        _imageImporter = imageImporter;
        _assetStore = assetStore;
        _dialogs = dialogs;

        if (_aiCoordinator is not null)
        {
            _aiCoordinator.StateChanged += OnAiCoordinatorStateChanged;
        }

        RegisterAiCommands();
    }

    private void RegisterAiCommands()
    {
        _commandRegistry.Register(new CommandDefinition(
            new CommandId("insert.image"),
            "إدراج صورة من الجهاز…",
            "Ctrl+Shift+I",
            "إدراج",
            () => _imageImporter is not null && _dialogs is not null,
            ImportImageCoreAsync));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("ai.removeBackground"),
            "عزل الخلفية تلقائياً (أوفلاين)",
            "Ctrl+Shift+B",
            "ذكاء اصطناعي",
            () => _aiCoordinator is not null && IsPhotoAiTargetVisible && IsAiIdle,
            RemoveBackgroundCoreAsync));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("ai.editMask"),
            "تعديل قناع العزل بالفرشاة (قبل/بعد)",
            string.Empty,
            "ذكاء اصطناعي",
            () => _aiCoordinator is not null && _assetStore is not null && IsPhotoAiTargetVisible && IsAiIdle,
            OpenMaskEditorCoreAsync));

        _commandRegistry.Register(new CommandDefinition(
            new CommandId("ai.autoFrameFace"),
            "تأطير الوجه التلقائي لصور الهوية",
            "Ctrl+Shift+F",
            "ذكاء اصطناعي",
            () => _aiCoordinator is not null && IsPhotoAiTargetVisible && IsAiIdle,
            AutoFrameFaceCoreAsync));
    }

    partial void OnSelectedMaskPreviewModeOptionChanged(MaskPreviewModeOption value)
    {
        if (value is not null && SelectedMaskPreviewMode != value.Mode)
        {
            SelectedMaskPreviewMode = value.Mode;
        }
    }

    partial void OnSelectedMaskPreviewModeChanged(MaskPreviewMode value)
    {
        OnPropertyChanged(nameof(IsSplitMaskPreview));
        MaskPreviewModeOption? matching = AvailableMaskPreviewModes.FirstOrDefault(o => o.Mode == value);
        if (matching is not null && !Equals(SelectedMaskPreviewModeOption, matching))
        {
            SelectedMaskPreviewModeOption = matching;
        }
    }

    partial void OnSelectedMaskBrushModeChanged(MaskBrushMode value) =>
        OnPropertyChanged(nameof(MaskEditorBrushSummary));

    partial void OnMaskBrushRadiusPxChanged(double value) =>
        OnPropertyChanged(nameof(MaskBrushRadiusText));

    partial void OnMaskBrushSoftnessChanged(double value) =>
        OnPropertyChanged(nameof(MaskBrushSoftnessText));

    public void NotifyMaskEditorModified()
    {
        OnPropertyChanged(nameof(CanUndoMaskStroke));
        OnPropertyChanged(nameof(CanRedoMaskStroke));
    }

    [RelayCommand]
    private Task ImportImageAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("insert.image"), ct);

    [RelayCommand]
    private Task RemoveBackgroundAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("ai.removeBackground"), ct);

    [RelayCommand]
    private Task OpenMaskEditorAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("ai.editMask"), ct);

    [RelayCommand]
    private Task AutoFrameFaceAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("ai.autoFrameFace"), ct);

    [RelayCommand]
    private void CancelAiOperation()
    {
        _aiCoordinator?.CancelActiveOperation();
        StatusMessage = "جارٍ إلغاء عملية الذكاء الاصطناعي...";
    }

    [RelayCommand]
    private void SetEraseMaskBrush() => SelectedMaskBrushMode = MaskBrushMode.Erase;

    [RelayCommand]
    private void SetRestoreMaskBrush() => SelectedMaskBrushMode = MaskBrushMode.Restore;

    [RelayCommand]
    private void UndoMaskStroke()
    {
        if (_activeMaskSession is null || _maskEditorOriginalBitmap is null || _maskEditorCutoutBitmap is null)
        {
            return;
        }

        if (_activeMaskSession.Undo() is { } dirty && !dirty.IsEmpty)
        {
            AlphaMaskCompositor.ApplyAlphaToBitmapInPlace(
                _maskEditorCutoutBitmap,
                _maskEditorOriginalBitmap,
                _activeMaskSession.AlphaSpan,
                dirty);
            RefreshMaskEditorBindings();
        }
    }

    [RelayCommand]
    private void RedoMaskStroke()
    {
        if (_activeMaskSession is null || _maskEditorOriginalBitmap is null || _maskEditorCutoutBitmap is null)
        {
            return;
        }

        if (_activeMaskSession.Redo() is { } dirty && !dirty.IsEmpty)
        {
            AlphaMaskCompositor.ApplyAlphaToBitmapInPlace(
                _maskEditorCutoutBitmap,
                _maskEditorOriginalBitmap,
                _activeMaskSession.AlphaSpan,
                dirty);
            RefreshMaskEditorBindings();
        }
    }

    [RelayCommand]
    private void ResetMaskToAuto()
    {
        if (_activeMaskSession is null || _maskEditorOriginalBitmap is null || _maskEditorCutoutBitmap is null)
        {
            return;
        }

        if (_activeMaskSession.ResetToInitialAiMask() is { } dirty && !dirty.IsEmpty)
        {
            AlphaMaskCompositor.ApplyAlphaToBitmapInPlace(
                _maskEditorCutoutBitmap,
                _maskEditorOriginalBitmap,
                _activeMaskSession.AlphaSpan,
                dirty);
            RefreshMaskEditorBindings();
            StatusMessage = "تمت إعادة القناع إلى نتيجة العزل التلقائي";
        }
    }

    [RelayCommand]
    private async Task ApplyMaskEditorAsync(CancellationToken ct)
    {
        if (_aiCoordinator is null || _preparedMaskContext is null || _activeMaskSession is null)
        {
            CloseMaskEditor();
            return;
        }

        byte[] alphaCopy = _activeMaskSession.GetAlphaCopy();
        int width = _activeMaskSession.Width;
        int height = _activeMaskSession.Height;
        PreparedMaskEditorContext ctx = _preparedMaskContext;

        CloseMaskEditor();
        AiOperationOutcome outcome = await _aiCoordinator.CommitEditedMaskAsync(
            ctx.Ticket,
            ctx.Prepared.OriginalAssetId,
            width,
            height,
            alphaCopy,
            ct);
        StatusMessage = outcome.MessageAr;
    }

    [RelayCommand]
    private void CloseMaskEditor()
    {
        IsMaskEditorOpen = false;
        _preparedMaskContext = null;
        _activeMaskSession = null;
        SKBitmap? oldOriginal = _maskEditorOriginalBitmap;
        SKBitmap? oldCutout = _maskEditorCutoutBitmap;
        _maskEditorOriginalBitmap = null;
        _maskEditorCutoutBitmap = null;
        RefreshMaskEditorBindings();
        DisposeBitmapAfterCompositorPass(oldOriginal);
        DisposeBitmapAfterCompositorPass(oldCutout);
    }

    private async Task ImportImageCoreAsync(CancellationToken ct)
    {
        if (_imageImporter is null || _dialogs is null)
        {
            StatusMessage = "خدمة استيراد الصور غير مهيأة";
            return;
        }

        string? filePath = await _dialogs.PickOpenImageAsync(ct);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            AssetId importedId = await _imageImporter.ImportImageFileAsync(filePath, ct);
            if (IsCollageSlotSelected && _session.SelectedCollageSlotId is { } sid)
            {
                _session.ApplyCommand("تعيين صورة في خانة الكولاج", doc => CollageOperations.AssignAssetToSlot(doc, sid, importedId));
                StatusMessage = "تم إدراج الصورة في خانة الكولاج";
                return;
            }

            if (ActivePage.CollageLayout is not null)
            {
                _session.ApplyCommand("إضافة صورة لقائمة انتظار الكولاج", doc =>
                {
                    ZiwaDocument enqueued = CollageOperations.EnqueuePendingAssets(doc, [importedId]);
                    return CollageOperations.AutoDistributePendingToSlots(enqueued);
                });
                StatusMessage = "تمت إضافة الصورة إلى شيت الكولاج";
                return;
            }

            AssetPixelDimensions? dims = _assetRegistrar?.TryGetAssetDimensions(importedId);
            double aspect = dims is { WidthPx: > 0, HeightPx: > 0 } d ? (double)d.WidthPx / d.HeightPx : 1.0;
            double targetW = Math.Clamp(ActivePage.WidthMm * 0.45, 24.0, Math.Max(24.0, ActivePage.WidthMm - 16.0));
            double targetH = Math.Clamp(targetW / Math.Max(0.2, aspect), 20.0, Math.Max(20.0, ActivePage.HeightMm - 16.0));
            var node = new ImageNode(
                Id: NodeId.New(),
                Name: Path.GetFileNameWithoutExtension(filePath),
                BoundsMm: new RectMm(
                    Math.Max(4.0, (ActivePage.WidthMm - targetW) * 0.5),
                    Math.Max(4.0, (ActivePage.HeightMm - targetH) * 0.5),
                    targetW,
                    targetH),
                CurrentAssetId: importedId,
                OriginalAssetId: importedId,
                Crop: CropSpec.Identity);
            _session.ApplyCommand("إدراج صورة", doc => FreeformSceneOperations.AddNode(doc, node));
            _session.SetSelection([node.Id]);
            StatusMessage = "تم إدراج الصورة في الورقة مع حفظ الأصل";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "تم إلغاء استيراد الصورة";
        }
        catch (Exception ex)
        {
            StatusMessage = $"تعذر استيراد الصورة: {ex.Message}";
        }
    }

    private async Task RemoveBackgroundCoreAsync(CancellationToken ct)
    {
        if (_aiCoordinator is null)
        {
            return;
        }

        AiOperationOutcome outcome = await _aiCoordinator.RemoveBackgroundForSelectionAsync(ct);
        StatusMessage = outcome.MessageAr;
    }

    private async Task OpenMaskEditorCoreAsync(CancellationToken ct)
    {
        if (_aiCoordinator is null || _assetStore is null)
        {
            return;
        }

        PreparedMaskEditorContext? prepared = await _aiCoordinator.PrepareMaskEditorForSelectionAsync(ct);
        if (prepared is null)
        {
            return;
        }

        if (!_assetStore.TryGetEncodedBytes(prepared.Prepared.OriginalAssetId, out byte[]? originalBytes) || originalBytes is null)
        {
            StatusMessage = "تعذر تحميل الصورة الأصلية لفتح محرر القناع";
            return;
        }

        SKBitmap originalBmp = AlphaMaskCompositor.DecodeNormalizedBytesToBitmap(originalBytes);
        var editable = new EditableAlphaMask(
            prepared.Prepared.WidthPx,
            prepared.Prepared.HeightPx,
            prepared.Prepared.CurrentAlphaMask,
            prepared.Prepared.AutoAiAlphaMask);
        SKBitmap cutoutBmp = AlphaMaskCompositor.BuildCutoutPreviewBitmap(originalBmp, editable.AlphaSpan);

        CloseMaskEditor();
        _preparedMaskContext = prepared;
        _maskEditorOriginalBitmap = originalBmp;
        _maskEditorCutoutBitmap = cutoutBmp;
        _activeMaskSession = editable;
        MaskEditorTargetTitle = prepared.TargetDisplayName;
        SelectedMaskBrushMode = MaskBrushMode.Erase;
        SelectedMaskPreviewMode = MaskPreviewMode.AfterCheckerboard;
        MaskSplitRatio = 0.50;
        RefreshMaskEditorBindings();
        IsMaskEditorOpen = true;
        StatusMessage = "محرر القناع جاهز — استخدم فرشاة المسح والاستعادة ثم اعتمد التعديل";
    }

    private async Task AutoFrameFaceCoreAsync(CancellationToken ct)
    {
        if (_aiCoordinator is null)
        {
            return;
        }

        IdPhotoFramingPreset preset = SelectedFramingPresetOption?.Preset ?? IdPhotoFramingPreset.AutoByAspect;
        AiOperationOutcome outcome = await _aiCoordinator.AutoFrameFaceForSelectionAsync(preset, ct);
        StatusMessage = outcome.MessageAr;
    }

    private void OnAiCoordinatorStateChanged()
    {
        void Raise()
        {
            OnPropertyChanged(nameof(IsAiBusy));
            OnPropertyChanged(nameof(IsAiIdle));
            OnPropertyChanged(nameof(AiBusyStatusText));
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

    private void RefreshMaskEditorBindings()
    {
        OnPropertyChanged(nameof(ActiveMaskSession));
        OnPropertyChanged(nameof(MaskEditorOriginalBitmap));
        OnPropertyChanged(nameof(MaskEditorCutoutBitmap));
        OnPropertyChanged(nameof(CanUndoMaskStroke));
        OnPropertyChanged(nameof(CanRedoMaskStroke));
    }

    private void DisposeAiResources()
    {
        if (_aiCoordinator is not null)
        {
            _aiCoordinator.StateChanged -= OnAiCoordinatorStateChanged;
        }

        IsMaskEditorOpen = false;
        _preparedMaskContext = null;
        _activeMaskSession = null;
        SKBitmap? oldOriginal = _maskEditorOriginalBitmap;
        SKBitmap? oldCutout = _maskEditorCutoutBitmap;
        _maskEditorOriginalBitmap = null;
        _maskEditorCutoutBitmap = null;
        oldOriginal?.Dispose();
        oldCutout?.Dispose();
    }
}
