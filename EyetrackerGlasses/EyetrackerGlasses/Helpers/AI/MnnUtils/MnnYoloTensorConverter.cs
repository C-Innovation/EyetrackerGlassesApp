using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using TrtDetector.Models;
using TrtDetector.Postprocessing;
using TrtDetector.Preprocessing;

namespace EyetrackerGlasses.Mnn;

/// <summary>Преобразует кадры и сырые выходы MNN с теми же правилами обработки, что и TensorRT.</summary>
public sealed class MnnYoloTensorConverter
{
    private readonly FramePreprocessor _preprocessor;
    private readonly DetectionPostprocessor _postprocessor;
    private readonly string _outputName;
    private readonly int[] _outputShape;
    private readonly int _channels;
    private readonly int _pixels;
    private readonly int _outputFrameSize;
    private readonly bool _inputIsNhwc;

    public int BatchSize { get; }
    /// <summary>Коэффициенты нормализации по каналам. Меняйте их между вызовами.</summary>
    public float[] SubVals { get; }
    public float[] DivVals { get; }

    public MnnYoloTensorConverter(MnnYoloRuntime runtime, ModelSpec spec,
        PostprocessOptions? post = null, PreprocessOptions? pre = null, IPackedLayout? packed = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(spec);
        int[] shape = runtime.InputShape;
        if (shape.Length != 4)
            throw new NotSupportedException("MNN YOLO requires an input with four dimensions (NCHW or NHWC).");

        _inputIsNhwc = runtime.InputIsNhwc;
        int[] expected = _inputIsNhwc
            ? new[] { shape[0], spec.Height, spec.Width, spec.Channels }
            : new[] { shape[0], spec.Channels, spec.Height, spec.Width };
        if (!shape.SequenceEqual(expected))
            throw new ArgumentException($"ModelSpec expects [{string.Join(", ", expected)}], " +
                $"but the MNN input is [{string.Join(", ", shape)}].", nameof(spec));
        BatchSize = shape[0];
        if (spec.FixedBatch is int batch && batch != BatchSize)
            throw new ArgumentException($"FixedBatch must match the MNN input batch ({BatchSize}).", nameof(spec));

        // MNN takes its batch from the loaded model rather than the TensorRT profile.
        var model = spec with { FixedBatch = BatchSize, Strides = (int[]?)spec.Strides?.Clone() };
        model.Validate();
        _channels = model.Channels;
        _pixels = checked(model.Height * model.Width);
        _outputName = model.OutputName;
        int[] outputItemDims = model.OutputItemDims;
        _outputShape = new[] { BatchSize, outputItemDims[0], outputItemDims[1] };
        _outputFrameSize = checked(outputItemDims[0] * outputItemDims[1]);
        SubVals = new float[_channels];
        DivVals = Enumerable.Repeat(1f, _channels).ToArray();
        _preprocessor = new FramePreprocessor(model, SubVals, DivVals, pre);
        _postprocessor = new DetectionPostprocessor(model, post, packed);
    }

    /// <summary>Готовит вход для Infer. Пустые кадры и свободные слоты batch заполняются нулями.</summary>
    public float[] PrepareInput(IReadOnlyList<Mat> frames, out FrameGeometry[] geometry, bool safeResize = true)
    {
        float[] planar = _preprocessor.Process(frames, safeResize, BatchSize, out geometry);
        if (!_inputIsNhwc || _channels == 1)
            return planar;

        var interleaved = new float[planar.Length];
        int frameSize = _pixels * _channels;
        for (int frame = 0; frame < frames.Count; frame++)
        {
            int start = frame * frameSize;
            for (int pixel = 0; pixel < _pixels; pixel++)
                for (int channel = 0; channel < _channels; channel++)
                    interleaved[start + pixel * _channels + channel] = planar[start + channel * _pixels + pixel];
        }
        return interleaved;
    }

    /// <summary>Находит выход по ModelSpec.OutputName и декодирует исходные кадры с NMS.</summary>
    public List<FrameResult> DecodeOutput(IReadOnlyList<MnnTensor> outputs, IReadOnlyList<FrameGeometry> geometry)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.Count > BatchSize)
            throw new ArgumentException("Frame geometry exceeds the MNN input batch.", nameof(geometry));

        MnnTensor? selected = null;
        foreach (var output in outputs)
        {
            if (!string.Equals(output.Name, _outputName, StringComparison.Ordinal))
                continue;
            if (selected != null)
                throw new InvalidOperationException($"MNN returned multiple outputs named '{_outputName}'.");
            selected = output;
        }
        if (selected == null)
            throw new InvalidOperationException($"MNN output '{_outputName}' was not found. Available outputs: " +
                string.Join(", ", outputs.Select(output => output.Name)));
        if (!selected.Shape.SequenceEqual(_outputShape)
            || selected.Values.Length != BatchSize * _outputFrameSize)
            throw new NotSupportedException($"MNN output '{_outputName}' has shape " +
                $"[{string.Join(", ", selected.Shape)}] and {selected.Values.Length} values. Expected a dense YOLO " +
                $"output [{string.Join(", ", _outputShape)}] with {BatchSize * _outputFrameSize} values.");

        var results = new List<FrameResult>(geometry.Count);
        for (int i = 0; i < geometry.Count; i++)
        {
            FrameGeometry frame = geometry[i];
            results.Add(frame == default
                ? new FrameResult { IsDone = true }
                : _postprocessor.Parse(selected.Values.AsSpan(i * _outputFrameSize, _outputFrameSize), frame));
        }
        return results;
    }
}
