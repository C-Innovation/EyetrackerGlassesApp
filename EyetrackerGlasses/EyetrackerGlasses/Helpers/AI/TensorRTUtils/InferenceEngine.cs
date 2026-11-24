using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using TrtDetector.Models;
using TrtDetector.Postprocessing;
using TrtDetector.Preprocessing;

namespace TrtDetector.Inference;

/// <summary>
/// Универсальный движок: препроцесс -> TensorRT -> парсер + NMS.
/// Любое число каналов/классов, любой размер входа, любой batch (в т.ч. статический через FixedBatch).
/// Run/RunBatch/Dispose сериализуются. Настройки нормализации меняйте только между запусками.
/// </summary>
public sealed class InferenceEngine : IDisposable
{
    private readonly ModelSpec _spec;
    private readonly FramePreprocessor _pre;
    private readonly DetectionPostprocessor _post;
    private readonly TrtRunner _runner;
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Вычитаемое по каналам (длина = Channels). Можно менять после создания.</summary>
    public float[] SubVals { get; }

    /// <summary>Делитель по каналам (длина = Channels). Можно менять после создания.</summary>
    public float[] DivVals { get; }

    /// <summary>lastMaxScore: максимум score последнего обработанного кадра до отсечки по порогу.</summary>
    public float LastMaxScore { get; private set; }

    public InferenceEngine(string enginePath, ModelSpec? spec = null,
                           PostprocessOptions? post = null, PreprocessOptions? pre = null,
                           IPackedLayout? packed = null)
    {
        var source = spec ?? new ModelSpec();
        _spec = source with { Strides = (int[]?)source.Strides?.Clone() };
        _spec.Validate();
        SubVals = new float[_spec.Channels];
        DivVals = Enumerable.Repeat(1f, _spec.Channels).ToArray();

        _pre  = new FramePreprocessor(_spec, SubVals, DivVals, pre);
        _post = new DetectionPostprocessor(_spec, post, packed);
        _runner = new TrtRunner(enginePath,
            _spec.InputName, _spec.InputItemDims,
            _spec.OutputName, _spec.OutputItemDims);
    }

    public FrameResult Run(Mat frame, bool safeResize = true)
        => RunBatch(new[] { frame }, safeResize)[0];

    /// <summary>Любое число кадров: делится на запуски по EffectiveMaxBatch.</summary>
    public List<FrameResult> RunBatch(IReadOnlyList<Mat> frames, bool safeResize = true)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RunBatchCore(frames, safeResize);
        }
    }

    private List<FrameResult> RunBatchCore(IReadOnlyList<Mat> frames, bool safeResize)
    {
        ArgumentNullException.ThrowIfNull(frames);
        for (int i = 0; i < frames.Count; i++) ArgumentNullException.ThrowIfNull(frames[i]);
        LastMaxScore = 0;
        var all = new List<FrameResult>(frames.Count);
        int maxB = _spec.EffectiveMaxBatch;
        int frameStride = checked(_spec.Outputs * _spec.AnchorCount);

        for (int start = 0; start < frames.Count;)
        {
            int n = Math.Min(maxB, frames.Count - start);
            int runBatch = _spec.FixedBatch ?? n;       // статический engine: добиваем нулями

            var chunk = new List<Mat>(n);
            for (int i = 0; i < n; i++) chunk.Add(frames[start + i]);

            float[] input = _pre.Process(chunk, safeResize, runBatch, out var geo);
            float[] output = _runner.Run(input, runBatch);

            for (int i = 0; i < n; i++)
            {
                if (chunk[i].Empty())
                {
                    LastMaxScore = 0;
                    all.Add(new FrameResult { IsDone = true });
                    continue;
                }
                var r = _post.Parse(new ReadOnlySpan<float>(output, i * frameStride, frameStride), geo[i]);
                LastMaxScore = r.MaxScore;
                all.Add(r);
            }
            start += n;
        }
        return all;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _runner.Dispose();
        }
    }
}
