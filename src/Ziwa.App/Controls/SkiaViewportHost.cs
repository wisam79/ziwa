using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Ziwa.App.Services;
using Ziwa.Application.Collage;
using Ziwa.Application.Commands;
using Ziwa.Application.Interaction;
using Ziwa.Application.Session;
using Ziwa.Domain.Collage;
using Ziwa.Domain.Models;
using Ziwa.Domain.Primitives;
using Ziwa.Rendering;
using Ziwa.Rendering.Viewport;

namespace Ziwa.App.Controls;

public sealed class SkiaViewportHost : Control
{
    public static readonly StyledProperty<DocumentSession?> SessionProperty =
        AvaloniaProperty.Register<SkiaViewportHost, DocumentSession?>(nameof(Session));

    public static readonly StyledProperty<CommandRegistry?> CommandRegistryProperty =
        AvaloniaProperty.Register<SkiaViewportHost, CommandRegistry?>(nameof(CommandRegistry));

    public static readonly StyledProperty<PageModel?> PageProperty =
        AvaloniaProperty.Register<SkiaViewportHost, PageModel?>(nameof(Page));

    public static readonly StyledProperty<SceneRenderer?> SceneRendererProperty =
        AvaloniaProperty.Register<SkiaViewportHost, SceneRenderer?>(nameof(SceneRenderer));

