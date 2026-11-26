using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Ziwa.Domain.Ai;
using Ziwa.Rendering.Ai;

namespace Ziwa.App.Controls;

public sealed class MaskBrushCanvasControl : Control
{
    public static readonly StyledProperty<EditableAlphaMask?> MaskSessionProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, EditableAlphaMask?>(nameof(MaskSession));

    public static readonly StyledProperty<SKBitmap?> OriginalBitmapProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, SKBitmap?>(nameof(OriginalBitmap));

    public static readonly StyledProperty<SKBitmap?> CutoutBitmapProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, SKBitmap?>(nameof(CutoutBitmap));

    public static readonly StyledProperty<MaskPreviewMode> PreviewModeProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, MaskPreviewMode>(
            nameof(PreviewMode),
            defaultValue: MaskPreviewMode.AfterCheckerboard);

    public static readonly StyledProperty<double> SplitRatioProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, double>(
            nameof(SplitRatio),
            defaultValue: 0.5,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<MaskBrushMode> BrushModeProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, MaskBrushMode>(
            nameof(BrushMode),
            defaultValue: MaskBrushMode.Erase);

    public static readonly StyledProperty<double> BrushRadiusPxProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, double>(
            nameof(BrushRadiusPx),
            defaultValue: 28.0);

    public static readonly StyledProperty<double> BrushSoftnessProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, double>(
            nameof(BrushSoftness),
            defaultValue: 0.55);

    public static readonly StyledProperty<IBrush?> PasteboardBrushProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, IBrush?>(nameof(PasteboardBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<MaskBrushCanvasControl, IBrush?>(nameof(AccentBrush));

    private Point? _lastBrushImgPt;
    private Point? _cursorDip;
    private DragAction _dragAction;
    private Point _lastPanDip;
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;

    static MaskBrushCanvasControl()
    {
        AffectsRender<MaskBrushCanvasControl>(
            MaskSessionProperty,
            OriginalBitmapProperty,
            CutoutBitmapProperty,
            PreviewModeProperty,
            SplitRatioProperty,
            BrushModeProperty,
            BrushRadiusPxProperty,
            BrushSoftnessProperty,
            PasteboardBrushProperty,
            AccentBrushProperty);
    }

    public MaskBrushCanvasControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public event Action? MaskModified;

    public EditableAlphaMask? MaskSession
    {
        get => GetValue(MaskSessionProperty);
        set => SetValue(MaskSessionProperty, value);
    }

    public SKBitmap? OriginalBitmap
    {
        get => GetValue(OriginalBitmapProperty);
        set => SetValue(OriginalBitmapProperty, value);
    }

    public SKBitmap? CutoutBitmap
    {
        get => GetValue(CutoutBitmapProperty);
        set => SetValue(CutoutBitmapProperty, value);
    }

    public MaskPreviewMode PreviewMode
    {
        get => GetValue(PreviewModeProperty);
        set => SetValue(PreviewModeProperty, value);
    }

    public double SplitRatio
    {
        get => GetValue(SplitRatioProperty);
        set => SetValue(SplitRatioProperty, value);
    }

    public MaskBrushMode BrushMode
    {
        get => GetValue(BrushModeProperty);
        set => SetValue(BrushModeProperty, value);
    }

    public double BrushRadiusPx
    {
        get => GetValue(BrushRadiusPxProperty);
        set => SetValue(BrushRadiusPxProperty, value);
    }

    public double BrushSoftness
    {
        get => GetValue(BrushSoftnessProperty);
        set => SetValue(BrushSoftnessProperty, value);
    }

    public IBrush? PasteboardBrush
    {
        get => GetValue(PasteboardBrushProperty);
        set => SetValue(PasteboardBrushProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public void ResetView()
    {
        _zoom = 1.0;
        _panX = 0.0;
        _panY = 0.0;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MaskSessionProperty)
        {
            ResetView();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        SKColor pasteboardColor = ResolveColor(PasteboardBrush, new SKColor(22, 22, 24, 255));
        SKColor accentColor = ResolveColor(AccentBrush, new SKColor(77, 163, 255, 255));
        SKBitmap? orig = OriginalBitmap is { Handle: not 0 } o ? o : null;
        SKBitmap? cut = CutoutBitmap is { Handle: not 0 } c ? c : null;
        SKRect destRect = ComputeDestRect(orig);
        SKImage? origSnapshot = orig is not null ? SKImage.FromBitmap(orig) : null;
        SKImage? cutSnapshot = cut is not null ? SKImage.FromBitmap(cut) : null;

        context.Custom(new MaskDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            destRect,
            origSnapshot,
            cutSnapshot,
            PreviewMode,
            (float)SplitRatio,
            pasteboardColor,
            accentColor,
            _cursorDip,
            ComputeDisplayBrushRadiusDip(destRect)));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        double nextZoom = Math.Clamp(_zoom * factor, 0.5, 10.0);
        if (Math.Abs(nextZoom - _zoom) > 1e-4)
        {
            Point pos = e.GetPosition(this);
            double ratio = nextZoom / _zoom;
            double centerX = Bounds.Width * 0.5;
            double centerY = Bounds.Height * 0.5;
            _panX = pos.X - centerX - ((pos.X - centerX - _panX) * ratio);
            _panY = pos.Y - centerY - ((pos.Y - centerY - _panY) * ratio);
            _zoom = nextZoom;
            InvalidateVisual();
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        _cursorDip = point.Position;

        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _dragAction = DragAction.Pan;
            _lastPanDip = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || MaskSession is null || OriginalBitmap is null || CutoutBitmap is null)
        {
            return;
        }

        SKRect destRect = ComputeDestRect();
        if (PreviewMode == MaskPreviewMode.SplitBeforeAfter)
        {
            double splitX = destRect.Left + (destRect.Width * Math.Clamp(SplitRatio, 0.02, 0.98));
            if (Math.Abs(point.Position.X - splitX) <= 12.0 &&
                point.Position.Y >= destRect.Top &&
                point.Position.Y <= destRect.Bottom)
            {
                _dragAction = DragAction.SplitLine;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (TryMapDipToImage(point.Position, destRect, out Point imgPt))
        {
            _dragAction = DragAction.Brush;
            MaskSession.BeginStroke();
            _lastBrushImgPt = imgPt;
            ApplyBrushDab(imgPt, imgPt);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pos = e.GetPosition(this);
        _cursorDip = pos;

        if (_dragAction == DragAction.Pan)
        {
            _panX += pos.X - _lastPanDip.X;
            _panY += pos.Y - _lastPanDip.Y;
            _lastPanDip = pos;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_dragAction == DragAction.SplitLine)
        {
            SKRect destRect = ComputeDestRect();
            if (destRect.Width > 1f)
            {
                double ratio = Math.Clamp((pos.X - destRect.Left) / destRect.Width, 0.02, 0.98);
                SetCurrentValue(SplitRatioProperty, ratio);
            }

            e.Handled = true;
            return;
        }

        if (_dragAction == DragAction.Brush &&
            MaskSession is not null &&
            OriginalBitmap is not null &&
            CutoutBitmap is not null &&
            TryMapDipToImage(pos, ComputeDestRect(), out Point imgPt))
        {
            Point prev = _lastBrushImgPt ?? imgPt;
            _lastBrushImgPt = imgPt;
            ApplyBrushDab(prev, imgPt);
            e.Handled = true;
            return;
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragAction == DragAction.Brush && MaskSession is not null && MaskSession.EndStroke())
        {
            MaskModified?.Invoke();
        }

        _dragAction = DragAction.None;
        _lastBrushImgPt = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _cursorDip = null;
        InvalidateVisual();
    }

    private void ApplyBrushDab(Point p0, Point p1)
    {
        if (MaskSession is null || OriginalBitmap is null || CutoutBitmap is null)
        {
            return;
        }

        SKRectI dirty = MaskSession.ApplyBrushSegment(
            (float)p0.X,
            (float)p0.Y,
            (float)p1.X,
            (float)p1.Y,
            (float)BrushRadiusPx,
            (float)BrushSoftness,
            BrushMode);

        if (!dirty.IsEmpty)
        {
            AlphaMaskCompositor.ApplyAlphaToBitmapInPlace(
                CutoutBitmap,
                OriginalBitmap,
                MaskSession.AlphaSpan,
                dirty);
            MaskModified?.Invoke();
            InvalidateVisual();
        }
    }

    private SKRect ComputeDestRect() => ComputeDestRect(OriginalBitmap);

    private SKRect ComputeDestRect(SKBitmap? bmp)
    {
        if (bmp is null || bmp.Handle == IntPtr.Zero || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return SKRect.Empty;
        }

        float pad = 16f;
        float availW = Math.Max(1f, (float)Bounds.Width - (pad * 2f));
        float availH = Math.Max(1f, (float)Bounds.Height - (pad * 2f));
        float fitScale = Math.Min(availW / bmp.Width, availH / bmp.Height) * (float)_zoom;
        float drawW = bmp.Width * fitScale;
        float drawH = bmp.Height * fitScale;
        float left = ((float)Bounds.Width - drawW) * 0.5f + (float)_panX;
        float top = ((float)Bounds.Height - drawH) * 0.5f + (float)_panY;
        return SKRect.Create(left, top, drawW, drawH);
    }

    private float ComputeDisplayBrushRadiusDip(SKRect destRect)
    {
        if (OriginalBitmap is null || destRect.Width <= 0f)
        {
            return 0f;
        }

        float scale = destRect.Width / OriginalBitmap.Width;
        return Math.Max(2f, (float)BrushRadiusPx * scale);
    }

    private bool TryMapDipToImage(Point dip, SKRect destRect, out Point imagePoint)
    {
        if (OriginalBitmap is null || destRect.Width <= 0f || destRect.Height <= 0f)
        {
            imagePoint = default;
            return false;
        }

        double x = ((dip.X - destRect.Left) / destRect.Width) * OriginalBitmap.Width;
        double y = ((dip.Y - destRect.Top) / destRect.Height) * OriginalBitmap.Height;
        imagePoint = new Point(
            Math.Clamp(x, 0.0, OriginalBitmap.Width - 1.0),
            Math.Clamp(y, 0.0, OriginalBitmap.Height - 1.0));
        return true;
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

    private enum DragAction
    {
        None,
        Brush,
        Pan,
        SplitLine,
    }

    private sealed class MaskDrawOperation : ICustomDrawOperation
    {
        private readonly SKRect _destRect;
        private readonly SKImage? _originalImage;
        private readonly SKImage? _cutoutImage;
        private readonly MaskPreviewMode _previewMode;
        private readonly float _splitRatio;
        private readonly SKColor _pasteboardColor;
        private readonly SKColor _accentColor;
        private readonly Point? _cursorDip;
        private readonly float _brushRadiusDip;

        public MaskDrawOperation(
            Rect bounds,
            SKRect destRect,
            SKImage? originalImage,
            SKImage? cutoutImage,
            MaskPreviewMode previewMode,
            float splitRatio,
            SKColor pasteboardColor,
            SKColor accentColor,
            Point? cursorDip,
            float brushRadiusDip)
        {
            Bounds = bounds;
            _destRect = destRect;
            _originalImage = originalImage;
            _cutoutImage = cutoutImage;
            _previewMode = previewMode;
            _splitRatio = splitRatio;
            _pasteboardColor = pasteboardColor;
            _accentColor = accentColor;
            _cursorDip = cursorDip;
            _brushRadiusDip = brushRadiusDip;
        }

        public Rect Bounds { get; }
        public void Dispose()
        {
            _originalImage?.Dispose();
            _cutoutImage?.Dispose();
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

            var viewport = SKRect.Create(0f, 0f, (float)Bounds.Width, (float)Bounds.Height);
            using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = _pasteboardColor };
            canvas.DrawRect(viewport, bgPaint);

            if (_originalImage is not null && _cutoutImage is not null && !_destRect.IsEmpty)
            {
                AlphaMaskCompositor.RenderPreview(
                    canvas,
                    _destRect,
                    _originalImage,
                    _cutoutImage,
                    _previewMode,
                    _splitRatio);
            }

            if (_cursorDip is { } c && _brushRadiusDip > 1f)
            {
                using var ringPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = _accentColor,
                    StrokeWidth = 1.5f,
                    IsAntialias = true,
                };
                canvas.DrawCircle((float)c.X, (float)c.Y, _brushRadiusDip, ringPaint);
            }
        }
    }
}
