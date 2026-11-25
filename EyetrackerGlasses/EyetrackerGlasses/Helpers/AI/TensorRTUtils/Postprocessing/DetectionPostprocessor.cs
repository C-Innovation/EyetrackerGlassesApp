using OpenCvSharp;
using OpenCvSharp.Dnn;
using System;
using System.Collections.Generic;
using System.Linq;
using TrtDetector.Models;

namespace TrtDetector.Postprocessing;

/// <summary>
/// Универсальный парсер детекторов: F = 4 + [obj] + NumClasses, раскладка [F,N] или [N,F], NMS.
/// score = max(class) (или obj * max(class)); класс = argmax (при равенстве — последний, как в C++).
/// </summary>
public sealed class DetectionPostprocessor
{
    private readonly int _n, _nc;
    private readonly int _outputSize;
    private readonly bool _hasObj;
    private readonly int _aStride, _fStride;     // шаги по якорю и по каналу в одномерном массиве
    private readonly PostprocessOptions _o;
    private readonly IPackedLayout? _packed;
    private readonly int _tileCount;

    public DetectionPostprocessor(ModelSpec s, PostprocessOptions? o = null, IPackedLayout? packed = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Validate();
        _n = s.AnchorCount;
        _nc = s.NumClasses;
        _hasObj = s.HasObjectness;
        if (s.Layout == OutputLayout.ChannelsFirst) { _aStride = 1; _fStride = _n; }
        else { _aStride = s.Outputs; _fStride = 1; }
        _outputSize = checked(s.Outputs * _n);
        _o = (o ?? new PostprocessOptions()).Snapshot();
        if (packed != null && (packed.TileCount is < 1 or > 100000
            || !float.IsFinite(packed.Stride) || packed.Stride <= 0))
            throw new ArgumentException("Packed layout requires positive tile count and finite positive stride", nameof(packed));
        _packed = packed;
        _tileCount = packed?.TileCount ?? 1;
    }

