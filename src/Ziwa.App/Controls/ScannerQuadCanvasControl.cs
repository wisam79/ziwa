using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Ziwa.Domain.Scanner;

namespace Ziwa.App.Controls;

public sealed class ScannerQuadCanvasControl : Control
{
    public static readonly StyledProperty<SKBitmap?> SourceBitmapProperty =
        AvaloniaProperty.Register<ScannerQuadCanvasControl, SKBitmap?>(nameof(SourceBitmap));

    public static readonly StyledProperty<IReadOnlyList<DetectedDocumentRegion>?> RegionsProperty =
        AvaloniaProperty.Register<ScannerQuadCanvasControl, IReadOnlyList<DetectedDocumentRegion>?>(nameof(Regions));

    public static readonly StyledProperty<int> SelectedRegionIndexProperty =
        AvaloniaProperty.Register<ScannerQuadCanvasControl, int>(
            nameof(SelectedRegionIndex),
            defaultValue: 0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IBrush?> PasteboardBrushProperty =
        AvaloniaProperty.Register<ScannerQuadCanvasControl, IBrush?>(nameof(PasteboardBrush));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<ScannerQuadCanvasControl, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<IBrush?> SecondaryQuadBrushProperty =
        AvaloniaProperty.Register<ScannerQuadCanvasControl, IBrush?>(nameof(SecondaryQuadBrush));

    private Point _loupeCursorDip;

    static ScannerQuadCanvasControl()
    {
        AffectsRender<ScannerQuadCanvasControl>(
            SourceBitmapProperty,
            RegionsProperty,
            SelectedRegionIndexProperty,
            PasteboardBrushProperty,
            AccentBrushProperty,
            SecondaryQuadBrushProperty);
    }

    public ScannerQuadCanvasControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public event Action<int, int, PointPx>? CornerMoved;
    public event Action? CornerDragCompleted;

    public bool IsMagnifierLoupeVisible { get; private set; }
    public int DraggedCornerIndex { get; private set; } = -1;

    public SKBitmap? SourceBitmap
    {
        get => GetValue(SourceBitmapProperty);
        set => SetValue(SourceBitmapProperty, value);
    }

    public IReadOnlyList<DetectedDocumentRegion>? Regions
    {
        get => GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public int SelectedRegionIndex
    {
        get => GetValue(SelectedRegionIndexProperty);
        set => SetValue(SelectedRegionIndexProperty, value);
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

    public IBrush? SecondaryQuadBrush
    {
        get => GetValue(SecondaryQuadBrushProperty);
        set => SetValue(SecondaryQuadBrushProperty, value);
    }

    public bool TryGetCornerDipPosition(int regionIndex, int cornerIndex, out Point dipPoint)
    {
        if (SourceBitmap is null ||
            Regions is null ||
            regionIndex < 0 ||
            regionIndex >= Regions.Count ||
            cornerIndex is < 0 or > 3)
        {
            dipPoint = default;
            return false;
        }

        SKRect dest = ComputeDestRect();
        if (dest.Width <= 0f || dest.Height <= 0f)
        {
            dipPoint = default;
            return false;
        }

        PointPx corner = Regions[regionIndex].Quad.GetCorner(cornerIndex);
        dipPoint = ImageToDip(corner, dest, SourceBitmap.Width, SourceBitmap.Height);
        return true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        SKColor pasteboard = ResolveColor(PasteboardBrush, new SKColor(22, 22, 24, 255));
        SKColor accent = ResolveColor(AccentBrush, new SKColor(77, 163, 255, 255));
        SKColor secondary = ResolveColor(SecondaryQuadBrush, new SKColor(174, 174, 178, 255));
        SKBitmap? sourceBmp = SourceBitmap;
        if (sourceBmp is not null && sourceBmp.Handle == IntPtr.Zero)
        {
            sourceBmp = null;
        }

        SKRect dest = ComputeDestRect(sourceBmp);
        SKImage? sourceImageSnapshot = sourceBmp is not null ? SKImage.FromBitmap(sourceBmp) : null;

        IReadOnlyList<DetectedDocumentRegion> snapshotRegions = Regions is null
            ? []
            : Regions.ToArray();

        context.Custom(new QuadDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            dest,
            sourceImageSnapshot,
            snapshotRegions,
            SelectedRegionIndex,
            DraggedCornerIndex,
            IsMagnifierLoupeVisible,
            _loupeCursorDip,
            pasteboard,
            accent,
            secondary));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || SourceBitmap is null || Regions is null || Regions.Count == 0)
        {
            return;
        }

        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        SKRect dest = ComputeDestRect();
        if (dest.Width <= 0f || dest.Height <= 0f)
        {
            return;
        }

        int selIdx = Math.Clamp(SelectedRegionIndex, 0, Regions.Count - 1);
        DocumentQuadPx activeQuad = Regions[selIdx].Quad;

        for (int c = 0; c < 4; c++)
        {
            Point handleDip = ImageToDip(activeQuad.GetCorner(c), dest, SourceBitmap.Width, SourceBitmap.Height);
            double dist = double.Hypot(point.Position.X - handleDip.X, point.Position.Y - handleDip.Y);
            if (dist <= 14.0)
            {
                DraggedCornerIndex = c;
                IsMagnifierLoupeVisible = true;
                _loupeCursorDip = point.Position;
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        PointPx imgClick = DipToImage(point.Position, dest, SourceBitmap.Width, SourceBitmap.Height);
        for (int r = 0; r < Regions.Count; r++)
        {
            if (IsPointInsideQuad(imgClick, Regions[r].Quad))
            {
                SetCurrentValue(SelectedRegionIndexProperty, r);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEnabled || DraggedCornerIndex is < 0 or > 3 || SourceBitmap is null || Regions is null || Regions.Count == 0)
        {
            return;
        }

        SKRect dest = ComputeDestRect();
        if (dest.Width <= 0f || dest.Height <= 0f)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        _loupeCursorDip = pos;
        int selIdx = Math.Clamp(SelectedRegionIndex, 0, Regions.Count - 1);
        PointPx newCorner = DipToImage(pos, dest, SourceBitmap.Width, SourceBitmap.Height);
        CornerMoved?.Invoke(selIdx, DraggedCornerIndex, newCorner);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (DraggedCornerIndex >= 0)
        {
            DraggedCornerIndex = -1;
            IsMagnifierLoupeVisible = false;
            CornerDragCompleted?.Invoke();
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private SKRect ComputeDestRect() => ComputeDestRect(SourceBitmap);

    private SKRect ComputeDestRect(SKBitmap? bmp)
    {
        if (bmp is null || bmp.Handle == IntPtr.Zero || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return SKRect.Empty;
        }

        const float pad = 16f;
        float availW = Math.Max(1f, (float)Bounds.Width - (pad * 2f));
        float availH = Math.Max(1f, (float)Bounds.Height - (pad * 2f));
        float scale = Math.Min(availW / bmp.Width, availH / bmp.Height);
        float drawW = bmp.Width * scale;
        float drawH = bmp.Height * scale;
        float left = ((float)Bounds.Width - drawW) * 0.5f;
        float top = ((float)Bounds.Height - drawH) * 0.5f;
        return SKRect.Create(left, top, drawW, drawH);
    }

    private static Point ImageToDip(PointPx p, SKRect dest, int imgW, int imgH) =>
        new(
            dest.Left + ((p.X / Math.Max(1, imgW)) * dest.Width),
            dest.Top + ((p.Y / Math.Max(1, imgH)) * dest.Height));

    private static PointPx DipToImage(Point dip, SKRect dest, int imgW, int imgH)
    {
        double x = ((dip.X - dest.Left) / Math.Max(1f, dest.Width)) * imgW;
        double y = ((dip.Y - dest.Top) / Math.Max(1f, dest.Height)) * imgH;
        return new PointPx(Math.Clamp(x, 0.0, imgW), Math.Clamp(y, 0.0, imgH));
    }

    private static bool IsPointInsideQuad(PointPx p, DocumentQuadPx quad)
    {
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            PointPx pi = quad.GetCorner(i);
            PointPx pj = quad.GetCorner(j);
            bool intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                             (p.X < ((pj.X - pi.X) * (p.Y - pi.Y) / ((pj.Y - pi.Y) + 1e-9)) + pi.X);
            if (intersect)
            {
                inside = !inside;
            }
        }

        return inside;
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

    private sealed class QuadDrawOperation : ICustomDrawOperation
    {
        private readonly SKRect _destRect;
        private readonly SKImage? _sourceImage;
        private readonly IReadOnlyList<DetectedDocumentRegion> _regions;
        private readonly int _selectedIndex;
        private readonly int _draggedCornerIndex;
        private readonly bool _showLoupe;
        private readonly Point _loupeCursorDip;
        private readonly SKColor _pasteboard;
        private readonly SKColor _accent;
        private readonly SKColor _secondary;

        public QuadDrawOperation(
            Rect bounds,
            SKRect destRect,
            SKImage? sourceImage,
            IReadOnlyList<DetectedDocumentRegion> regions,
            int selectedIndex,
            int draggedCornerIndex,
            bool showLoupe,
            Point loupeCursorDip,
            SKColor pasteboard,
            SKColor accent,
            SKColor secondary)
        {
            Bounds = bounds;
            _destRect = destRect;
            _sourceImage = sourceImage;
            _regions = regions;
            _selectedIndex = selectedIndex;
            _draggedCornerIndex = draggedCornerIndex;
            _showLoupe = showLoupe;
            _loupeCursorDip = loupeCursorDip;
            _pasteboard = pasteboard;
            _accent = accent;
            _secondary = secondary;
        }

        public Rect Bounds { get; }
        public void Dispose() => _sourceImage?.Dispose();
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
            using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = _pasteboard };
            canvas.DrawRect(viewport, bgPaint);

            if (_sourceImage is null || _destRect.IsEmpty)
            {
                return;
            }

            using var imgPaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
            canvas.DrawImage(_sourceImage, _destRect, imgPaint);

            for (int r = 0; r < _regions.Count; r++)
            {
                bool isSelected = r == _selectedIndex;
                SKColor strokeColor = isSelected ? _accent : _secondary;
                DocumentQuadPx q = _regions[r].Quad;

                SKPoint[] pts = new SKPoint[4];
                for (int c = 0; c < 4; c++)
                {
                    Point d = ImageToDip(q.GetCorner(c), _destRect, _sourceImage.Width, _sourceImage.Height);
                    pts[c] = new SKPoint((float)d.X, (float)d.Y);
                }

                using var path = new SKPath();
                path.MoveTo(pts[0]);
                path.LineTo(pts[1]);
                path.LineTo(pts[2]);
                path.LineTo(pts[3]);
                path.Close();

                using var fillPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = new SKColor(strokeColor.Red, strokeColor.Green, strokeColor.Blue, (byte)(isSelected ? 40 : 20)),
                };
                canvas.DrawPath(path, fillPaint);

                using var strokePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = strokeColor,
                    StrokeWidth = isSelected ? 2.4f : 1.5f,
                    IsAntialias = true,
                };
                canvas.DrawPath(path, strokePaint);

                if (isSelected)
                {
                    using var handleFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                    using var handleRing = new SKPaint { Style = SKPaintStyle.Stroke, Color = _accent, StrokeWidth = 2.4f, IsAntialias = true };
                    for (int c = 0; c < 4; c++)
                    {
                        float radius = c == _draggedCornerIndex ? 8.5f : 6.5f;
                        canvas.DrawCircle(pts[c], radius, handleFill);
                        canvas.DrawCircle(pts[c], radius, handleRing);
                    }
                }
            }

            if (_showLoupe &&
                _draggedCornerIndex is >= 0 and <= 3 &&
                _selectedIndex >= 0 &&
                _selectedIndex < _regions.Count)
            {
                DrawMagnifierLoupe(canvas, _regions[_selectedIndex].Quad);
            }
        }

        private void DrawMagnifierLoupe(SKCanvas canvas, DocumentQuadPx quad)
        {
            if (_sourceImage is null)
            {
                return;
            }

            const float loupeRadius = 58f;
            const float zoomFactor = 3.5f;
            float cx = Math.Clamp((float)_loupeCursorDip.X, loupeRadius + 8f, Math.Max(loupeRadius + 8f, (float)Bounds.Width - loupeRadius - 8f));
            float cy = _loupeCursorDip.Y < 140.0
                ? Math.Min((float)Bounds.Height - loupeRadius - 8f, (float)_loupeCursorDip.Y + 82f)
                : Math.Max(loupeRadius + 8f, (float)_loupeCursorDip.Y - 82f);

            PointPx corner = quad.GetCorner(_draggedCornerIndex);
            PointPx prevCorner = quad.GetCorner((_draggedCornerIndex + 3) % 4);
            PointPx nextCorner = quad.GetCorner((_draggedCornerIndex + 1) % 4);

            float imgPerDip = Math.Max(_sourceImage.Width / Math.Max(1f, _destRect.Width), _sourceImage.Height / Math.Max(1f, _destRect.Height));
            float sampleHalfSpanPx = (loupeRadius * imgPerDip) / zoomFactor;

            var srcRect = SKRect.Create(
                (float)corner.X - sampleHalfSpanPx,
                (float)corner.Y - sampleHalfSpanPx,
                sampleHalfSpanPx * 2f,
                sampleHalfSpanPx * 2f);
            var dstRect = SKRect.Create(cx - loupeRadius, cy - loupeRadius, loupeRadius * 2f, loupeRadius * 2f);

            int save = canvas.Save();
            try
            {
                using var clipPath = new SKPath();
                clipPath.AddCircle(cx, cy, loupeRadius);
                canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);

                using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = _pasteboard };
                canvas.DrawRect(dstRect, bgPaint);

                using var imgPaint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Low };
                canvas.DrawImage(_sourceImage, srcRect, dstRect, imgPaint);

                float pxToLoupeScale = loupeRadius / Math.Max(0.01f, sampleHalfSpanPx);
                var prevPt = new SKPoint(
                    cx + ((float)(prevCorner.X - corner.X) * pxToLoupeScale),
                    cy + ((float)(prevCorner.Y - corner.Y) * pxToLoupeScale));
                var nextPt = new SKPoint(
                    cx + ((float)(nextCorner.X - corner.X) * pxToLoupeScale),
                    cy + ((float)(nextCorner.Y - corner.Y) * pxToLoupeScale));

                using var edgePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = _accent,
                    StrokeWidth = 2.0f,
                    IsAntialias = true,
                };
                canvas.DrawLine(prevPt, new SKPoint(cx, cy), edgePaint);
                canvas.DrawLine(new SKPoint(cx, cy), nextPt, edgePaint);

                using var crossPaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = SKColors.White,
                    StrokeWidth = 1.4f,
                };
                canvas.DrawLine(cx - 12f, cy, cx + 12f, cy, crossPaint);
                canvas.DrawLine(cx, cy - 12f, cx, cy + 12f, crossPaint);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }

            using var outerRing = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = _accent,
                StrokeWidth = 3.0f,
                IsAntialias = true,
            };
            canvas.DrawCircle(cx, cy, loupeRadius, outerRing);
        }
    }
}
