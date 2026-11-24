using System;
using System.Collections.Generic;
using System.Globalization;
using OpenCvSharp;
using TrtDetector.Models;

namespace TrtDetector.Visualization;

/// <summary>
/// Отрисовка рамок. Mat imgWithBBox = BBoxDrawer.AddBBox(img, res);
/// Работает с любым числом классов (цвета генерируются по ClassId) и с кадрами другого размера,
/// чем исходный (рамки масштабируются по FrameResult.SourceWidth/Height).
/// </summary>
public static class BBoxDrawer
{
    /// <summary>Возвращает НОВЫЙ Mat (8-bit, BGR/BGRA) с рамками; исходный не меняется. Освобождать вызывающему.</summary>
    public static Mat AddBBox(Mat img, FrameResult res, BBoxDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(res);
        var o = options ?? new BBoxDrawOptions();
        o.Validate();
        Mat canvas = PrepareCanvas(img, o);
        try { DrawInPlace(canvas, res, o); }
        catch { canvas.Dispose(); throw; }
        return canvas;
    }

    /// <summary>Батч: imgs[i] + results[i]. Вызывающий освобождает все Mat в результате.</summary>
    public static List<Mat> AddBBox(IReadOnlyList<Mat> imgs, IReadOnlyList<FrameResult> results,
                                    BBoxDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(imgs);
        ArgumentNullException.ThrowIfNull(results);
        if (imgs.Count != results.Count) throw new ArgumentException("imgs.Count != results.Count");
        var list = new List<Mat>(imgs.Count);
        try { for (int i = 0; i < imgs.Count; i++) list.Add(AddBBox(imgs[i], results[i], options)); }
        catch { foreach (var m in list) m.Dispose(); throw; }
        return list;
    }

    /// <summary>Рисует прямо в img (8-bit; 1, 3 или 4 канала).</summary>
    public static void DrawInPlace(Mat img, FrameResult res, BBoxDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(res);
        var o = options ?? new BBoxDrawOptions();
        o.Validate();
        if (img.Empty()) throw new ArgumentException("Empty image");
        if (img.Depth() != 0 || img.Channels() is not (1 or 3 or 4))
            throw new NotSupportedException("DrawInPlace needs 8-bit image with 1, 3 or 4 channels");

        int ch = img.Channels();
        double sx = res.SourceWidth  > 0 ? (double)img.Width  / res.SourceWidth  : 1.0;
        double sy = res.SourceHeight > 0 ? (double)img.Height / res.SourceHeight : 1.0;

        foreach (var d in res.Detections)
        {
            if (d == null || !float.IsFinite(d.Confidence) || d.Confidence < o.MinConfidence
                || !float.IsFinite(d.Box.X) || !float.IsFinite(d.Box.Y)
                || !float.IsFinite(d.Box.Width) || !float.IsFinite(d.Box.Height)
                || d.Box.Width <= 0 || d.Box.Height <= 0) continue;

            double left = d.Box.X * sx, top = d.Box.Y * sy;
            double right = ((double)d.Box.X + d.Box.Width) * sx;
            double bottom = ((double)d.Box.Y + d.Box.Height) * sy;
            if (right <= 0 || bottom <= 0 || left >= img.Width || top >= img.Height) continue;

            Scalar color = ColorFor(d.ClassId, o);
            Scalar draw = Fit(color, ch);

            int x1 = (int)Math.Round(Math.Clamp(left, 0, img.Width - 1));
            int y1 = (int)Math.Round(Math.Clamp(top, 0, img.Height - 1));
            int x2 = (int)Math.Round(Math.Clamp(right, x1, img.Width - 1));
            int y2 = (int)Math.Round(Math.Clamp(bottom, y1, img.Height - 1));

            Cv2.Rectangle(img, new Point(x1, y1), new Point(x2, y2), draw, o.Thickness);

            if (!o.ShowLabel) continue;
            string text = Label(d, o);
            if (text.Length == 0) continue;

            var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, o.FontScale, o.FontThickness, out int baseline);
            int boxW = size.Width + 4, boxH = size.Height + baseline + 4;
            int ty = y1 - boxH >= 0 ? y1 - boxH : y1;                       // нет места сверху — внутрь рамки
            int tx = Math.Clamp(x1, 0, Math.Max(0, img.Width - boxW));

            Cv2.Rectangle(img, new Rect(tx, ty, Math.Min(boxW, img.Width - tx), Math.Min(boxH, img.Height - ty)),
                          draw, -1);
            Scalar txt = Fit(Luminance(color) > 150 ? Scalar.Black : Scalar.White, ch);
            Cv2.PutText(img, text, new Point(tx + 2, ty + 2 + size.Height),
                        HersheyFonts.HersheySimplex, o.FontScale, txt, o.FontThickness, LineTypes.AntiAlias);
        }
    }

    // ---------------- helpers ----------------

    private static Mat PrepareCanvas(Mat img, BBoxDrawOptions o)
    {
        if (img.Empty()) throw new ArgumentException("Empty image");

        var m8 = new Mat();
        try
        {
            switch (img.Depth())
            {
                case 0: img.CopyTo(m8); break;                                                     // 8U
                case 2: img.ConvertTo(m8, MatType.CV_8UC(img.Channels()), 255.0 / 65535.0); break;     // 16U
                case 5: img.ConvertTo(m8, MatType.CV_8UC(img.Channels()), 255.0); break;               // 32F в [0..1]
                default:
                    throw new NotSupportedException($"Mat depth {img.Depth()} is not supported (use 8U/16U/32F)");
            }

            int ch = m8.Channels();
            if (ch == 1 && o.ConvertGrayToBgr)
            {
                var bgr = new Mat();
                try { Cv2.CvtColor(m8, bgr, ColorConversionCodes.GRAY2BGR); }
                catch { bgr.Dispose(); throw; }
                m8.Dispose();
                return bgr;
            }
            if (ch is not (1 or 3 or 4))
                throw new NotSupportedException($"{ch}-channel image cannot be drawn on");
            return m8;
        }
        catch { m8.Dispose(); throw; }
    }

    private static string Label(Detection d, BBoxDrawOptions o)
    {
        var parts = new List<string>(2);
        if (o.ShowClassName) parts.Add(string.IsNullOrEmpty(d.ClassName) ? $"class{d.ClassId}"
            : d.ClassName[..Math.Min(d.ClassName.Length, 512)]);
        if (o.ShowConfidence) parts.Add(d.Confidence.ToString(o.ConfidenceFormat, CultureInfo.InvariantCulture));
        return string.Join(" ", parts);
    }

    private static Scalar ColorFor(int classId, BBoxDrawOptions o)
    {
        long id = Math.Abs((long)classId);
        if (o.Palette is { Length: > 0 }) return o.Palette[(int)(id % o.Palette.Length)];
        double hue = (id * 0.61803398875 % 1.0) * 360.0;       // золотое сечение: соседние классы различимы
        return HsvToBgr(hue, 0.85, 0.95);
    }

    private static Scalar HsvToBgr(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60.0) % 2 - 1)), m = v - c;
        (double r, double g, double b) = ((int)(h / 60.0) % 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new Scalar((b + m) * 255, (g + m) * 255, (r + m) * 255);
    }

    private static double Luminance(Scalar bgr) => 0.114 * bgr.Val0 + 0.587 * bgr.Val1 + 0.299 * bgr.Val2;

    private static Scalar Fit(Scalar c, int channels) => channels switch
    {
        1 => new Scalar(Luminance(c)),
        4 => new Scalar(c.Val0, c.Val1, c.Val2, 255),
        _ => c
    };
}
