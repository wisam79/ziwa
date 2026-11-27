using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ziwa.Application.Commands;
using Ziwa.Application.Interaction;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;

namespace Ziwa.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private NodeId? _lastInlineTextEditId;

    public event Action? ViewportFitRequested;
    public event Action<TextNode>? InlineTextEditOpened;

    [ObservableProperty]
    private double? _canvasPointerXMm;

    [ObservableProperty]
    private double? _canvasPointerYMm;

    [ObservableProperty]
    private double? _selectionStartXMm;

    [ObservableProperty]
    private double? _selectionEndXMm;

    [ObservableProperty]
    private double? _selectionStartYMm;

    [ObservableProperty]
    private double? _selectionEndYMm;

    [ObservableProperty]
    private bool _isInlineTextEditOpen;

    [ObservableProperty]
    private string _inlineTextEditDraft = string.Empty;

    public bool IsCropModeActive => _session.ActiveCropNodeId is not null;

    public bool IsNotCropModeActive => !IsCropModeActive;

    public bool IsSelectToolActive => !IsCropModeActive && !IsInlineTextEditOpen;

    public string UndoToolTipText => _session.NextUndoDescription is { } desc
        ? $"تراجع: {desc} (Ctrl+Z)"
        : "تراجع (Ctrl+Z)";

    public string RedoToolTipText => _session.NextRedoDescription is { } desc
        ? $"إعادة: {desc} (Ctrl+Y)"
        : "إعادة (Ctrl+Y)";

    public string CanvasPointerCoordsText => CanvasPointerXMm is { } x && CanvasPointerYMm is { } y
        ? $"X: {x:0.#}  Y: {y:0.#} مم"
        : "X: --  Y: -- مم";

    partial void OnCanvasPointerXMmChanged(double? value) => OnPropertyChanged(nameof(CanvasPointerCoordsText));

    partial void OnCanvasPointerYMmChanged(double? value) => OnPropertyChanged(nameof(CanvasPointerCoordsText));

    public string CropZoomPercentText => ActiveCropImageNode is { } img
        ? $"{img.Crop.Zoom * 100.0:0}%"
        : "100%";

    private ImageNode? ActiveCropImageNode => _session.ActiveCropNodeId is { } id
        ? ActivePage.Nodes.FirstOrDefault(n => n.Id == id) as ImageNode
        : null;

    private void SyncCanvasInteractionFields()
    {
        if (HasSelection)
        {
            SelectionBoundsInfo selInfo = HitTestEngine.ComputeSelectionBounds(ActivePage, _session.SelectedNodeIds);
            if (selInfo.HasSelection)
            {
                RectMm aabb = HitTestEngine.ComputeRotatedAabb(selInfo.BoundsMm, selInfo.RotationDeg);
                SelectionStartXMm = aabb.X;
                SelectionEndXMm = aabb.Right;
                SelectionStartYMm = aabb.Y;
                SelectionEndYMm = aabb.Bottom;
            }
            else
            {
                ClearSelectionRulerSpan();
            }
        }
        else if (IsCollageSlotSelected && SelectedCollageSlot is { } slot)
        {
            SelectionStartXMm = slot.BoundsMm.X;
            SelectionEndXMm = slot.BoundsMm.Right;
            SelectionStartYMm = slot.BoundsMm.Y;
            SelectionEndYMm = slot.BoundsMm.Bottom;
        }
        else
        {
            ClearSelectionRulerSpan();
        }

        if (_session.ActiveTextEditNodeId is { } textId &&
            ActivePage.Nodes.FirstOrDefault(n => n.Id == textId) is TextNode txt)
        {
            if (_lastInlineTextEditId != textId)
            {
                _lastInlineTextEditId = textId;
                InlineTextEditDraft = txt.Text;
                IsInlineTextEditOpen = true;
                InlineTextEditOpened?.Invoke(txt);
            }
            else
            {
                IsInlineTextEditOpen = true;
            }
        }
        else
        {
            _lastInlineTextEditId = null;
            IsInlineTextEditOpen = false;
        }

        OnPropertyChanged(nameof(IsCropModeActive));
        OnPropertyChanged(nameof(IsNotCropModeActive));
        OnPropertyChanged(nameof(IsSelectToolActive));
        OnPropertyChanged(nameof(CropZoomPercentText));
        OnPropertyChanged(nameof(UndoToolTipText));
        OnPropertyChanged(nameof(RedoToolTipText));
    }

    private void ClearSelectionRulerSpan()
    {
        SelectionStartXMm = null;
        SelectionEndXMm = null;
        SelectionStartYMm = null;
        SelectionEndYMm = null;
    }

    [RelayCommand]
    private Task FitPageAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("view.fitPage"), ct);

    [RelayCommand]
    private Task DuplicateSelectionAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("edit.duplicate"), ct);

    [RelayCommand]
    private Task SelectAllNodesAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("edit.selectAll"), ct);

    [RelayCommand]
    private Task BringForwardSelectionAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("arrange.bringForward"), ct);

    [RelayCommand]
    private Task SendBackwardSelectionAsync(CancellationToken ct) => _commandRegistry.TryExecuteAsync(new CommandId("arrange.sendBackward"), ct);

    [RelayCommand]
    private void EnterSelectedCropMode()
    {
        if (SelectedNodes.Count == 1 && SelectedNodes[0] is ImageNode img && !img.IsLocked)
        {
            _session.EnterCropMode(img.Id);
            StatusMessage = "وضع القص الموضعي: اسحب لتحريك الصورة ومرر العجلة للتكبير";
        }
    }

    [RelayCommand]
    private void ZoomInCrop()
    {
        if (ActiveCropImageNode is not { } img)
        {
            return;
        }

        CropSpec next = img.Crop with { Zoom = Math.Clamp(img.Crop.Zoom * 1.15, 1.0, 20.0) };
        _session.UpdateTransientPreview(doc => FreeformSceneOperations.UpdateImageCrop(doc, img.Id, next));
    }

    [RelayCommand]
    private void ZoomOutCrop()
    {
        if (ActiveCropImageNode is not { } img)
        {
            return;
        }

        CropSpec next = img.Crop with { Zoom = Math.Clamp(img.Crop.Zoom / 1.15, 1.0, 20.0) };
        _session.UpdateTransientPreview(doc => FreeformSceneOperations.UpdateImageCrop(doc, img.Id, next));
    }

    [RelayCommand]
    private void ResetCropSpec()
    {
        if (ActiveCropImageNode is not { } img)
        {
            return;
        }

        _session.UpdateTransientPreview(doc => FreeformSceneOperations.UpdateImageCrop(doc, img.Id, CropSpec.Identity));
    }

    [RelayCommand]
    private void ApplyCropMode()
    {
        if (_session.CommitCropMode())
        {
            StatusMessage = "تم تطبيق القص الموضعي";
        }
        else if (_session.ActiveCropNodeId is not null)
        {
            _session.ExitCropMode(cancel: false);
        }
    }

    [RelayCommand]
    private void CancelCropMode()
    {
        if (_session.ActiveCropNodeId is not null)
        {
            _session.ExitCropMode(cancel: true);
            StatusMessage = "تم إلغاء تعديل القص";
        }
    }

    [RelayCommand]
    private void EnterSelectedTextEdit()
    {
        if (SelectedNodes.Count == 1 && SelectedNodes[0] is TextNode txt && !txt.IsLocked)
        {
            _session.EnterTextEditMode(txt.Id);
        }
    }

    [RelayCommand]
    private void CommitInlineTextEdit()
    {
        if (_session.ActiveTextEditNodeId is not { } textId)
        {
            IsInlineTextEditOpen = false;
            return;
        }

        string nextText = InlineTextEditDraft;
        _session.ExitTextEditMode();
        if (ActivePage.Nodes.FirstOrDefault(n => n.Id == textId) is TextNode txt &&
            !string.Equals(txt.Text, nextText, StringComparison.Ordinal))
        {
            _session.ApplyCommand("تعديل النص", doc => FreeformSceneOperations.UpdateTextNodeContent(doc, textId, nextText));
            StatusMessage = "تم تحديث النص";
        }
    }

    [RelayCommand]
    private void CancelInlineTextEdit()
    {
        if (_session.ActiveTextEditNodeId is not null)
        {
            _session.ExitTextEditMode();
        }
        else
        {
            IsInlineTextEditOpen = false;
        }
    }

    [RelayCommand]
    private void ActivateSelectTool()
    {
        if (_session.ActiveCropNodeId is not null)
        {
            _session.CommitCropMode();
        }

        if (_session.ActiveTextEditNodeId is not null)
        {
            CommitInlineTextEdit();
        }

        StatusMessage = "أداة التحديد والتحريك نشطة";
    }

    [RelayCommand]
    private Task AddRectangleShapeAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("insert.rectangleShape"), ct);

    [RelayCommand]
    private Task AddEllipseShapeAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("insert.ellipseShape"), ct);

    [RelayCommand]
    private Task AddLineShapeAsync(CancellationToken ct) =>
        _commandRegistry.TryExecuteAsync(new CommandId("insert.lineShape"), ct);

    private Task InsertDefaultShape(
        ShapeKind kind,
        string nameAr,
        RectMm boundsMm,
        ShapeFill fill,
        StrokeSpec? stroke,
        int polygonSides = 5,
        double innerRatio = 0.5)
    {
        var shape = new ShapeNode(
            Id: NodeId.New(),
            Name: nameAr,
            BoundsMm: boundsMm,
            Kind: kind,
            Fill: fill,
            Stroke: stroke,
            PolygonSides: polygonSides,
            InnerRadiusRatio: innerRatio);
        _session.ApplyCommand($"إدراج {nameAr}", doc => FreeformSceneOperations.AddNode(doc, shape));
        _session.SetSelection([shape.Id]);
        return Task.CompletedTask;
    }
}