    /// <summary>o — выход одного кадра (Outputs * AnchorCount значений).</summary>
    public FrameResult Parse(ReadOnlySpan<float> o, in FrameGeometry g)
    {
        if (o.Length != _outputSize) throw new ArgumentException("Unexpected output tensor length", nameof(o));
        if (g.SrcW <= 0 || g.SrcH <= 0 || !float.IsFinite(g.XScale) || !float.IsFinite(g.YScale)
            || g.XScale <= 0 || g.YScale <= 0)
            throw new ArgumentException("Invalid frame geometry", nameof(g));
        int clsBase = _hasObj ? 5 : 4;
        var classIds = new List<int>();
        var tileIds = new List<int>();
        var confs = new List<float>();
        var boxes = new List<Rect2d>();
        float maxScore = 0f;

        for (int a = 0; a < _n; a++)
        {
            int ab = a * _aStride, fs = _fStride;
            float cx = o[ab], cy = o[ab + fs], w = o[ab + 2 * fs], h = o[ab + 3 * fs];
            if (!float.IsFinite(cx) || !float.IsFinite(cy) || !float.IsFinite(w) || !float.IsFinite(h)
                || w <= 0 || h <= 0) continue;

            float best = float.NegativeInfinity; int cls = 0;
            for (int k = 0; k < _nc; k++)
            {
                float s = o[ab + (clsBase + k) * fs];
                if (s >= best) { best = s; cls = k; }
            }
            float score = _hasObj ? o[ab + 4 * fs] * best : best;

            if (!float.IsFinite(score)) continue;
            if (score > maxScore) maxScore = score;           // до отсечки по порогу
            if (score <= _o.ScoreThreshold) continue;

            var box = new Rect2f((cx - 0.5f * w) * g.XScale, (cy - 0.5f * h) * g.YScale,
                                 w * g.XScale, h * g.YScale);
            if (!IsFinite(box)) continue;

            int tile = 0;
            if (_packed != null)
            {
                if (!_packed.ClipToTile(ref box, out tile) || tile < 0 || tile >= _tileCount) continue;
            }
            // Clip both edges, not just origin/size; a partly negative box must shrink.
            if (!IsFinite(box)) continue;
            float x = Math.Clamp(box.X, 0, g.SrcW), y = Math.Clamp(box.Y, 0, g.SrcH);
            float right = (float)Math.Clamp((double)box.X + box.Width, 0, g.SrcW);
            float bottom = (float)Math.Clamp((double)box.Y + box.Height, 0, g.SrcH);
            if (right <= x || bottom <= y) continue;
            box = new Rect2f(x, y, right - x, bottom - y);

            confs.Add(score);
            classIds.Add(cls);
            tileIds.Add(tile);
            boxes.Add(new Rect2d(box.X, box.Y, box.Width, box.Height));
        }

        int[] keep = Nms(boxes, confs, classIds, tileIds);

        var res = new FrameResult { MaxScore = maxScore, SourceWidth = g.SrcW, SourceHeight = g.SrcH };
        foreach (int idx in keep)
        {
            var b = boxes[idx];
            var box = new Rect2f((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);
            int cls = classIds[idx];
            var det = new Detection(cls, NameOf(cls), confs[idx], box);
            res.Detections.Add(det);
            if (det.Confidence >= _o.ItemsMinConf)
                res.Items.Add(new InferenceItem(box.X, box.Y, box.Width, box.Height, det.Confidence, cls));
        }
        while (res.Items.Count < _o.PadItemsTo)
            res.Items.Add(new InferenceItem(-1f, -1f, -1f, -1f, 0f, 0));

        res.IsDone = true;
        return res;
    }

    private string NameOf(int cls) =>
        _o.ClassNames != null && cls >= 0 && cls < _o.ClassNames.Length ? _o.ClassNames[cls] : $"class{cls}";

    private int[] Nms(List<Rect2d> boxes, List<float> confs, List<int> classIds, List<int> tileIds)
    {
        if (boxes.Count == 0) return Array.Empty<int>();
        if (boxes.Count > _o.MaxNmsCandidates)
        {
            int[] top = Enumerable.Range(0, boxes.Count).OrderByDescending(i => confs[i])
                .Take(_o.MaxNmsCandidates).ToArray();
            boxes = top.Select(i => boxes[i]).ToList();
            confs = top.Select(i => confs[i]).ToList();
            classIds = top.Select(i => classIds[i]).ToList();
            tileIds = top.Select(i => tileIds[i]).ToList();
            // Return original indices to the caller of Nms.
            return Nms(boxes, confs, classIds, tileIds).Select(i => top[i]).ToArray();
        }
        float st = _o.ScoreThreshold, nt = _o.NmsThreshold;

        if (_packed != null)   // NMS отдельно по каждому тайлу
        {
            return NmsGrouped(boxes, confs, st, nt,
                i => ((long)tileIds[i], _o.ClassAgnosticNms ? 0 : classIds[i]));
        }
        if (!_o.ClassAgnosticNms)   // NMS отдельно по классам, затем сортировка по conf
        {
            var r = NmsGrouped(boxes, confs, st, nt, i => (0L, classIds[i]));
            return r.OrderByDescending(i => confs[i]).ToArray();
        }
        CvDnn.NMSBoxes(boxes, confs, st, nt, out int[] keep);   // общий NMS на все классы (как в C++)
        return keep;
    }

    private static int[] NmsGrouped(List<Rect2d> boxes, List<float> confs, float st, float nt,
                                    Func<int, (long Tile, int Class)> groupOf)
    {
        var groups = new Dictionary<(long, int), List<int>>();
        for (int i = 0; i < boxes.Count; i++)
        {
            var gi = groupOf(i);
            if (!groups.TryGetValue(gi, out var group)) groups[gi] = group = new List<int>();
            group.Add(i);
        }

        var keep = new List<int>();
        foreach (var idxs in groups.Values)
        {
            var tb = idxs.Select(i => boxes[i]).ToList();
            var tc = idxs.Select(i => confs[i]).ToList();
            CvDnn.NMSBoxes(tb, tc, st, nt, out int[] k);
            foreach (int j in k) keep.Add(idxs[j]);
        }
        return keep.ToArray();
    }

    private static bool IsFinite(Rect2f box) => float.IsFinite(box.X) && float.IsFinite(box.Y)
        && float.IsFinite(box.Width) && float.IsFinite(box.Height) && box.Width > 0 && box.Height > 0;
}