    public static readonly StyledProperty<ViewportTransform> ViewportProperty =
        AvaloniaProperty.Register<SkiaViewportHost, ViewportTransform>(
            nameof(Viewport),
            defaultValue: new ViewportTransform(1.0, 32.0, 32.0, 1.0),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> PasteboardBrushProperty =
        AvaloniaProperty.Register<SkiaViewportHost, IBrush?>(nameof(PasteboardBrush));

    public static readonly StyledProperty<IBrush?> SelectionAccentBrushProperty =
        AvaloniaProperty.Register<SkiaViewportHost, IBrush?>(nameof(SelectionAccentBrush));

    public static readonly StyledProperty<double?> PointerPageXMmProperty =
        AvaloniaProperty.Register<SkiaViewportHost, double?>(
            nameof(PointerPageXMm),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double?> PointerPageYMmProperty =
        AvaloniaProperty.Register<SkiaViewportHost, double?>(
            nameof(PointerPageYMm),
            defaultBindingMode: BindingMode.TwoWay);

    private const double HandleHitRadiusDip = 7.5;
    private const double RotateHandleOffsetDip = 22.0;

    private bool _hasInitialFit;
    private long _lastFittedGeneration = -1;
    private DragMode _dragMode;
    private Point _lastPointerDip;
    private (double XMm, double YMm) _dragStartMm;
    private (double XMm, double YMm) _marqueeCurrentMm;
    private TransformHandleKind _activeHandle;
    private TransformHandleKind _hoveredHandle;
    private NodeId? _hoveredNodeId;
    private bool _isSpacePressed;
    private bool _isPendingRightPan;
    private Point _rightPressOriginDip;
    private RectMm _initialMoveBounds;
    private SlotId _sourceSlotId;
    private SlotId? _hoveredSwapTargetSlotId;
    private ImmutableArray<NodeId> _marqueeBaseSelection = [];
    private ImmutableArray<SnapGuideLine> _activeSnapGuides = [];
    private RectMm? _activeMarqueeMm;

    static SkiaViewportHost()
    {
        AffectsRender<SkiaViewportHost>(
            SessionProperty,
            PageProperty,
            SceneRendererProperty,
            ViewportProperty,
            PasteboardBrushProperty,
            SelectionAccentBrushProperty);
    }

    public SkiaViewportHost()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public DocumentSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public CommandRegistry? CommandRegistry
    {
        get => GetValue(CommandRegistryProperty);
        set => SetValue(CommandRegistryProperty, value);
    }

    public PageModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public SceneRenderer? SceneRenderer
    {
        get => GetValue(SceneRendererProperty);
        set => SetValue(SceneRendererProperty, value);
    }

    public ViewportTransform Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    public IBrush? PasteboardBrush
    {
        get => GetValue(PasteboardBrushProperty);
        set => SetValue(PasteboardBrushProperty, value);
    }

    public IBrush? SelectionAccentBrush
    {
        get => GetValue(SelectionAccentBrushProperty);
        set => SetValue(SelectionAccentBrushProperty, value);
    }

    public double? PointerPageXMm
    {
        get => GetValue(PointerPageXMmProperty);
        set => SetValue(PointerPageXMmProperty, value);
    }

    public double? PointerPageYMm
    {
        get => GetValue(PointerPageYMmProperty);
        set => SetValue(PointerPageYMmProperty, value);
    }

    public bool FitToPage()
    {
        PageModel? page = Session?.EffectiveDocument.ActivePage ?? Page;
        if (page is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return false;
        }

        double dpiScale = VisualRoot?.RenderScaling ?? 1.0;
        SetCurrentValue(
            ViewportProperty,
            ViewportTransform.FitPage(
                Bounds.Width,
                Bounds.Height,
                page.WidthMm,
                page.HeightMm,
                paddingDip: 28.0,
                dpiScale: dpiScale));
        _hasInitialFit = true;
        if (Session is not null)
        {
            _lastFittedGeneration = Session.Generation;
        }

        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SessionProperty)
        {
            if (change.OldValue is DocumentSession oldSession)
            {
                oldSession.SessionChanged -= OnSessionChanged;
            }

            if (change.NewValue is DocumentSession newSession)
            {
                newSession.SessionChanged += OnSessionChanged;
                _lastFittedGeneration = -1;
            }
        }
    }

    private void OnSessionChanged()
    {
        if (Session is not null &&
            Session.Generation != _lastFittedGeneration &&
            Bounds.Width > 0 &&
            Bounds.Height > 0)
        {
            FitToPage();
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double dpiScale = VisualRoot?.RenderScaling ?? 1.0;
        ViewportTransform effectiveViewport = Viewport with { DpiScale = dpiScale };
        SKColor pasteboardColor = ResolveColor(PasteboardBrush, new SKColor(18, 20, 24, 255));
        SKColor accentColor = ResolveColor(SelectionAccentBrush, new SKColor(88, 166, 255, 255));

        PageModel? effectivePage = Session?.EffectiveDocument.ActivePage ?? Page;
        ImmutableArray<NodeId> selectedIds = Session?.SelectedNodeIds ?? [];
        SlotId? selectedSlotId = Session?.SelectedCollageSlotId;
        NodeId? activeCropId = Session?.ActiveCropNodeId;
        string? dragHudText = BuildDragHudText(effectivePage, selectedIds, activeCropId);
        SlotId? swapSourceSlotId = _dragMode == DragMode.CollageSlotSwap ? _sourceSlotId : null;
        SlotId? swapTargetSlotId = _dragMode == DragMode.CollageSlotSwap ? _hoveredSwapTargetSlotId : null;
        bool isSlotCropActive = _dragMode == DragMode.SlotCropPan;

        context.Custom(new ViewportDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            effectivePage,
            SceneRenderer,
            effectiveViewport,
            pasteboardColor,
            accentColor,
            selectedIds,
            selectedSlotId,
            activeCropId,
            _dragMode == DragMode.None ? _hoveredNodeId : null,
            _hoveredHandle,
            dragHudText,
            _activeSnapGuides,
            _activeMarqueeMm,
            swapSourceSlotId,
            swapTargetSlotId,
            isSlotCropActive));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (!_hasInitialFit && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            FitToPage();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        PageModel? page = Session?.EffectiveDocument.ActivePage ?? Page;
        if (page is null)
        {
            return;
        }

        if (Session is { ActiveCropNodeId: { } cropId } &&
            page.Nodes.FirstOrDefault(n => n.Id == cropId) is ImageNode cropImage)
        {
            double cropFactor = e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12;
            CropSpec nextCrop = cropImage.Crop with
            {
                Zoom = Math.Clamp(cropImage.Crop.Zoom * cropFactor, 1.0, 20.0),
            };
            Session.UpdateTransientPreview(doc => FreeformSceneOperations.UpdateImageCrop(doc, cropId, nextCrop));
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) &&
            Session is { SelectedCollageSlotId: { } slotId } &&
            page.CollageLayout?.Slots.FirstOrDefault(s => s.Id == slotId) is { AssignedAssetId: not null } slot)
        {
            double slotFactor = e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12;
            CropSpec nextSlotCrop = slot.EffectiveCrop with
            {
                Zoom = Math.Clamp(slot.EffectiveCrop.Zoom * slotFactor, 1.0, 20.0),
            };
            Session.ApplyCommand(
                "تكبير قص الخانة",
                doc => CollageOperations.TransformSlotContent(doc, slotId, crop: nextSlotCrop),
                coalesceKey: $"slotcropzoom:{slotId.Value}");
            e.Handled = true;
            return;
        }

        Point cursor = e.GetPosition(this);
        (double cxMm, double cyMm) = Viewport.DipToPageMm(cursor.X, cursor.Y);
        SetCurrentValue(PointerPageXMmProperty, cxMm);
        SetCurrentValue(PointerPageYMmProperty, cyMm);
        double factor = e.Delta.Y > 0 ? 1.12 : 1.0 / 1.12;
        ViewportTransform updated = Viewport.ZoomAroundCursor(cursor.X, cursor.Y, Viewport.Zoom * factor);
        SetCurrentValue(ViewportProperty, updated);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        PointerPoint point = e.GetCurrentPoint(this);
        _lastPointerDip = point.Position;

        if (point.Properties.IsMiddleButtonPressed)
        {
            _dragMode = DragMode.ViewportPan;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            _isPendingRightPan = true;
            _rightPressOriginDip = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || Session is null)
        {
            return;
        }

        if (_isSpacePressed)
        {
            _dragMode = DragMode.ViewportPan;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        PageModel page = Session.EffectiveDocument.ActivePage;
        (double xMm, double yMm) = Viewport.DipToPageMm(point.Position.X, point.Position.Y);
        _dragStartMm = (xMm, yMm);

        if (e.ClickCount >= 2)
        {
            SceneNode? doubleHit = HitTestEngine.HitTestTopmostNode(page, xMm, yMm);
            if (doubleHit is ImageNode img)
            {
                Session.EnterCropMode(img.Id);
                e.Handled = true;
                return;
            }

            if (doubleHit is TextNode txt)
            {
                Session.EnterTextEditMode(txt.Id);
                e.Handled = true;
                return;
            }

            if (doubleHit is StickerNode sticker)
            {
                Session.OpenStickerEditor(sticker.Id);
                e.Handled = true;
                return;
            }

            if (doubleHit is null &&
                HitTestCollageSlot(page, xMm, yMm) is { } doubleSlot &&
                doubleSlot.AssignedAssetId is null &&
                CommandRegistry is not null)
            {
                Session.SelectCollageSlot(doubleSlot.Id);
                _ = CommandRegistry.TryExecuteAsync(new CommandId("collage.insertIntoSelectedSlot"));
                e.Handled = true;
                return;
            }
        }

        if (Session.ActiveCropNodeId is { } cropNodeId)
        {
            SceneNode? cropTarget = HitTestEngine.HitTestTopmostNode(page, xMm, yMm);
            if (cropTarget?.Id == cropNodeId)
            {
                _dragMode = DragMode.CropPan;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            Session.CommitCropMode();
        }

        double scale = Math.Max(0.1, Viewport.DipPerMm);
        double handleToleranceMm = HandleHitRadiusDip / scale;
        double rotateOffsetMm = RotateHandleOffsetDip / scale;

        SelectionBoundsInfo selInfo = HitTestEngine.ComputeSelectionBounds(page, Session.SelectedNodeIds);
        if (selInfo.HasSelection)
        {
            TransformHandleKind handle = HitTestEngine.HitTestHandles(
                selInfo.BoundsMm,
                selInfo.RotationDeg,
                xMm,
                yMm,
                handleToleranceMm,
                rotateOffsetMm);

            if (handle == TransformHandleKind.Rotate)
            {
                _dragMode = DragMode.RotateSelection;
                Session.BeginInteraction("تدوير التحديد");
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (handle != TransformHandleKind.None)
            {
                _dragMode = DragMode.ResizeSelection;
                _activeHandle = handle;
                Session.BeginInteraction("تحجيم التحديد");
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        SceneNode? hitNode = HitTestEngine.HitTestTopmostNode(page, xMm, yMm);
        bool shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (hitNode is not null)
        {
            if (shiftHeld)
            {
                ImmutableArray<NodeId> nextSel = Session.SelectedNodeIds.Contains(hitNode.Id)
                    ? Session.SelectedNodeIds.Remove(hitNode.Id)
                    : Session.SelectedNodeIds.Add(hitNode.Id);
                Session.SetSelection(nextSel);
            }
            else if (!Session.SelectedNodeIds.Contains(hitNode.Id))
            {
                Session.SetSelection([hitNode.Id]);
            }

            SelectionBoundsInfo moveBounds = HitTestEngine.ComputeSelectionBounds(page, Session.SelectedNodeIds);
            if (moveBounds.HasSelection)
            {
                _initialMoveBounds = moveBounds.BoundsMm;
                _dragMode = DragMode.MoveSelection;
                Session.BeginInteraction("تحريك التحديد");
                e.Pointer.Capture(this);
            }

            e.Handled = true;
            return;
        }

        CollageSlot? hitSlot = HitTestCollageSlot(page, xMm, yMm);
        if (hitSlot is not null)
        {
            Session.SelectCollageSlot(hitSlot.Id);
            _sourceSlotId = hitSlot.Id;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                Session.BeginInteraction("تحريك قص الخانة");
                _dragMode = DragMode.SlotCropPan;
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }
            else
            {
                _dragMode = DragMode.CollageSlotSwap;
                Cursor = new Cursor(StandardCursorType.Hand);
            }

            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        _marqueeBaseSelection = shiftHeld ? Session.SelectedNodeIds : [];
        if (!shiftHeld)
        {
            Session.ClearSelection();
        }

        _dragMode = DragMode.Marquee;
        _marqueeCurrentMm = (xMm, yMm);
        _activeMarqueeMm = new RectMm(xMm, yMm, 0.0, 0.0);
        Cursor = new Cursor(StandardCursorType.Cross);
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        SetCurrentValue(PointerPageXMmProperty, null);
        SetCurrentValue(PointerPageYMmProperty, null);

        if (_dragMode == DragMode.None && (_hoveredNodeId is not null || _hoveredHandle != TransformHandleKind.None))
        {
            _hoveredNodeId = null;
            _hoveredHandle = TransformHandleKind.None;
            Cursor = _isSpacePressed ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        Point currentDip = e.GetPosition(this);
        (double curXMm, double curYMm) = Viewport.DipToPageMm(currentDip.X, currentDip.Y);
        SetCurrentValue(PointerPageXMmProperty, curXMm);
        SetCurrentValue(PointerPageYMmProperty, curYMm);

        if (_isPendingRightPan)
        {
            double distSq = Math.Pow(currentDip.X - _rightPressOriginDip.X, 2) + Math.Pow(currentDip.Y - _rightPressOriginDip.Y, 2);
            if (distSq > 16.0)
            {
                _isPendingRightPan = false;
                _dragMode = DragMode.ViewportPan;
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }
            else
            {
                return;
            }
        }

        if (_dragMode == DragMode.None)
        {
            UpdateHoverAndCursor(currentDip, curXMm, curYMm, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            return;
        }

        if (_dragMode == DragMode.ViewportPan)
        {
            double dx = currentDip.X - _lastPointerDip.X;
            double dy = currentDip.Y - _lastPointerDip.Y;
            _lastPointerDip = currentDip;
            SetCurrentValue(ViewportProperty, Viewport.PanBy(dx, dy));
            e.Handled = true;
            return;
        }

        if (Session is null)
        {
            return;
        }

        bool shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool altHeld = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        switch (_dragMode)
        {
            case DragMode.MoveSelection:
                {
                    double rawDx = curXMm - _dragStartMm.XMm;
                    double rawDy = curYMm - _dragStartMm.YMm;
                    RectMm candidate = _initialMoveBounds with
                    {
                        X = _initialMoveBounds.X + rawDx,
                        Y = _initialMoveBounds.Y + rawDy,
                    };

                    PageModel committedPage = Session.CommittedDocument.ActivePage;
                    HashSet<NodeId> selectedSet = Session.SelectedNodeIds.ToHashSet();
                    double snapTolMm = 6.0 / Math.Max(0.1, Viewport.DipPerMm);
                    SnapResult snap = SnapEngine.ComputeMoveSnap(candidate, committedPage, selectedSet, snapTolMm, bypassSnap: altHeld);

                    double finalDx = snap.SnappedBoundsMm.X - _initialMoveBounds.X;
                    double finalDy = snap.SnappedBoundsMm.Y - _initialMoveBounds.Y;
                    _activeSnapGuides = snap.Guides;

                    Session.UpdateTransientPreview(_ =>
                        FreeformSceneOperations.TranslateNodes(Session.CommittedDocument, selectedSet, finalDx, finalDy));
                    break;
                }

            case DragMode.ResizeSelection:
                {
                    ImmutableArray<NodeId> selected = Session.SelectedNodeIds;
                    Session.UpdateTransientPreview(_ => FreeformSceneOperations.ResizeSelection(
                        Session.CommittedDocument,
                        selected,
                        _activeHandle,
                        curXMm,
                        curYMm,
                        lockAspectRatio: shiftHeld));
                    break;
                }

            case DragMode.RotateSelection:
                {
                    ImmutableArray<NodeId> selected = Session.SelectedNodeIds;
                    Session.UpdateTransientPreview(_ => FreeformSceneOperations.RotateSelection(
                        Session.CommittedDocument,
                        selected,
                        curXMm,
                        curYMm,
                        snap15Degrees: shiftHeld));
                    break;
                }

            case DragMode.CropPan:
                {
                    if (Session.ActiveCropNodeId is { } cropId &&
                        Session.EffectiveDocument.ActivePage.Nodes.FirstOrDefault(n => n.Id == cropId) is ImageNode img)
                    {
                        double dx = curXMm - _dragStartMm.XMm;
                        double dy = curYMm - _dragStartMm.YMm;
                        _dragStartMm = (curXMm, curYMm);
                        CropSpec next = img.Crop with
                        {
                            PanX = Math.Clamp(img.Crop.PanX - (dx / Math.Max(1.0, img.BoundsMm.Width)), -1.0, 1.0),
                            PanY = Math.Clamp(img.Crop.PanY - (dy / Math.Max(1.0, img.BoundsMm.Height)), -1.0, 1.0),
                        };
                        Session.UpdateTransientPreview(doc => FreeformSceneOperations.UpdateImageCrop(doc, cropId, next));
                    }

                    break;
                }

            case DragMode.SlotCropPan:
                {
                    CollageSlot? slot = Session.EffectiveDocument.ActivePage.CollageLayout?.Slots
                        .FirstOrDefault(s => s.Id == _sourceSlotId);
                    if (slot is not null)
                    {
                        double dx = curXMm - _dragStartMm.XMm;
                        double dy = curYMm - _dragStartMm.YMm;
                        _dragStartMm = (curXMm, curYMm);
                        CropSpec next = slot.EffectiveCrop with
                        {
                            PanX = Math.Clamp(slot.EffectiveCrop.PanX - (dx / Math.Max(1.0, slot.BoundsMm.Width)), -1.0, 1.0),
                            PanY = Math.Clamp(slot.EffectiveCrop.PanY - (dy / Math.Max(1.0, slot.BoundsMm.Height)), -1.0, 1.0),
                        };
                        Session.UpdateTransientPreview(doc => CollageOperations.TransformSlotContent(doc, _sourceSlotId, crop: next));
                    }

                    break;
                }

            case DragMode.CollageSlotSwap:
                {
                    CollageSlot? hoverSlot = HitTestCollageSlot(Session.EffectiveDocument.ActivePage, curXMm, curYMm);
                    SlotId? nextTarget = hoverSlot is not null && hoverSlot.Id != _sourceSlotId ? hoverSlot.Id : null;
                    if (nextTarget != _hoveredSwapTargetSlotId)
                    {
                        _hoveredSwapTargetSlotId = nextTarget;
                        InvalidateVisual();
                    }

                    break;
                }

            case DragMode.Marquee:
                {
                    _marqueeCurrentMm = (curXMm, curYMm);
                    _activeMarqueeMm = new RectMm(
                        _dragStartMm.XMm,
                        _dragStartMm.YMm,
                        _marqueeCurrentMm.XMm - _dragStartMm.XMm,
                        _marqueeCurrentMm.YMm - _dragStartMm.YMm);
                    InvalidateVisual();
                    break;
                }
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPendingRightPan)
        {
            _isPendingRightPan = false;
            e.Pointer.Capture(null);
            ContextMenu?.Open(this);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.None)
        {
            return;
        }

        DragMode finishedMode = _dragMode;
        _dragMode = DragMode.None;
        _activeHandle = TransformHandleKind.None;
        _activeSnapGuides = [];
        _hoveredSwapTargetSlotId = null;

        if (finishedMode == DragMode.Marquee && _activeMarqueeMm is { } marquee && Session is not null)
        {
            ImmutableArray<NodeId> hits = HitTestEngine.HitTestMarquee(Session.EffectiveDocument.ActivePage, marquee);
            ImmutableArray<NodeId> nextSelection = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !_marqueeBaseSelection.IsDefaultOrEmpty
                ? _marqueeBaseSelection.Concat(hits).Distinct().ToImmutableArray()
                : hits;
            Session.SetSelection(nextSelection);
            _activeMarqueeMm = null;
            _marqueeBaseSelection = [];
            InvalidateVisual();
        }
        else if (finishedMode == DragMode.CollageSlotSwap && Session is not null)
        {
            Point releaseDip = e.GetPosition(this);
            (double rxMm, double ryMm) = Viewport.DipToPageMm(releaseDip.X, releaseDip.Y);
            CollageSlot? targetSlot = HitTestCollageSlot(Session.EffectiveDocument.ActivePage, rxMm, ryMm);
            if (targetSlot is not null && targetSlot.Id != _sourceSlotId)
            {
                Session.ApplyCommand(
                    "تبديل خانتي الكولاج",
                    doc => CollageOperations.SwapSlots(doc, _sourceSlotId, targetSlot.Id));
                Session.SelectCollageSlot(targetSlot.Id);
            }
        }
        else if (finishedMode is DragMode.MoveSelection or DragMode.ResizeSelection or DragMode.RotateSelection or DragMode.SlotCropPan)
        {
            Session?.CommitInteraction();
        }

        _sourceSlotId = SlotId.Empty;
        _marqueeBaseSelection = [];
        e.Pointer.Capture(null);
        Point endDip = e.GetPosition(this);
        (double endXMm, double endYMm) = Viewport.DipToPageMm(endDip.X, endDip.Y);
        UpdateHoverAndCursor(endDip, endXMm, endYMm, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Key == Key.Space && _isSpacePressed)
        {
            _isSpacePressed = false;
            if (_dragMode == DragMode.None)
            {
                Cursor = Cursor.Default;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Session is null)
        {
            return;
        }

        if (e.Key == Key.Space && Session.ActiveTextEditNodeId is null)
        {
            _isSpacePressed = true;
            if (_dragMode == DragMode.None)
            {
                Cursor = new Cursor(StandardCursorType.Hand);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _dragMode = DragMode.None;
            _activeSnapGuides = [];
            _activeMarqueeMm = null;
            _hoveredSwapTargetSlotId = null;
            if (Session.ActiveCropNodeId is not null)
            {
                Session.ExitCropMode(cancel: true);
            }
            else if (Session.IsInteracting)
            {
                Session.CancelInteraction();
            }
            else if (Session.ActiveTextEditNodeId is not null)
            {
                Session.ExitTextEditMode();
            }
            else
            {
                Session.ClearSelection();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (Session.ActiveCropNodeId is not null)
            {
                Session.CommitCropMode();
                e.Handled = true;
                return;
            }

            if (Session.ActiveTextEditNodeId is not null)
            {
                Session.ExitTextEditMode();
                e.Handled = true;
                return;
            }
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && !Session.SelectedNodeIds.IsDefaultOrEmpty)
        {
            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 1.0;
            (double dx, double dy) = e.Key switch
            {
                Key.Left => (-step, 0.0),
                Key.Right => (step, 0.0),
                Key.Up => (0.0, -step),
                Key.Down => (0.0, step),
                _ => (0.0, 0.0),
            };

            HashSet<NodeId> selectedSet = Session.SelectedNodeIds.ToHashSet();
            string coalesceKey = $"nudge:{string.Join(',', selectedSet.OrderBy(id => id.Value))}";
            Session.ApplyCommand(
                "تحريك بالأسهم",
                doc => FreeformSceneOperations.TranslateNodes(doc, selectedSet, dx, dy),
                coalesceKey);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && Session.ActiveTextEditNodeId is null && CommandRegistry is not null)
        {
            _ = CommandRegistry.TryExecuteShortcutAsync("Delete");
            e.Handled = true;
            return;
        }

        if (CommandRegistry is not null)
        {
            string? gesture = KeyboardCommandRouter.GetGesture(e.Key, e.KeyModifiers);
            if (gesture is not null && CommandRegistry.TryGetByShortcut(gesture, out _))
            {
                _ = CommandRegistry.TryExecuteShortcutAsync(gesture);
                e.Handled = true;
            }
        }
    }

    private void UpdateHoverAndCursor(Point currentDip, double curXMm, double curYMm, bool altHeld)
    {
        if (_isSpacePressed)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        PageModel? page = Session?.EffectiveDocument.ActivePage ?? Page;
        if (page is null || Session is null)
        {
            Cursor = Cursor.Default;
            return;
        }

        if (Session.ActiveCropNodeId is { } cropId)
        {
            SceneNode? overCrop = HitTestEngine.HitTestTopmostNode(page, curXMm, curYMm);
            Cursor = overCrop?.Id == cropId ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
            return;
        }

        double scale = Math.Max(0.1, Viewport.DipPerMm);
        double handleToleranceMm = HandleHitRadiusDip / scale;
        double rotateOffsetMm = RotateHandleOffsetDip / scale;

        TransformHandleKind nextHoveredHandle = TransformHandleKind.None;
        NodeId? nextHoveredNode = null;

        SelectionBoundsInfo selInfo = HitTestEngine.ComputeSelectionBounds(page, Session.SelectedNodeIds);
        if (selInfo.HasSelection)
        {
            nextHoveredHandle = HitTestEngine.HitTestHandles(
                selInfo.BoundsMm,
                selInfo.RotationDeg,
                curXMm,
                curYMm,
                handleToleranceMm,
                rotateOffsetMm);
        }

        if (nextHoveredHandle != TransformHandleKind.None)
        {
            Cursor = ResolveHandleCursor(nextHoveredHandle, selInfo.RotationDeg);
        }
        else
        {
            SceneNode? hit = HitTestEngine.HitTestTopmostNode(page, curXMm, curYMm);
            if (hit is not null)
            {
                nextHoveredNode = hit.Id;
                Cursor = Session.SelectedNodeIds.Contains(hit.Id)
                    ? new Cursor(StandardCursorType.SizeAll)
                    : new Cursor(StandardCursorType.Hand);
            }
            else if (HitTestCollageSlot(page, curXMm, curYMm) is { } slot)
            {
                Cursor = altHeld && slot.AssignedAssetId is not null
                    ? new Cursor(StandardCursorType.SizeAll)
                    : new Cursor(StandardCursorType.Hand);
            }
            else
            {
                Cursor = Cursor.Default;
            }
        }

        if (nextHoveredHandle != _hoveredHandle || nextHoveredNode != _hoveredNodeId)
        {
            _hoveredHandle = nextHoveredHandle;
            _hoveredNodeId = nextHoveredNode;
            InvalidateVisual();
        }
    }

    private static Cursor ResolveHandleCursor(TransformHandleKind handle, double rotationDeg)
    {
        if (handle == TransformHandleKind.Rotate)
        {
            return new Cursor(StandardCursorType.Hand);
        }

        double baseAngle = handle switch
        {
            TransformHandleKind.Top or TransformHandleKind.Bottom => 90.0,
            TransformHandleKind.Left or TransformHandleKind.Right => 0.0,
            TransformHandleKind.TopLeft or TransformHandleKind.BottomRight => 45.0,
            TransformHandleKind.TopRight or TransformHandleKind.BottomLeft => 135.0,
            _ => 0.0,
        };

        double effective = ((baseAngle + rotationDeg) % 180.0 + 180.0) % 180.0;
        StandardCursorType type = effective switch
        {
            < 22.5 or >= 157.5 => StandardCursorType.SizeWestEast,
            < 67.5 => StandardCursorType.TopLeftCorner,
            < 112.5 => StandardCursorType.SizeNorthSouth,
            _ => StandardCursorType.TopRightCorner,
        };
        return new Cursor(type);
    }

    private string? BuildDragHudText(
        PageModel? page,
        ImmutableArray<NodeId> selectedIds,
        NodeId? activeCropId)
    {
        if (page is null)
        {
            return null;
        }

        if (activeCropId is { } cId && page.Nodes.FirstOrDefault(n => n.Id == cId) is ImageNode cropImg)
        {
            return $"Crop {cropImg.Crop.Zoom:0.00}×";
        }

        if (_dragMode == DragMode.SlotCropPan &&
            page.CollageLayout?.Slots.FirstOrDefault(s => s.Id == _sourceSlotId) is { } cropSlot)
        {
            return $"Slot #{cropSlot.Index + 1}  {cropSlot.EffectiveCrop.Zoom:0.00}×";
        }

        if (_dragMode == DragMode.CollageSlotSwap &&
            page.CollageLayout?.Slots.FirstOrDefault(s => s.Id == _sourceSlotId) is { } srcSlot &&
            _hoveredSwapTargetSlotId is { } targetId &&
            page.CollageLayout.Slots.FirstOrDefault(s => s.Id == targetId) is { } dstSlot)
        {
            return $"#{srcSlot.Index + 1} <-> #{dstSlot.Index + 1}";
        }

        if (_dragMode is not (DragMode.MoveSelection or DragMode.ResizeSelection or DragMode.RotateSelection))
        {
            return null;
        }

        SelectionBoundsInfo selInfo = HitTestEngine.ComputeSelectionBounds(page, selectedIds);
        if (!selInfo.HasSelection)
        {
            return null;
        }

        return _dragMode switch
        {
            DragMode.MoveSelection => $"X {selInfo.BoundsMm.X:0.#}  Y {selInfo.BoundsMm.Y:0.#} mm",
            DragMode.ResizeSelection => $"{selInfo.BoundsMm.Width:0.#} × {selInfo.BoundsMm.Height:0.#} mm",
            DragMode.RotateSelection => $"{((selInfo.RotationDeg % 360.0) + 360.0) % 360.0:0.#}°",
            _ => null,
        };
    }

    private static CollageSlot? HitTestCollageSlot(PageModel page, double xMm, double yMm)
    {
        if (page.CollageLayout is not { Slots.IsDefaultOrEmpty: false } layout)
        {
            return null;
        }

        foreach (CollageSlot slot in layout.Slots)
        {
            RectMm b = slot.BoundsMm;
            if (xMm >= b.X && xMm <= b.Right && yMm >= b.Y && yMm <= b.Bottom)
            {
                return slot;
            }
        }

        return null;
    }

    private static SKColor ResolveColor(IBrush? brush, SKColor fallback)
    {
        if (brush is ISolidColorBrush solid)
        {
            Color c = solid.Color;
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return fallback;
    }

    private enum DragMode
    {
        None,
        ViewportPan,
        MoveSelection,
        ResizeSelection,
        RotateSelection,
        CropPan,
        SlotCropPan,
        CollageSlotSwap,
        Marquee,
    }

    private sealed class ViewportDrawOperation : ICustomDrawOperation
    {
        private readonly PageModel? _page;
        private readonly SceneRenderer? _renderer;
        private readonly ViewportTransform _viewport;
        private readonly SKColor _pasteboardColor;
        private readonly SKColor _accentColor;
        private readonly ImmutableArray<NodeId> _selectedIds;
        private readonly SlotId? _selectedSlotId;
        private readonly NodeId? _activeCropId;
        private readonly NodeId? _hoveredNodeId;
        private readonly TransformHandleKind _hoveredHandle;
        private readonly string? _dragHudText;
        private readonly ImmutableArray<SnapGuideLine> _snapGuides;
        private readonly RectMm? _marqueeMm;
        private readonly SlotId? _swapSourceSlotId;
        private readonly SlotId? _swapTargetSlotId;
        private readonly bool _isSlotCropActive;

        public ViewportDrawOperation(
            Rect bounds,
            PageModel? page,
            SceneRenderer? renderer,
            ViewportTransform viewport,
            SKColor pasteboardColor,
            SKColor accentColor,
            ImmutableArray<NodeId> selectedIds,
            SlotId? selectedSlotId,
            NodeId? activeCropId,
            NodeId? hoveredNodeId,
            TransformHandleKind hoveredHandle,
            string? dragHudText,
            ImmutableArray<SnapGuideLine> snapGuides,
            RectMm? marqueeMm,
            SlotId? swapSourceSlotId,
            SlotId? swapTargetSlotId,
            bool isSlotCropActive)
        {
            Bounds = bounds;
            _page = page;
            _renderer = renderer;
            _viewport = viewport;
            _pasteboardColor = pasteboardColor;
            _accentColor = accentColor;
            _selectedIds = selectedIds;
            _selectedSlotId = selectedSlotId;
            _activeCropId = activeCropId;
            _hoveredNodeId = hoveredNodeId;
            _hoveredHandle = hoveredHandle;
            _dragHudText = dragHudText;
            _snapGuides = snapGuides;
            _marqueeMm = marqueeMm;
            _swapSourceSlotId = swapSourceSlotId;
            _swapTargetSlotId = swapTargetSlotId;
            _isSlotCropActive = isSlotCropActive;
        }

        public Rect Bounds { get; }

        public void Dispose()
        {
        }

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            ISkiaSharpApiLeaseFeature? leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null)
            {
                return;
            }

            using ISkiaSharpApiLease lease = leaseFeature.Lease();
            SKCanvas canvas = lease.SkCanvas;

            int rootSave = canvas.Save();
            try
            {
                var viewportRect = SKRect.Create(0f, 0f, (float)Bounds.Width, (float)Bounds.Height);
                canvas.ClipRect(viewportRect);

                using var pasteboardPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = _pasteboardColor,
                    IsAntialias = false,
                };
                canvas.DrawRect(viewportRect, pasteboardPaint);

                if (_page is null || _renderer is null)
                {
                    return;
                }

                float scale = (float)_viewport.DipPerMm;
                float paperW = (float)_page.WidthMm * scale;
                float paperH = (float)_page.HeightMm * scale;
                var paperRectDip = SKRect.Create((float)_viewport.PanXDip, (float)_viewport.PanYDip, paperW, paperH);

                using (SKImageFilter? ambientBlur = SKImageFilter.CreateBlur(10f, 10f))
                using (var ambientPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(0, 0, 0, 44),
                    ImageFilter = ambientBlur,
                    IsAntialias = true,
                })
                {
                    var ambientRect = paperRectDip;
                    ambientRect.Offset(0f, 5f);
                    canvas.DrawRect(ambientRect, ambientPaint);
                }

                using (SKImageFilter? contactBlur = SKImageFilter.CreateBlur(3f, 3f))
                using (var contactPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(0, 0, 0, 56),
                    ImageFilter = contactBlur,
                    IsAntialias = true,
                })
                {
                    var contactRect = paperRectDip;
                    contactRect.Offset(0f, 1.5f);
                    canvas.DrawRect(contactRect, contactPaint);
                }

                int pageSave = canvas.Save();
                try
                {
                    canvas.Translate((float)_viewport.PanXDip, (float)_viewport.PanYDip);
                    canvas.Scale(scale, scale);

                    double effectiveDpi = Math.Max(72.0, 96.0 * _viewport.Zoom * _viewport.DpiScale);
                    _renderer.RenderPage(
                        canvas,
                        _page,
                        new global::Ziwa.Rendering.RenderOptions(
                            Dpi: effectiveDpi,
                            QualityMode: RenderQualityMode.InteractivePreview,
                            IsPreScaledToMillimeters: true,
                            IncludeBleed: false));
                }
                finally
                {
                    canvas.RestoreToCount(pageSave);
                }

                using var framePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(148, 163, 184, 140),
                    StrokeWidth = 1f,
                    IsAntialias = false,
                };
                canvas.DrawRect(paperRectDip, framePaint);

                DrawOverlays(canvas, scale);
            }
            finally
            {
                canvas.RestoreToCount(rootSave);
            }
        }

        private void DrawOverlays(SKCanvas canvas, float scale)
        {
            if (_page is null)
            {
                return;
            }

            if (_page.CollageLayout is { Slots.IsDefaultOrEmpty: false } layout)
            {
                using SKPathEffect slotDash = SKPathEffect.CreateDash([4f, 3f], 0f);
                using var emptySlotPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(148, 163, 184, 145),
                    StrokeWidth = 1f,
                    PathEffect = slotDash,
                };
                using var cutTickPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(100, 116, 139, 130),
                    StrokeWidth = 1f,
                    IsAntialias = false,
                };
                using var selectedSlotPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = _accentColor,
                    StrokeWidth = 2f,
                };
                using var swapFillPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = _accentColor.WithAlpha(48),
                };
                using var swapSourceFillPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = _accentColor.WithAlpha(22),
                };
                using var emptyHintFont = new SKFont(SKTypeface.Default, 11f)
                {
                    Subpixel = true,
                    Edging = SKFontEdging.Antialias,
                };
                using var emptySubFont = new SKFont(SKTypeface.Default, 9.5f)
                {
                    Subpixel = true,
                    Edging = SKFontEdging.Antialias,
                };
                using var emptyHintTextPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(100, 116, 139, 200),
                    IsAntialias = true,
                };

                bool drawCutTicks = layout.Slots.Length > 1;
                SKRect? activeSlotHudRect = null;

                foreach (CollageSlot slot in layout.Slots)
                {
                    (double slx, double sly) = _viewport.PageMmToDip(slot.BoundsMm.X, slot.BoundsMm.Y);
                    var slRect = SKRect.Create(
                        (float)slx,
                        (float)sly,
                        (float)(slot.BoundsMm.Width * scale),
                        (float)(slot.BoundsMm.Height * scale));

                    if (drawCutTicks && slRect.Width >= 16f && slRect.Height >= 16f)
                    {
                        float tick = Math.Min(4.5f, Math.Min(slRect.Width, slRect.Height) * 0.14f);
                        canvas.DrawLine(slRect.Left, slRect.Top + tick, slRect.Left, slRect.Top, cutTickPaint);
                        canvas.DrawLine(slRect.Left, slRect.Top, slRect.Left + tick, slRect.Top, cutTickPaint);
                        canvas.DrawLine(slRect.Right - tick, slRect.Top, slRect.Right, slRect.Top, cutTickPaint);
                        canvas.DrawLine(slRect.Right, slRect.Top, slRect.Right, slRect.Top + tick, cutTickPaint);
                        canvas.DrawLine(slRect.Left, slRect.Bottom - tick, slRect.Left, slRect.Bottom, cutTickPaint);
                        canvas.DrawLine(slRect.Left, slRect.Bottom, slRect.Left + tick, slRect.Bottom, cutTickPaint);
                        canvas.DrawLine(slRect.Right - tick, slRect.Bottom, slRect.Right, slRect.Bottom, cutTickPaint);
                        canvas.DrawLine(slRect.Right, slRect.Bottom - tick, slRect.Right, slRect.Bottom, cutTickPaint);
                    }

                    if (_swapSourceSlotId == slot.Id)
                    {
                        canvas.DrawRect(slRect, swapSourceFillPaint);
                    }

                    if (_swapTargetSlotId == slot.Id)
                    {
                        canvas.DrawRect(slRect, swapFillPaint);
                        canvas.DrawRect(slRect, selectedSlotPaint);
                        activeSlotHudRect = slRect;
                    }
                    else if (_selectedSlotId == slot.Id)
                    {
                        if (_isSlotCropActive)
                        {
                            SKColor amberCrop = new(245, 158, 11, 240);
                            using var slotCropPaint = new SKPaint
                            {
                                Style = SKPaintStyle.Stroke,
                                Color = amberCrop,
                                StrokeWidth = 2f,
                                IsAntialias = true,
                            };
                            canvas.DrawRect(slRect, slotCropPaint);
                            DrawCropGuides(canvas, slRect, amberCrop);
                        }
                        else
                        {
                            canvas.DrawRect(slRect, selectedSlotPaint);
                        }

                        activeSlotHudRect ??= slRect;
                    }
                    else if (slot.AssignedAssetId is null)
                    {
                        canvas.DrawRect(slRect, emptySlotPaint);
                    }

                    if (slot.AssignedAssetId is null && slRect.Width >= 42f && slRect.Height >= 26f)
                    {
                        using SKTextBlob? badge = SKTextBlob.Create($"+ #{slot.Index + 1}", emptyHintFont);
                        if (badge is not null)
                        {
                            SKRect b = badge.Bounds;
                            float labelY = slRect.Height >= 46f
                                ? slRect.MidY - 5f - b.MidY
                                : slRect.MidY - b.MidY;
                            canvas.DrawText(badge, slRect.MidX - b.MidX, labelY, emptyHintTextPaint);
                        }

                        if (slRect.Width >= 56f && slRect.Height >= 46f)
                        {
                            using SKTextBlob? sizeBlob = SKTextBlob.Create(
                                $"{slot.BoundsMm.Width:0.#}x{slot.BoundsMm.Height:0.#} mm",
                                emptySubFont);
                            if (sizeBlob is not null)
                            {
                                SKRect sb = sizeBlob.Bounds;
                                canvas.DrawText(sizeBlob, slRect.MidX - sb.MidX, slRect.MidY + 9f - sb.MidY, emptyHintTextPaint);
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(_dragHudText) && activeSlotHudRect is { } hudRect && _selectedIds.IsDefaultOrEmpty)
                {
                    DrawHudBadge(canvas, _dragHudText, hudRect.MidX, hudRect.Bottom + 10f);
                }
            }

            if (_page.SafeMarginMm > 0.0)
            {
                (double sx, double sy) = _viewport.PageMmToDip(_page.SafeMarginMm, _page.SafeMarginMm);
                float sw = (float)(_page.WidthMm - (_page.SafeMarginMm * 2.0)) * scale;
                float sh = (float)(_page.HeightMm - (_page.SafeMarginMm * 2.0)) * scale;
                if (sw > 4f && sh > 4f)
                {
                    using SKPathEffect dash = SKPathEffect.CreateDash([4f, 4f], 0f);
                    using var safePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = new SKColor(148, 163, 184, 95),
                        StrokeWidth = 1f,
                        PathEffect = dash,
                    };
                    canvas.DrawRect(SKRect.Create((float)sx, (float)sy, sw, sh), safePaint);
                }
            }

            if (!_snapGuides.IsDefaultOrEmpty)
            {
                using var snapPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = new SKColor(236, 72, 153, 220),
                    StrokeWidth = 1.25f,
                    IsAntialias = false,
                };
                foreach (SnapGuideLine guide in _snapGuides)
                {
                    if (guide.IsVertical)
                    {
                        (double x, double y1) = _viewport.PageMmToDip(guide.PositionMm, guide.StartMm);
                        (_, double y2) = _viewport.PageMmToDip(guide.PositionMm, guide.EndMm);
                        canvas.DrawLine((float)x, (float)y1, (float)x, (float)y2, snapPaint);
                    }
                    else
                    {
                        (double x1, double y) = _viewport.PageMmToDip(guide.StartMm, guide.PositionMm);
                        (double x2, _) = _viewport.PageMmToDip(guide.EndMm, guide.PositionMm);
                        canvas.DrawLine((float)x1, (float)y, (float)x2, (float)y, snapPaint);
                    }
                }
            }

            if (_hoveredNodeId is { } hoverId &&
                (_selectedIds.IsDefaultOrEmpty || !_selectedIds.Contains(hoverId)) &&
                _page.Nodes.FirstOrDefault(n => n.Id == hoverId) is { } hoverNode)
            {
                (double hlx, double hly) = _viewport.PageMmToDip(hoverNode.BoundsMm.X, hoverNode.BoundsMm.Y);
                (double hcx, double hcy) = _viewport.PageMmToDip(hoverNode.BoundsMm.CenterX, hoverNode.BoundsMm.CenterY);
                int hoverSave = canvas.Save();
                try
                {
                    if (Math.Abs(hoverNode.RotationDeg) > 0.0001)
                    {
                        canvas.RotateDegrees((float)hoverNode.RotationDeg, (float)hcx, (float)hcy);
                    }

                    var hRect = SKRect.Create(
                        (float)hlx,
                        (float)hly,
                        (float)(hoverNode.BoundsMm.Width * scale),
                        (float)(hoverNode.BoundsMm.Height * scale));
                    using var hoverPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = _accentColor.WithAlpha(120),
                        StrokeWidth = 1f,
                        IsAntialias = true,
                    };
                    canvas.DrawRect(hRect, hoverPaint);
                }
                finally
                {
                    canvas.RestoreToCount(hoverSave);
                }
            }

            SelectionBoundsInfo selInfo = HitTestEngine.ComputeSelectionBounds(_page, _selectedIds);
            if (selInfo.HasSelection)
            {
                RectMm b = selInfo.BoundsMm;
                (double tlx, double tly) = _viewport.PageMmToDip(b.X, b.Y);
                (double cxDip, double cyDip) = _viewport.PageMmToDip(b.CenterX, b.CenterY);

                int selSave = canvas.Save();
                try
                {
                    if (Math.Abs(selInfo.RotationDeg) > 0.0001)
                    {
                        canvas.RotateDegrees((float)selInfo.RotationDeg, (float)cxDip, (float)cyDip);
                    }

                    var selRectDip = SKRect.Create((float)tlx, (float)tly, (float)(b.Width * scale), (float)(b.Height * scale));
                    SKColor amberCrop = new(245, 158, 11, 240);
                    using var selPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = _activeCropId is not null ? amberCrop : _accentColor,
                        StrokeWidth = _activeCropId is not null ? 2f : 1.5f,
                        IsAntialias = true,
                    };
                    canvas.DrawRect(selRectDip, selPaint);

                    if (_activeCropId is not null)
                    {
                        DrawCropGuides(canvas, selRectDip, amberCrop);
                    }
                    else
                    {
                        float rotStemTop = selRectDip.Top - (float)RotateHandleOffsetDip;
                        canvas.DrawLine(selRectDip.MidX, selRectDip.Top, selRectDip.MidX, rotStemTop, selPaint);

                        bool rotHovered = _hoveredHandle == TransformHandleKind.Rotate;
                        float rotRadius = rotHovered ? 5.8f : 4.6f;
                        using var handleShadow = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Color = new SKColor(0, 0, 0, 55),
                            IsAntialias = true,
                        };
                        using var handleFill = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Color = rotHovered ? _accentColor : SKColors.White,
                            IsAntialias = true,
                        };
                        canvas.DrawCircle(selRectDip.MidX, rotStemTop + 1f, rotRadius, handleShadow);
                        canvas.DrawCircle(selRectDip.MidX, rotStemTop, rotRadius, handleFill);
                        canvas.DrawCircle(selRectDip.MidX, rotStemTop, rotRadius, selPaint);

                        (TransformHandleKind Kind, float HX, float HY)[] handlePoints =
                        [
                            (TransformHandleKind.TopLeft, selRectDip.Left, selRectDip.Top),
                            (TransformHandleKind.Top, selRectDip.MidX, selRectDip.Top),
                            (TransformHandleKind.TopRight, selRectDip.Right, selRectDip.Top),
                            (TransformHandleKind.Right, selRectDip.Right, selRectDip.MidY),
                            (TransformHandleKind.BottomRight, selRectDip.Right, selRectDip.Bottom),
                            (TransformHandleKind.Bottom, selRectDip.MidX, selRectDip.Bottom),
                            (TransformHandleKind.BottomLeft, selRectDip.Left, selRectDip.Bottom),
                            (TransformHandleKind.Left, selRectDip.Left, selRectDip.MidY),
                        ];

                        using var normalFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                        using var hotFill = new SKPaint { Style = SKPaintStyle.Fill, Color = _accentColor, IsAntialias = true };
                        foreach ((TransformHandleKind kind, float hx, float hy) in handlePoints)
                        {
                            bool isHot = _hoveredHandle == kind;
                            float half = isHot ? 4.6f : 3.6f;
                            var shadowRect = SKRect.Create(hx - half, hy - half + 1f, half * 2f, half * 2f);
                            var hRect = SKRect.Create(hx - half, hy - half, half * 2f, half * 2f);
                            canvas.DrawRoundRect(shadowRect, 2.2f, 2.2f, handleShadow);
                            canvas.DrawRoundRect(hRect, 2.2f, 2.2f, isHot ? hotFill : normalFill);
                            canvas.DrawRoundRect(hRect, 2.2f, 2.2f, selPaint);
                        }
                    }
                }
                finally
                {
                    canvas.RestoreToCount(selSave);
                }

