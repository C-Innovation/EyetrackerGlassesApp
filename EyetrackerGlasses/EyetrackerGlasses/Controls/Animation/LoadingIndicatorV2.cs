using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EyetrackerGlasses;

/// <summary>
/// Анимация загрузки: сжимающаяся/растущая дуга -> кольцо -> рукописная «m» -> «m» стирается -> кольцо -> повтор.
/// Тайминги и форма сняты с исходного gif (122 кадра по 30 мс, кольцо R = 1/3 стороны контрола).
/// </summary>
public class LoadingIndicatorV2 : Control
{
    // ---------- Настройки ----------
    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<LoadingIndicatorV2, Color>(nameof(LineColor), Colors.White);

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<LoadingIndicatorV2, double>(nameof(LineThickness), 8.0);

    public static readonly StyledProperty<Color> BackgroundColorProperty =
        AvaloniaProperty.Register<LoadingIndicatorV2, Color>(nameof(BackgroundColor), Color.Parse("#1991EC"));

    /// <summary>Цвет линии.</summary>
    public Color LineColor { get => GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }

    /// <summary>Толщина линии.</summary>
    public double LineThickness { get => GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

    /// <summary>Цвет фона (Transparent — без фона).</summary>
    public Color BackgroundColor { get => GetValue(BackgroundColorProperty); set => SetValue(BackgroundColorProperty, value); }

    // ---------- Тайминг ----------
    private const double FrameSec = 0.03;
    private const double TotalFrames = 122;
    private const double D2R = Math.PI / 180.0;

    // Угол кольца: a = -14° - phi (phi растёт против часовой стрелки, как вращается дуга в gif)
    private const double RingEndPhi = 768;   // голова доходит до верха справа после двух кругов

    // Голова на кольце, кадры 0..50 (phi = 346 - угол)
    private static readonly double[] HeadRingF = { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50 };
    private static readonly double[] HeadRingPhi =
    {
        406, 410, 416, 420, 422, 426, 430, 434, 436, 440, 442, 446, 450, 454, 462, 470, 480, 492, 506, 526, 550, 586, 644, 686, 732, 768
    };

    // Хвост на кольце, кадры 0..62 (phi)
    private static readonly double[] TailRingF =
        { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62 };
    private static readonly double[] TailRingPhi =
    {
        250, 268, 286, 304, 322, 338, 352, 366, 376, 384, 388, 390, 394, 398, 402, 408, 408, 412, 420, 420, 424, 434,
        448, 464, 488, 564, 608, 628, 634, 648, 718, 758
    };

    // Выход из M на кольцо: голова (phi) по кадрам 92..122
    private static readonly double[] HeadExitF = { 92, 96, 98, 100, 102, 104, 106, 108, 110, 112, 114, 116, 118, 120, 122 };
    private static readonly double[] HeadExitPhi = { 10, 26, 36, 50, 66, 86, 110, 142, 184, 240, 334, 378, 392, 400, 406 };

    // Хвост после ухода M: кадры 114..122
    private static readonly double[] TailExitF = { 114, 116, 118, 120, 122 };
    private static readonly double[] TailExitPhi = { 0, 92, 150, 222, 250 };

    // ---------- Геометрия единого штриха (единицы = радиус кольца) ----------
    // Пульсовая «M» со скриншота, нормированный бокс 0..1
    private static readonly (double X, double Y)[] Ecg =
    {
        (0.00, 0.82), (0.08, 0.66), (0.15, 0.74), (0.32, 0.04), (0.52, 0.96),
        (0.68, 0.28), (0.80, 0.62), (0.92, 0.52), (1.00, 0.54),
    };
    private const double MW = 1.5, MH = 1.3;

    private static readonly Point[] P;     // вершины штриха (последняя = начало M, замыкание)
    private static readonly double[] Cum;  // накопленная длина
    private static readonly double UTot, LM, UR0, URingEnd, USpiralEnd;

    static LoadingIndicatorV2()
    {
        AffectsRender<LoadingIndicatorV2>(LineColorProperty, LineThicknessProperty, BackgroundColorProperty);

        // --- M ---
        var m = new List<Point>();
        foreach (var (x, y) in Ecg) m.Add(new Point((x - 0.5) * MW, (y - 0.5) * MH));
        for (int it = 0; it < 2; it++) m = Chaikin(m);
        var m0 = m[0]; var m1 = m[^1];

        var all = new List<Point>(m);
        int mCount = m.Count;

        // --- переход с конца M на кольцо (справа, угол -14°) ---
        var r0 = RingPt(0, 1);
        var c1 = new Point(m1.X + 0.2, m1.Y);
        var c2 = new Point(r0.X + 0.07, r0.Y + 0.3);
        for (int i = 1; i <= 24; i++)
        {
            double t = i / 24.0, u = 1 - t;
            all.Add(new Point(
                u * u * u * m1.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * r0.X,
                u * u * u * m1.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * r0.Y));
        }
        int ur0Index = all.Count - 1;

        // --- кольцо: phi 0..768 ---
        for (double phi = 2; phi <= RingEndPhi; phi += 2) all.Add(RingPt(phi, 1));
        int ringEndIndex = all.Count - 1;

        // --- спираль внутрь до начала M ---
        double a0 = Math.Atan2(m0.Y, m0.X) / D2R;                 // угол начала M
        double phiEnd = -14 - a0;
        while (phiEnd < RingEndPhi + 60) phiEnd += 360;
        double rEnd = Math.Sqrt(m0.X * m0.X + m0.Y * m0.Y);
        for (double phi = RingEndPhi + 2; phi < phiEnd; phi += 2)
        {
            double k = (phi - RingEndPhi) / (phiEnd - RingEndPhi);
            all.Add(RingPt(phi, 1 - (1 - rEnd) * k));
        }
        all.Add(m0);                                              // замыкание
        int spiralEndIndex = all.Count - 1;

        P = all.ToArray();
        Cum = new double[P.Length];
        for (int i = 1; i < P.Length; i++) Cum[i] = Cum[i - 1] + Dist(P[i - 1], P[i]);
        UTot = Cum[^1];
        LM = Cum[mCount - 1];
        UR0 = Cum[ur0Index];
        URingEnd = Cum[ringEndIndex];
        USpiralEnd = Cum[spiralEndIndex];
    }

    private static Point RingPt(double phiDeg, double r)
    {
        double a = (-14 - phiDeg) * D2R;
        return new Point(r * Math.Cos(a), r * Math.Sin(a));
    }

    private static List<Point> Chaikin(List<Point> p)
    {
        var o = new List<Point> { p[0] };
        for (int i = 0; i < p.Count - 1; i++)
        {
            var a = p[i]; var b = p[i + 1];
            o.Add(new Point(0.75 * a.X + 0.25 * b.X, 0.75 * a.Y + 0.25 * b.Y));
            o.Add(new Point(0.25 * a.X + 0.75 * b.X, 0.25 * a.Y + 0.75 * b.Y));
        }
        o.Add(p[^1]);
        return o;
    }

    // ---------- Окно (хвост, голова) по кадру ----------
    private static (double Tail, double Head) WindowAt(double f)
    {
        double head, tail;

        if (f <= 50) head = UR0 + Interp(f, HeadRingF, HeadRingPhi) * D2R;
        else if (f <= 51) head = Lerp(URingEnd, USpiralEnd, f - 50);                      // спираль внутрь
        else if (f <= 60) head = UTot + LM * Ease((f - 51) / 9);                          // рисуется M
        else if (f <= 86) head = UTot + LM;                                               // M держится
        else if (f <= 92) head = Lerp(UTot + LM, UTot + UR0 + 10 * D2R, Ease((f - 86) / 6)); // выход из M по переходу
        else head = UTot + UR0 + Interp(f, HeadExitF, HeadExitPhi) * D2R;                 // уход на кольцо

        if (f <= 62) tail = UR0 + Interp(f, TailRingF, TailRingPhi) * D2R;
        else if (f <= 66)
        {
            double from = UR0 + 758 * D2R;
            tail = Lerp(from, UTot, Ease((f - 62) / 4));
        }
        else if (f < 92) tail = UTot;
        else if (f <= 112) tail = UTot + LM * Ease((f - 92) / 20);                        // M стирается
        else if (f <= 114) tail = UTot + LM + (UR0 - LM) * ((f - 112) / 2);
        else tail = UTot + UR0 + Interp(f, TailExitF, TailExitPhi) * D2R;

        return (tail, head);
    }

    private static List<Point> Slice(double t, double h)
    {
        var res = new List<Point>();
        if (h - t < 1e-3) return res;
        double shift = Math.Floor(t / UTot) * UTot;
        t -= shift; h -= shift;

        res.Add(PointAt(t));
        for (int k = 0; k < 3; k++)
        {
            for (int i = 0; i < P.Length; i++)
            {
                double u = Cum[i] + k * UTot;
                if (u <= t) continue;
                if (u >= h) { res.Add(PointAt(h)); return res; }
                res.Add(P[i]);
            }
        }
        res.Add(PointAt(h));
        return res;
    }

    private static Point PointAt(double u)
    {
        double v = u - Math.Floor(u / UTot) * UTot;
        int i = Array.BinarySearch(Cum, v);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, P.Length - 2);
        double seg = Cum[i + 1] - Cum[i];
        double t = seg > 1e-9 ? (v - Cum[i]) / seg : 0;
        return new Point(P[i].X + (P[i + 1].X - P[i].X) * t, P[i].Y + (P[i + 1].Y - P[i].Y) * t);
    }

    // ---------- Контрол ----------
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;

    public LoadingIndicatorV2()
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

        double f = (_clock.Elapsed.TotalSeconds / FrameSec) % TotalFrames;
        double r = size / 3.0;
        var c = bounds.Center;

        var (tail, head) = WindowAt(f);
        var pts = Slice(tail, head);
        if (pts.Count < 2) return;

        var pen = new Pen(new SolidColorBrush(LineColor), LineThickness,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        var g = new StreamGeometry();
        using (var sg = g.Open())
        {
            sg.BeginFigure(new Point(c.X + pts[0].X * r, c.Y + pts[0].Y * r), false);
            for (int i = 1; i < pts.Count; i++)
                sg.LineTo(new Point(c.X + pts[i].X * r, c.Y + pts[i].Y * r));
            sg.EndFigure(false);
        }
        context.DrawGeometry(null, pen, g);
    }

    // ---------- Утилиты ----------
    private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    }

    private static double Interp(double x, double[] xs, double[] ys)
    {
        if (x <= xs[0]) return ys[0];
        for (int i = 1; i < xs.Length; i++)
        {
            if (x <= xs[i])
            {
                double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
            }
        }
        return ys[^1];
    }
}
