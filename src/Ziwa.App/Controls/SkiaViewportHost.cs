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

    private const double HandleHitRadiusDip = 7.0;
    private const double RotateHandleOffsetDip = 22.0;

    private bool _hasInitialFit;
    private DragMode _dragMode;
    private Point _lastPointerDip;
    private (double XMm, double YMm) _dragStartMm;
    private (double XMm, double YMm) _marqueeCurrentMm;
    private TransformHandleKind _activeHandle;
    private RectMm _initialMoveBounds;
    private SlotId _sourceSlotId;
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
            }
        }
    }

    private void OnSessionChanged() => InvalidateVisual();

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
            _activeSnapGuides,
            _activeMarqueeMm));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        PageModel? page = Session?.EffectiveDocument.ActivePage ?? Page;
        if (!_hasInitialFit && page is not null && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _hasInitialFit = true;
            double dpiScale = VisualRoot?.RenderScaling ?? 1.0;
            SetCurrentValue(
                ViewportProperty,
                ViewportTransform.FitPage(
                    e.NewSize.Width,
                    e.NewSize.Height,
                    page.WidthMm,
                    page.HeightMm,
                    paddingDip: 28.0,
                    dpiScale: dpiScale));
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
            double delta = e.Delta.Y > 0 ? 0.12 : -0.12;
            CropSpec nextCrop = cropImage.Crop with
            {
                Zoom = Math.Clamp(cropImage.Crop.Zoom + delta, 1.0, 20.0),
            };
            Session.UpdateTransientPreview(doc => FreeformSceneOperations.UpdateImageCrop(doc, cropId, nextCrop));
            e.Handled = true;
            return;
        }

        Point cursor = e.GetPosition(this);
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

        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _dragMode = DragMode.ViewportPan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || Session is null)
        {
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
            _dragMode = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                ? DragMode.SlotCropPan
                : DragMode.CollageSlotSwap;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!shiftHeld)
        {
            Session.ClearSelection();
        }

        _dragMode = DragMode.Marquee;
        _marqueeCurrentMm = (xMm, yMm);
        _activeMarqueeMm = new RectMm(xMm, yMm, 0.0, 0.0);
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragMode == DragMode.None)
        {
            return;
        }

        Point currentDip = e.GetPosition(this);
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

        (double curXMm, double curYMm) = Viewport.DipToPageMm(currentDip.X, currentDip.Y);
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

        if (_dragMode == DragMode.None)
        {
            return;
        }

        DragMode finishedMode = _dragMode;
        _dragMode = DragMode.None;
        _activeHandle = TransformHandleKind.None;
        _activeSnapGuides = [];

        if (finishedMode == DragMode.Marquee && _activeMarqueeMm is { } marquee && Session is not null)
        {
            ImmutableArray<NodeId> hits = HitTestEngine.HitTestMarquee(Session.EffectiveDocument.ActivePage, marquee);
            Session.SetSelection(hits);
            _activeMarqueeMm = null;
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
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Session is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _dragMode = DragMode.None;
            _activeSnapGuides = [];
            _activeMarqueeMm = null;
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

        if (CommandRegistry is not null)
        {
            string? gesture = ToShortcutString(e.Key, e.KeyModifiers);
            if (gesture is not null && CommandRegistry.TryGetByShortcut(gesture, out _))
            {
                _ = CommandRegistry.TryExecuteShortcutAsync(gesture);
                e.Handled = true;
            }
        }
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

    private static string? ToShortcutString(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Delete && modifiers == KeyModifiers.None)
        {
            return "Delete";
        }

        if (!modifiers.HasFlag(KeyModifiers.Control))
        {
            return null;
        }

        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        return key switch
        {
            Key.Z => shift ? "Ctrl+Shift+Z" : "Ctrl+Z",
            Key.Y => "Ctrl+Y",
            Key.C => "Ctrl+C",
            Key.V => "Ctrl+V",
            Key.G => shift ? "Ctrl+Shift+G" : "Ctrl+G",
            Key.S => "Ctrl+S",
            Key.E => "Ctrl+E",
            Key.K => "Ctrl+K",
            Key.OemPeriod or Key.Decimal => "Ctrl+.",
            _ => null,
        };
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
        private readonly ImmutableArray<SnapGuideLine> _snapGuides;
        private readonly RectMm? _marqueeMm;

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
            ImmutableArray<SnapGuideLine> snapGuides,
            RectMm? marqueeMm)
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
            _snapGuides = snapGuides;
            _marqueeMm = marqueeMm;
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

                using (SKImageFilter? blur = SKImageFilter.CreateBlur(6f, 6f))
                using (var shadowPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(0, 0, 0, 72),
                    ImageFilter = blur,
                    IsAntialias = true,
                })
                {
                    var shadowRect = paperRectDip;
                    shadowRect.Offset(0f, 3f);
                    canvas.DrawRect(shadowRect, shadowPaint);
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
                    Color = new SKColor(148, 163, 184, 140),
                    StrokeWidth = 1f,
                    PathEffect = slotDash,
                };
                using var selectedSlotPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = _accentColor,
                    StrokeWidth = 2f,
                };

                foreach (CollageSlot slot in layout.Slots)
                {
                    (double slx, double sly) = _viewport.PageMmToDip(slot.BoundsMm.X, slot.BoundsMm.Y);
                    var slRect = SKRect.Create(
                        (float)slx,
                        (float)sly,
                        (float)(slot.BoundsMm.Width * scale),
                        (float)(slot.BoundsMm.Height * scale));
                    if (_selectedSlotId == slot.Id)
                    {
                        canvas.DrawRect(slRect, selectedSlotPaint);
                    }
                    else if (slot.AssignedAssetId is null)
                    {
                        canvas.DrawRect(slRect, emptySlotPaint);
                    }
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
                    using var selPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = _activeCropId is not null ? new SKColor(245, 158, 11, 240) : _accentColor,
                        StrokeWidth = _activeCropId is not null ? 2f : 1.5f,
                        IsAntialias = true,
                    };
                    canvas.DrawRect(selRectDip, selPaint);

                    if (_activeCropId is null)
                    {
                        float rotStemTop = selRectDip.Top - (float)RotateHandleOffsetDip;
                        canvas.DrawLine(selRectDip.MidX, selRectDip.Top, selRectDip.MidX, rotStemTop, selPaint);

                        using var handleFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                        canvas.DrawCircle(selRectDip.MidX, rotStemTop, 4.5f, handleFill);
                        canvas.DrawCircle(selRectDip.MidX, rotStemTop, 4.5f, selPaint);

                        (float HX, float HY)[] handlePoints =
                        [
                            (selRectDip.Left, selRectDip.Top),
                            (selRectDip.MidX, selRectDip.Top),
                            (selRectDip.Right, selRectDip.Top),
                            (selRectDip.Right, selRectDip.MidY),
                            (selRectDip.Right, selRectDip.Bottom),
                            (selRectDip.MidX, selRectDip.Bottom),
                            (selRectDip.Left, selRectDip.Bottom),
                            (selRectDip.Left, selRectDip.MidY),
                        ];

                        foreach ((float hx, float hy) in handlePoints)
                        {
                            var hRect = SKRect.Create(hx - 3.5f, hy - 3.5f, 7f, 7f);
                            canvas.DrawRect(hRect, handleFill);
                            canvas.DrawRect(hRect, selPaint);
                        }
                    }
                }
                finally
                {
                    canvas.RestoreToCount(selSave);
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
    }
}
