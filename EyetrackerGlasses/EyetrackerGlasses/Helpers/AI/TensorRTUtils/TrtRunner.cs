using JYPPX.CudaSharp;
using JYPPX.TensorRtSharp;
using JYPPX.TensorRtSharp.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TrtDetector.Inference;

/// <summary>
/// Универсальный раннер: один входной и один выходной FP32-тензор, переменный batch.
/// Привязки пересоздаются только при смене размера batch. Run/Dispose сериализуются.
/// Загружать можно только engine из доверенного источника: engine содержит исполняемый код.
/// </summary>
public sealed class TrtRunner : IDisposable
{
    private readonly object _sync = new();
    private bool _disposed;
    private readonly TensorRtLogger _logger;
    private readonly TensorRtRuntime _runtime;
    private readonly TensorRtEngine _engine;
    private readonly TensorRtExecutionContext _context;
    private readonly CudaStream _stream;

    private readonly string _inName, _outName;
    private readonly int[] _inItemDims;      // например {C, H, W}
    private readonly int[] _outItemDims;     // например {F, N}
    private readonly int _inItemSize, _outItemSize;

    private TensorRtInferenceBindings? _bindings;
    private TensorRtDims _inShape = default!;
    private int _batch = -1;

    public TrtRunner(string enginePath, string inName, int[] inItemDims,
                     string outName, int[] outItemDims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enginePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outName);
        if (inName == outName) throw new ArgumentException("Input/output names must differ");
        _inItemSize = ElementCount(inItemDims);
        _outItemSize = ElementCount(outItemDims);
        (_inName, _inItemDims, _outName, _outItemDims) =
            (inName, (int[])inItemDims.Clone(), outName, (int[])outItemDims.Clone());
        string path = Path.GetFullPath(enginePath);
        if (!File.Exists(path)) throw new FileNotFoundException("TensorRT engine not found", path);

        TensorRtLogger? logger = null;
        TensorRtRuntime? runtime = null;
        TensorRtEngine? engine = null;
        TensorRtExecutionContext? context = null;
        CudaStream? stream = null;
        try
        {
            logger = new TensorRtLogger(JYPPX.TensorRtSharp.Shared.Interop.TensorRtApiLine.TensorRt11);
            runtime = new TensorRtRuntime(logger);
            engine = runtime.DeserializeFromFile(path);
            if (engine.IOTensorCount != 2)
                throw new NotSupportedException("Expected exactly one input and one output tensor");
            ValidateTensor(engine, inName, TensorRtIOMode.Input, _inItemDims);
            ValidateTensor(engine, outName, TensorRtIOMode.Output, _outItemDims);
            context = engine.CreateExecutionContext();
            stream = new CudaStream();
            (_logger, _runtime, _engine, _context, _stream) = (logger, runtime, engine, context, stream);
        }
        catch (Exception creationError)
        {
            try { DisposeResources(stream, context, engine, runtime, logger); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("TensorRT initialization and cleanup failed", creationError, cleanupError);
            }
            throw;
        }
    }

    /// <summary>input: n * prod(inItemDims) float; возвращает n * prod(outItemDims) float.</summary>
    public float[] Run(float[] input, int batch)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(input);
            if (batch < 1) throw new ArgumentOutOfRangeException(nameof(batch));
            int count = checked(batch * _inItemSize);
            _ = checked(count * sizeof(float));
            _ = checked(batch * _outItemSize * sizeof(float));
            if (input.Length != count) throw new ArgumentException("Input length does not match batch/shape", nameof(input));
            EnsureBatch(batch);
            _bindings!.CopyInputFromHost(_inName, input, _inShape);
            _bindings.BindAll();
            _bindings.EnqueueAsync(_stream, synchronize: true, runShapeInference: false);
            return _bindings.ReadOutputSingles(_outName, checked(batch * _outItemSize));
        }
    }

    private void EnsureBatch(int n)
    {
        if (n == _batch) return;
        // A failed reconfiguration must not leave a disposed cached binding behind.
        var previous = _bindings;
        _bindings = null;
        _batch = -1;
        previous?.Dispose();
        var shape = new TensorRtDims(Prepend(n, _inItemDims));
        var bindings = new TensorRtInferenceBindings(_engine, _context, 0);
        try
        {
            bindings.SetInputShape(_inName, shape);
            if (_context.InferShapes() != 0)
                throw new NotSupportedException("TensorRT could not resolve all tensor shapes");
            // Never size native output memory from ModelSpec alone: a larger real output
            // could otherwise be written beyond the allocated GPU buffer.
            if (!_context.GetTensorShape(_inName).Values.SequenceEqual(shape.Values)
                || !_context.GetTensorShape(_outName).Values.SequenceEqual(Prepend(n, _outItemDims)))
                throw new ArgumentException("Runtime tensor shapes do not match the configured model");
            bindings.AllocateDeviceBuffer(_outName, _context.GetTensorShape(_outName));
            (_bindings, _inShape, _batch) = (bindings, shape, n);
        }
        catch { bindings.Dispose(); throw; }
    }

    private static int[] Prepend(int n, int[] dims) => new[] { n }.Concat(dims).ToArray();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeResources(_bindings, _stream, _context, _engine, _runtime, _logger);
        }
    }

    private static void DisposeResources(params IDisposable?[] resources)
    {
        List<Exception>? errors = null;
        foreach (var resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception error) { (errors ??= new List<Exception>()).Add(error); }
        }
        if (errors != null) throw new AggregateException("Failed to release TensorRT/CUDA resources", errors);
    }

    private static int ElementCount(int[] dims)
    {
        ArgumentNullException.ThrowIfNull(dims);
        if (dims.Length is < 1 or > 7 || dims.Any(d => d < 1))
            throw new ArgumentException("Item dimensions must have rank 1..7 and positive extents", nameof(dims));
        int count = dims.Aggregate(1, (a, b) => checked(a * b));
        _ = checked(count * sizeof(float));
        return count;
    }

    private static void ValidateTensor(TensorRtEngine engine, string name, TensorRtIOMode mode, int[] itemDims)
    {
        if (engine.GetTensorIndex(name) < 0 || engine.GetTensorIOMode(name) != mode
            || engine.GetTensorDataType(name) != TensorRtDataType.Float
            || engine.GetTensorLocation(name) != TensorRtTensorLocation.Device
            || engine.GetTensorFormat(name) != TensorRtTensorFormat.Linear)
            throw new NotSupportedException($"Tensor '{name}' must be a device-resident linear FP32 {mode}");
        int[] dims = engine.GetTensorShape(name).Values;
        if (dims.Length != itemDims.Length + 1 || (dims[0] != -1 && dims[0] < 1)
            || !dims.Skip(1).SequenceEqual(itemDims))
            throw new ArgumentException($"Engine tensor '{name}' does not match the configured item dimensions");
    }
}
