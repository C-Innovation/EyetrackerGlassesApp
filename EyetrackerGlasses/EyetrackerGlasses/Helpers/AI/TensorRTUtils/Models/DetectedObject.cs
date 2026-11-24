using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TrtDetector.Models;

/// <summary>Итоговый объект в целых пикселях исходного изображения: (X, Y) — левый верхний угол.</summary>
public readonly record struct DetectedObject(int Id, float Conf, int X, int Y, int H, int W)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public Rect ToRect() => new(X, Y, W, H);
    public override string ToString() => $"id={Id} conf={Conf:F2} x={X} y={Y} h={H} w={W}";
}

public static class DetectionExtensions
{
    public static DetectedObject ToObject(this Detection d, int imgW, int imgH)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (imgW <= 0 || imgH <= 0) throw new ArgumentOutOfRangeException(nameof(imgW));
        if (!float.IsFinite(d.Confidence) || !float.IsFinite(d.Box.X) || !float.IsFinite(d.Box.Y)
            || !float.IsFinite(d.Box.Width) || !float.IsFinite(d.Box.Height)
            || d.Box.Width <= 0 || d.Box.Height <= 0)
            throw new ArgumentException("Detection must have a finite confidence and finite positive box", nameof(d));
        int x1 = (int)Math.Round(Math.Clamp((double)d.Box.X, 0, imgW - 1));
        int y1 = (int)Math.Round(Math.Clamp((double)d.Box.Y, 0, imgH - 1));
        int x2 = (int)Math.Round(Math.Clamp((double)d.Box.X + d.Box.Width, x1 + 1, imgW));
        int y2 = (int)Math.Round(Math.Clamp((double)d.Box.Y + d.Box.Height, y1 + 1, imgH));
        return new DetectedObject(d.ClassId, d.Confidence, x1, y1, y2 - y1, x2 - x1);
    }

    public static List<DetectedObject> ToObjects(this IEnumerable<Detection> dets, int imgW, int imgH)
        => dets.Select(d => d.ToObject(imgW, imgH)).OrderByDescending(o => o.Conf).ToList();
}
