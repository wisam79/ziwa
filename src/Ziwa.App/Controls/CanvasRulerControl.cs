using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ziwa.Rendering.Viewport;

namespace Ziwa.App.Controls;

public sealed class CanvasRulerControl : Control
{
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<CanvasRulerControl, Orientation>(nameof(Orientation), Orientation.Horizontal);

    public static readonly StyledProperty<ViewportTransform> ViewportProperty =
        AvaloniaProperty.Register<CanvasRulerControl, ViewportTransform>(
            nameof(Viewport),
            new ViewportTransform(1.0, 32.0, 32.0, 1.0));

    public static readonly StyledProperty<double> PageLengthMmProperty =
        AvaloniaProperty.Register<CanvasRulerControl, double>(nameof(PageLengthMm), 148.0);

    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<CanvasRulerControl, IBrush?>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<CanvasRulerControl, IBrush?>(nameof(TickBrush));

    public static readonly StyledProperty<IBrush?> PaperHighlightBrushProperty =
        AvaloniaProperty.Register<CanvasRulerControl, IBrush?>(nameof(PaperHighlightBrush));

    public static readonly StyledProperty<IBrush?> OriginBrushProperty =
        AvaloniaProperty.Register<CanvasRulerControl, IBrush?>(nameof(OriginBrush));

    private static readonly double[] StepCandidatesMm = [1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0];

    static CanvasRulerControl()
    {
        AffectsRender<CanvasRulerControl>(
            OrientationProperty,
            ViewportProperty,
            PageLengthMmProperty,
            BackgroundBrushProperty,
            TickBrushProperty,
            PaperHighlightBrushProperty,
            OriginBrushProperty);
    }

    public CanvasRulerControl()
    {
        ClipToBounds = true;
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public ViewportTransform Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    public double PageLengthMm
    {
        get => GetValue(PageLengthMmProperty);
        set => SetValue(PageLengthMmProperty, value);
    }

    public IBrush? BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public IBrush? PaperHighlightBrush
    {
        get => GetValue(PaperHighlightBrushProperty);
        set => SetValue(PaperHighlightBrushProperty, value);
    }

    public IBrush? OriginBrush
    {
        get => GetValue(OriginBrushProperty);
        set => SetValue(OriginBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        IBrush bgBrush = BackgroundBrush ?? Brushes.Transparent;
        IBrush tickBrush = TickBrush ?? Brushes.Gray;
        IBrush highlightBrush = PaperHighlightBrush ?? Brushes.Transparent;
        IBrush originBrush = OriginBrush ?? tickBrush;

        context.FillRectangle(bgBrush, new Rect(0, 0, Bounds.Width, Bounds.Height));

        double scale = Viewport.DipPerMm;
        if (scale <= 0.001)
        {
            return;
        }

        bool isHorizontal = Orientation == Orientation.Horizontal;
        double panDip = isHorizontal ? Viewport.PanXDip : Viewport.PanYDip;
        double controlSpanDip = isHorizontal ? Bounds.Width : Bounds.Height;

        double paperStartDip = panDip;
        double paperEndDip = panDip + (PageLengthMm * scale);
        double clampedStart = Math.Max(0, paperStartDip);
        double clampedEnd = Math.Min(controlSpanDip, paperEndDip);
        if (clampedEnd > clampedStart)
        {
            Rect highlightRect = isHorizontal
                ? new Rect(clampedStart, 0, clampedEnd - clampedStart, Bounds.Height)
                : new Rect(0, clampedStart, Bounds.Width, clampedEnd - clampedStart);
            context.FillRectangle(highlightBrush, highlightRect);
        }

        double majorStepMm = ChooseMajorStepMm(scale);
        double minorStepMm = majorStepMm / 5.0;
        double minMm = Math.Floor((-panDip) / scale / majorStepMm) * majorStepMm;
        double maxMm = Math.Ceiling((controlSpanDip - panDip) / scale / majorStepMm) * majorStepMm;

        var tickPen = new Pen(tickBrush, 1.0);
        var originPen = new Pen(originBrush, 1.5);
        var typeface = Typeface.Default;

        for (double mm = minMm; mm <= maxMm; mm += minorStepMm)
        {
            double posDip = panDip + (mm * scale);
            if (posDip < -2.0 || posDip > controlSpanDip + 2.0)
            {
                continue;
            }

            double snappedMajor = Math.Round(mm / majorStepMm) * majorStepMm;
            bool isMajor = Math.Abs(mm - snappedMajor) < (minorStepMm * 0.15);
            bool isOriginOrEnd = Math.Abs(mm) < 0.05 || Math.Abs(mm - PageLengthMm) < 0.05;

            double tickLen = isMajor ? 10.0 : 4.5;
            Pen pen = isOriginOrEnd ? originPen : tickPen;

            if (isHorizontal)
            {
                context.DrawLine(pen, new Point(posDip, Bounds.Height - tickLen), new Point(posDip, Bounds.Height));
                if (isMajor && posDip >= 12.0 && posDip <= controlSpanDip - 22.0)
                {
                    string label = ((int)Math.Round(snappedMajor)).ToString(CultureInfo.InvariantCulture);
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9.5, isOriginOrEnd ? originBrush : tickBrush);
                    context.DrawText(ft, new Point(posDip + 2.0, 1.0));
                }
            }
            else
            {
                context.DrawLine(pen, new Point(Bounds.Width - tickLen, posDip), new Point(Bounds.Width, posDip));
                if (isMajor && posDip >= 12.0 && posDip <= controlSpanDip - 14.0)
                {
                    string label = ((int)Math.Round(snappedMajor)).ToString(CultureInfo.InvariantCulture);
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9.5, isOriginOrEnd ? originBrush : tickBrush);
                    context.DrawText(ft, new Point(2.0, posDip + 1.0));
                }
            }
        }
    }

    private static double ChooseMajorStepMm(double dipPerMm)
    {
        foreach (double candidate in StepCandidatesMm)
        {
            if (candidate * dipPerMm >= 38.0)
            {
                return candidate;
            }
        }

        return 100.0;
    }
}
