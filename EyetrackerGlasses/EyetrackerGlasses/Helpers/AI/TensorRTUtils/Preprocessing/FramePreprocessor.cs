using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenCvSharp;
using TrtDetector.Models;

namespace TrtDetector.Preprocessing;

/// <summary>
/// Универсальный препроцесс (любое число каналов C, любой размер, любой batch).
/// Логика C++: letterbox с паддингом справа/снизу (safeResize) или растяжение, нулевой паддинг,
/// нормализация (x*scale - sub)/div. Для C==1 нормализация до letterbox (паддинг = 0),
/// для C>1 — после (как в C++, паддинг получает -sub/div).
/// </summary>
public sealed class FramePreprocessor
{
    private const int Depth8U = 0, Depth16U = 2, Depth32F = 5;

    private readonly int _c, _h, _w;
    private readonly int _maxBatch;
    private readonly float[] _sub, _div;
    private readonly PreprocessOptions _o;

    public FramePreprocessor(ModelSpec s, float[] sub, float[] div, PreprocessOptions? o = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(sub);
        ArgumentNullException.ThrowIfNull(div);
        s.Validate();
        if (sub.Length < s.Channels || div.Length < s.Channels)
            throw new ArgumentException("SubVals/DivVals must have at least Channels elements");
        var options = o ?? new PreprocessOptions();
        if (!double.IsFinite(options.Scale8) || !double.IsFinite(options.Scale16))
            throw new ArgumentException("Input scales must be finite", nameof(o));
        _o = new PreprocessOptions { SwapRB = options.SwapRB, Scale8 = options.Scale8, Scale16 = options.Scale16 };
        (_c, _h, _w, _sub, _div, _maxBatch) = (s.Channels, s.Height, s.Width, sub, div, s.EffectiveMaxBatch);
    }

    /// <summary>
    /// float[batch*C*H*W] в NCHW. batch >= frames.Count; лишние слоты и пустые кадры остаются нулями.
    /// </summary>
    public float[] Process(IReadOnlyList<Mat> frames, bool safeResize, int batch, out FrameGeometry[] geo)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (batch < 1 || batch < frames.Count || batch > _maxBatch)
            throw new ArgumentOutOfRangeException(nameof(batch));
        // These arrays intentionally remain editable by the caller between runs.
        for (int c = 0; c < _c; c++)
            if (!float.IsFinite(_sub[c]) || !float.IsFinite(_div[c]) || _div[c] == 0)
                throw new ArgumentException("Normalization values must be finite and divisors nonzero");
        for (int i = 0; i < frames.Count; i++)
            ArgumentNullException.ThrowIfNull(frames[i]);
        int frameSize = checked(_c * _h * _w);
        var data = new float[checked(batch * frameSize)];
        geo = new FrameGeometry[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            if (!frames[i].Empty())
                geo[i] = ProcessOne(frames[i], data, i * frameSize, safeResize);
        return data;
    }

    private FrameGeometry ProcessOne(Mat src, float[] dst, int offset, bool safeResize)
    {
        using var cvt = new Mat();
        Mat s = EnsureChannels(src, cvt);
        double k = ScaleFor(s);
        using var flt = new Mat();

        if (_c == 1)
        {
            s.ConvertTo(flt, MatType.CV_32FC1, k / _div[0], -_sub[0] / _div[0]);
            using var plane = Letterbox(flt, safeResize, flt.Type(), out var g1);
            Marshal.Copy(plane.Data, dst, offset, _h * _w);
            return g1;
        }

        s.ConvertTo(flt, MatType.CV_32FC(_c), k);
        using var canvas = Letterbox(flt, safeResize, flt.Type(), out var geo);
        Cv2.Split(canvas, out Mat[] planes);
        try
        {
            for (int c = 0; c < _c; c++)
            {
                int mc = (_o.SwapRB && _c == 3) ? 2 - c : c;      // канал модели
                var p = planes[c];
                if (_sub[mc] != 0f || _div[mc] != 1f)
                    p.ConvertTo(p, MatType.CV_32FC1, 1.0 / _div[mc], -_sub[mc] / _div[mc]);
                Marshal.Copy(p.Data, dst, offset + mc * _h * _w, _h * _w);
            }
        }
        finally { foreach (var p in planes) p.Dispose(); }
        return geo;
    }

    private Mat EnsureChannels(Mat src, Mat tmp)
    {
        int sc = src.Channels();
        if (sc == _c) return src;
        ColorConversionCodes? code = (sc, _c) switch
        {
            (1, 3) => ColorConversionCodes.GRAY2BGR,
            (4, 3) => ColorConversionCodes.BGRA2BGR,
            (3, 1) => ColorConversionCodes.BGR2GRAY,
            (4, 1) => ColorConversionCodes.BGRA2GRAY,
            _ => null
        };
        if (code is null)
            throw new NotSupportedException($"Cannot convert {sc}-channel image to {_c}-channel model input");
        Cv2.CvtColor(src, tmp, code.Value);
        return tmp;
    }

    private double ScaleFor(Mat m) => m.Depth() switch
    {
        Depth8U => _o.Scale8,
        Depth16U => _o.Scale16,
        Depth32F => 1.0,
        _ => throw new NotSupportedException($"Mat depth {m.Depth()} is not supported (use 8U/16U/32F)")
    };

    private Mat Letterbox(Mat flt, bool safeResize, MatType type, out FrameGeometry geo)
    {
        var canvas = new Mat(_h, _w, type, Scalar.All(0));
        try
        {
            if (safeResize)
            {
                // Pad right/bottom; preserve aspect ratio before integer rounding.
                float r = Math.Min(_w / (float)flt.Cols, _h / (float)flt.Rows);
                int nw = Math.Clamp((int)Math.Round((double)(flt.Cols * r)), 1, _w);
                int nh = Math.Clamp((int)Math.Round((double)(flt.Rows * r)), 1, _h);
                using var roi = new Mat(canvas, new Rect(0, 0, nw, nh));
                Cv2.Resize(flt, roi, new Size(nw, nh));
                // Account for integer rounding of the resized ROI on each axis.
                geo = new FrameGeometry(flt.Cols, flt.Rows, flt.Cols / (float)nw, flt.Rows / (float)nh);
            }
            else
            {
                if (flt.Cols == _w && flt.Rows == _h) flt.CopyTo(canvas);
                else Cv2.Resize(flt, canvas, new Size(_w, _h));
                geo = new FrameGeometry(flt.Cols, flt.Rows, flt.Cols / (float)_w, flt.Rows / (float)_h);
            }
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }
}