                if (!string.IsNullOrWhiteSpace(_dragHudText))
                {
                    RectMm aabb = HitTestEngine.ComputeRotatedAabb(b, selInfo.RotationDeg);
                    (double hcx, double hBottom) = _viewport.PageMmToDip(aabb.CenterX, aabb.Bottom);
                    DrawHudBadge(canvas, _dragHudText, (float)hcx, (float)hBottom + 10f);
                }
            }

            if (_marqueeMm is { } m)
            {
                double x0 = m.Width < 0 ? m.X + m.Width : m.X;
                double y0 = m.Height < 0 ? m.Y + m.Height : m.Y;
                (double mx, double my) = _viewport.PageMmToDip(x0, y0);
                var mRect = SKRect.Create((float)mx, (float)my, (float)(Math.Abs(m.Width) * scale), (float)(Math.Abs(m.Height) * scale));
                using var mFill = new SKPaint { Style = SKPaintStyle.Fill, Color = _accentColor.WithAlpha(35) };
                using var mStroke = new SKPaint { Style = SKPaintStyle.Stroke, Color = _accentColor, StrokeWidth = 1f };
                canvas.DrawRect(mRect, mFill);
                canvas.DrawRect(mRect, mStroke);
            }
        }

        private static void DrawCropGuides(SKCanvas canvas, SKRect rect, SKColor cropColor)
        {
            if (rect.Width < 18f || rect.Height < 18f)
            {
                return;
            }

            using var gridPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 255, 255, 155),
                StrokeWidth = 1f,
                IsAntialias = false,
            };

            float thirdW = rect.Width / 3f;
            float thirdH = rect.Height / 3f;
            canvas.DrawLine(rect.Left + thirdW, rect.Top, rect.Left + thirdW, rect.Bottom, gridPaint);
            canvas.DrawLine(rect.Left + (thirdW * 2f), rect.Top, rect.Left + (thirdW * 2f), rect.Bottom, gridPaint);
            canvas.DrawLine(rect.Left, rect.Top + thirdH, rect.Right, rect.Top + thirdH, gridPaint);
            canvas.DrawLine(rect.Left, rect.Top + (thirdH * 2f), rect.Right, rect.Top + (thirdH * 2f), gridPaint);

            float bracket = Math.Min(12f, Math.Min(rect.Width, rect.Height) * 0.25f);
            using var bracketPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = cropColor,
                StrokeWidth = 3f,
                StrokeCap = SKStrokeCap.Square,
                IsAntialias = false,
            };

            canvas.DrawLine(rect.Left, rect.Top + bracket, rect.Left, rect.Top, bracketPaint);
            canvas.DrawLine(rect.Left, rect.Top, rect.Left + bracket, rect.Top, bracketPaint);

            canvas.DrawLine(rect.Right - bracket, rect.Top, rect.Right, rect.Top, bracketPaint);
            canvas.DrawLine(rect.Right, rect.Top, rect.Right, rect.Top + bracket, bracketPaint);

            canvas.DrawLine(rect.Left, rect.Bottom - bracket, rect.Left, rect.Bottom, bracketPaint);
            canvas.DrawLine(rect.Left, rect.Bottom, rect.Left + bracket, rect.Bottom, bracketPaint);

            canvas.DrawLine(rect.Right - bracket, rect.Bottom, rect.Right, rect.Bottom, bracketPaint);
            canvas.DrawLine(rect.Right, rect.Bottom - bracket, rect.Right, rect.Bottom, bracketPaint);
        }

        private void DrawHudBadge(SKCanvas canvas, string text, float centerX, float topY)
        {
            using var font = new SKFont(SKTypeface.Default, 11f)
            {
                Subpixel = true,
                Edging = SKFontEdging.Antialias,
            };

            using SKTextBlob? blob = SKTextBlob.Create(text, font);
            if (blob is null)
            {
                return;
            }

            SKRect textBounds = blob.Bounds;
            float padX = 10f;
            float pillW = Math.Max(40f, textBounds.Width + (padX * 2f));
            float pillH = 22f;

            float x = Math.Clamp(centerX - (pillW * 0.5f), 8f, Math.Max(8f, (float)Bounds.Width - pillW - 8f));
            float y = Math.Clamp(topY, 8f, Math.Max(8f, (float)Bounds.Height - pillH - 8f));
            var pillRect = SKRect.Create(x, y, pillW, pillH);

            using var bgPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(20, 22, 26, 232),
                IsAntialias = true,
            };
            using var borderPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 255, 255, 52),
                StrokeWidth = 1f,
                IsAntialias = true,
            };
            using var textPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = SKColors.White,
                IsAntialias = true,
            };

            canvas.DrawRoundRect(pillRect, 11f, 11f, bgPaint);
            canvas.DrawRoundRect(pillRect, 11f, 11f, borderPaint);
            canvas.DrawText(blob, pillRect.MidX - textBounds.MidX, pillRect.MidY - textBounds.MidY, textPaint);
        }
    }
}
