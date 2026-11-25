using System;
using System.Collections.Generic;
using OpenCvSharp;
using TrtDetector.Models;
using TrtDetector.Postprocessing;

namespace EyetrackerGlasses.Mnn;

/// <summary>Запускает MNN на Mat и возвращает FrameResult. Run, RunBatch и Dispose сериализуются.</summary>
public sealed class MnnInferenceEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly MnnYoloRuntime _runtime;
    private readonly MnnYoloTensorConverter _converter;
    private bool _disposed;

    /// <summary>Коэффициенты нормализации по каналам. Меняйте их между вызовами.</summary>
    public float[] SubVals => _converter.SubVals;
    public float[] DivVals => _converter.DivVals;
    public int BatchSize => _converter.BatchSize;
    public float LastMaxScore { get; private set; }

    public MnnInferenceEngine(string modelPath, ModelSpec? spec = null, PostprocessOptions? post = null,
        PreprocessOptions? pre = null, IPackedLayout? packed = null, int threads = 4, bool preferAutoBackend = true)
    {
        _runtime = new MnnYoloRuntime(modelPath, threads, preferAutoBackend);
        try
        {
            _converter = new MnnYoloTensorConverter(_runtime, spec ?? new ModelSpec(), post, pre, packed);
        }
        catch
        {
            _runtime.Dispose();
            throw;
        }
    }

    public FrameResult Run(Mat frame, bool safeResize = true) => RunBatch(new[] { frame }, safeResize)[0];

    /// <summary>Делит список на запуски по BatchSize. Порядок кадров и результатов совпадает.</summary>
    public List<FrameResult> RunBatch(IReadOnlyList<Mat> frames, bool safeResize = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(frames);
            for (int i = 0; i < frames.Count; i++)
                ArgumentNullException.ThrowIfNull(frames[i]);

            LastMaxScore = 0;
            var results = new List<FrameResult>(frames.Count);
            for (int start = 0; start < frames.Count;)
            {
                int count = Math.Min(BatchSize, frames.Count - start);
                var chunk = new Mat[count];
                for (int i = 0; i < count; i++)
                    chunk[i] = frames[start + i];

                float[] input = _converter.PrepareInput(chunk, out var geometry, safeResize);
                var converted = _converter.DecodeOutput(_runtime.Infer(input), geometry);
                results.AddRange(converted);
                LastMaxScore = converted[^1].MaxScore;
                start += count;
            }
            return results;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _runtime.Dispose();
        }
    }
}
