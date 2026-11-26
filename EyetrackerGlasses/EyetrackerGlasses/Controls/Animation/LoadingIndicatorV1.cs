using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EyetrackerGlasses;

/// <summary>
/// Анимация загрузки: вращающиеся дуги -> кольцо -> пульсовая буква «М» -> затухание -> повтор.
/// </summary>
public class LoadingIndicatorV1 : Control
{
    // ---------- Настройки ----------
    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<LoadingIndicatorV1, Color>(nameof(LineColor), Colors.Orange);

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<LoadingIndicatorV1, double>(nameof(LineThickness), 6.0);

    public static readonly StyledProperty<Color> BackgroundColorProperty =
        AvaloniaProperty.Register<LoadingIndicatorV1, Color>(nameof(BackgroundColor), Color.Parse("#1E88E5"));

    /// <summary>Цвет линии.</summary>
    public Color LineColor { get => GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }

    /// <summary>Толщина линии.</summary>
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    /// <summary>Цвет фона (Transparent — без фона).</summary>
    public Color BackgroundColor { get => GetValue(BackgroundColorProperty); set => SetValue(BackgroundColorProperty, value); }

    // ---------- Тайминги цикла (сек) ----------
    private const double SpinEnd = 1.8;     // конец вращения дуг
    private const double MergeEnd = 2.4;    // дуги сомкнулись в кольцо
    private const double DrawStart = 2.2;   // начало прорисовки «М»
    private const double DrawEnd = 3.1;     // «М» дорисована
    private const double FadeStart = 3.9;   // начало затухания
    private const double FadeEnd = 4.4;
    private const double Cycle = 4.5;       // длина цикла
    private const double FadeIn = 0.25;

    // «М» в нормированных координатах (0..1), y вниз — по мотиву скриншота
    private static readonly Point[] MShape =
    {
        new(0.00, 0.82), new(0.08, 0.66), new(0.15, 0.74),
        new(0.32, 0.04), new(0.52, 0.96), new(0.68, 0.28),
        new(0.80, 0.62), new(0.92, 0.52), new(1.00, 0.54),
    };

    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;

    static LoadingIndicatorV1()
    {
        AffectsRender<LoadingIndicatorV1>(LineColorProperty, LineThicknessProperty, BackgroundColorProperty);
    }

    public LoadingIndicatorV1()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Restart();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _clock.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 100 : availableSize.Height;
        double s = Math.Min(w, h);
        return new Size(s, s);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (BackgroundColor.A > 0)
            context.FillRectangle(new SolidColorBrush(BackgroundColor), bounds);

        double size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0) return;

        double t = _clock.Elapsed.TotalSeconds % Cycle;
        double thickness = LineThickness;
        double r = Math.Max(1, size * 0.5 - thickness * 0.5 - size * 0.12);
        var center = bounds.Center;

        double opacity = 1;
        if (t < FadeIn) opacity = t / FadeIn;
        else if (t > FadeStart) opacity = 1 - Math.Clamp((t - FadeStart) / (FadeEnd - FadeStart), 0, 1);
        if (t > FadeEnd) return;

        var pen = new Pen(new SolidColorBrush(LineColor), thickness,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushOpacity(opacity))
        {
            DrawRing(context, pen, center, r, t);
            if (t >= DrawStart)
            {
                double p = EaseInOut(Math.Clamp((t - DrawStart) / (DrawEnd - DrawStart), 0, 1));
                DrawM(context, pen, center, r, p);
            }
        }
    }

    // ---------- Кольцо из трёх дуг ----------
    private static void DrawRing(DrawingContext ctx, Pen pen, Point c, double r, double t)
    {
        double rot = 300 * Math.Min(t, MergeEnd);                 // град.
        double merge = EaseInOut(Math.Clamp((t - SpinEnd) / (MergeEnd - SpinEnd), 0, 1));
        double wob = Math.Sin(2 * Math.PI * t / 1.2);

        // Три дуги: длинная, средняя, короткая (как на gif)
        double[] spinStart = { rot, rot + 195 + 20 * wob, rot + 290 - 15 * wob };
        double[] spinSweep = { 140 + 50 * wob, 55, 22 };

        for (int i = 0; i < 3; i++)
        {
            double targetStart = rot + 120 * i;
            double delta = NormalizeDelta(targetStart - spinStart[i]);
            double start = spinStart[i] + delta * merge;
            double sweep = Lerp(spinSweep[i], 119.5, merge);
            DrawArc(ctx, pen, c, r, start, sweep);
        }
    }

    private static void DrawArc(DrawingContext ctx, Pen pen, Point c, double r, double startDeg, double sweepDeg)
    {
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));

        var g = new StreamGeometry();
        using (var sg = g.Open())
        {
            sg.BeginFigure(p0, false);
            sg.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            sg.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    // ---------- Пульсовая «М» ----------
    private static void DrawM(DrawingContext ctx, Pen pen, Point c, double r, double progress)
    {
        double w = r * 1.25, h = r * 1.10;
        var origin = new Point(c.X - w / 2, c.Y - h / 2);

        var pts = new List<Point>(MShape.Length);
        foreach (var p in MShape) pts.Add(new Point(origin.X + p.X * w, origin.Y + p.Y * h));

        double total = 0;
        for (int i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);

        double limit = total * progress, acc = 0;
        var visible = new List<Point> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            double seg = Dist(pts[i - 1], pts[i]);
            if (acc + seg <= limit) { visible.Add(pts[i]); acc += seg; continue; }
            double f = seg > 0 ? (limit - acc) / seg : 0;
            visible.Add(new Point(
                pts[i - 1].X + (pts[i].X - pts[i - 1].X) * f,
                pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * f));
            break;
        }
        if (visible.Count < 2 || progress <= 0) return;

        var g = new StreamGeometry();
        using (var sg = g.Open())
        {
            sg.BeginFigure(visible[0], false);
            for (int i = 1; i < visible.Count; i++) sg.LineTo(visible[i]);
            sg.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    // ---------- Утилиты ----------
    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double EaseInOut(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    private static double NormalizeDelta(double d)
    {
        d %= 360;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }
}
