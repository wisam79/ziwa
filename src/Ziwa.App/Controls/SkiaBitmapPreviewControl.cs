using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Ziwa.App.Controls;

public sealed class SkiaBitmapPreviewControl : Control
{
    public static readonly StyledProperty<SKBitmap?> PreviewBitmapProperty =
        AvaloniaProperty.Register<SkiaBitmapPreviewControl, SKBitmap?>(nameof(PreviewBitmap));

    public static readonly StyledProperty<IBrush?> PasteboardBrushProperty =
        AvaloniaProperty.Register<SkiaBitmapPreviewControl, IBrush?>(nameof(PasteboardBrush));

    static SkiaBitmapPreviewControl()
    {
        AffectsRender<SkiaBitmapPreviewControl>(PreviewBitmapProperty, PasteboardBrushProperty);
    }

    public SkiaBitmapPreviewControl()
    {
        ClipToBounds = true;
    }

    public SKBitmap? PreviewBitmap
    {
        get => GetValue(PreviewBitmapProperty);
        set => SetValue(PreviewBitmapProperty, value);
    }

    public IBrush? PasteboardBrush
    {
        get => GetValue(PasteboardBrushProperty);
        set => SetValue(PasteboardBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        SKColor bg = PasteboardBrush is ISolidColorBrush solid
            ? new SKColor(solid.Color.R, solid.Color.G, solid.Color.B, solid.Color.A)
            : new SKColor(22, 22, 24, 255);

        SKBitmap? bmp = PreviewBitmap;
        SKImage? snapshot = bmp is not null && bmp.Handle != IntPtr.Zero ? SKImage.FromBitmap(bmp) : null;
        context.Custom(new PreviewDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), snapshot, bg));
    }

    private sealed class PreviewDrawOperation(Rect bounds, SKImage? image, SKColor bgColor) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        public void Dispose() => image?.Dispose();
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
            using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = bgColor };
            canvas.DrawRect(viewport, bgPaint);

            if (image is null || image.Width <= 0 || image.Height <= 0)
            {
                return;
            }

            const float pad = 16f;
            float availW = Math.Max(1f, (float)Bounds.Width - (pad * 2f));
            float availH = Math.Max(1f, (float)Bounds.Height - (pad * 2f));
            float scale = Math.Min(availW / image.Width, availH / image.Height);
            float drawW = image.Width * scale;
            float drawH = image.Height * scale;
            float left = ((float)Bounds.Width - drawW) * 0.5f;
            float top = ((float)Bounds.Height - drawH) * 0.5f;
            var dest = SKRect.Create(left, top, drawW, drawH);

            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawImage(image, dest, paint);
        }
    }
}
