using System.Text;

namespace EyetrackerGlasses.Mnn;

/// <summary>
/// Fixed-input FP32 MNN runtime. Returns raw named tensors, without YOLO decoding/NMS.
/// Infer/Dispose are serialized. Use a background thread for inference in Avalonia.
/// Load only trusted .mnn models. The bridge is not a sandbox for malicious model files.
/// </summary>
public sealed class MnnYoloRuntime : IDisposable
{
    private const int MaxRank = 8;
    private const int ErrorCapacity = 4096;
    private readonly object _gate = new();
    private readonly MnnHandle _handle;
    private readonly int[] _inputShape;

    /// <summary>Logical unpacked shape. Returns a copy to protect the session contract.</summary>
    public int[] InputShape => (int[])_inputShape.Clone();
    public bool InputIsNhwc { get; }
    public int InputElementCount { get; }

    /// <param name="preferAutoBackend">Auto may select CPU. The supplied portable builds contain the CPU backend.</param>
    /// <param name="maxModelBytes">Maximum input file size before reading it into managed memory.</param>
    public unsafe MnnYoloRuntime(string modelPath, int threads = 4, bool preferAutoBackend = true,
        long maxModelBytes = 512L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (threads is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(threads));
        if (maxModelBytes is < 1 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxModelBytes));
        using var stream = File.OpenRead(Path.GetFullPath(modelPath));
        if (stream.Length < 1 || stream.Length > maxModelBytes)
            throw new ArgumentException("MNN file is empty or exceeds the configured size limit", nameof(modelPath));
        byte[] model = new byte[checked((int)stream.Length)];
        stream.ReadExactly(model);
        if (MnnNative.AbiVersion() != 1) throw new NotSupportedException("Incompatible MNN native bridge ABI");
        byte* error = stackalloc byte[ErrorCapacity];
        new Span<byte>(error, ErrorCapacity).Clear();
        IntPtr pointer;
        fixed (byte* bytes = model)
            pointer = MnnNative.Create(bytes, (nuint)model.Length, threads, preferAutoBackend ? 1 : 0,
                error, ErrorCapacity);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException(ReadString(error));
        _handle = new MnnHandle(pointer);
        try
        {
            int* dimensions = stackalloc int[MaxRank];
            int rank = MnnNative.InputInfo(_handle, dimensions, MaxRank, out int count, out int nhwc);
            _inputShape = ValidateShape(dimensions, rank, count, allowScalar: false);
            if (nhwc is not (0 or 1)) throw new InvalidOperationException("Invalid input layout from MNN");
            InputIsNhwc = nhwc == 1;
            InputElementCount = count;
        }
        catch { _handle.Dispose(); throw; }
    }

    /// <summary>Input must already be resized, normalized and arranged in the model's NCHW/NHWC layout.</summary>
    public unsafe IReadOnlyList<MnnTensor> Infer(float[] input)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            ArgumentNullException.ThrowIfNull(input);
            if (input.Length != InputElementCount)
                throw new ArgumentException($"Expected {InputElementCount} FP32 values, received {input.Length}", nameof(input));
            byte* text = stackalloc byte[ErrorCapacity];
            new Span<byte>(text, ErrorCapacity).Clear();
            fixed (float* values = input)
                if (MnnNative.Run(_handle, values, input.Length, text, ErrorCapacity) != 0)
                    throw new InvalidOperationException(ReadString(text));
            int outputCount = MnnNative.OutputCount(_handle);
            if (outputCount is < 1 or > 1024) throw new InvalidOperationException("Invalid output count from MNN");
            var result = new List<MnnTensor>(outputCount);
            int* dimensions = stackalloc int[MaxRank];
            for (int index = 0; index < outputCount; index++)
            {
                new Span<byte>(text, ErrorCapacity).Clear();
                int rank = MnnNative.OutputInfo(_handle, index, dimensions, MaxRank, out int count,
                    out int nhwc, text, ErrorCapacity);
                int[] shape = ValidateShape(dimensions, rank, count, allowScalar: true);
                if (nhwc is not (0 or 1)) throw new InvalidOperationException("Invalid output layout from MNN");
                string name = ReadString(text);
                float[] output = new float[count];
                fixed (float* destination = output)
                    if (MnnNative.OutputCopy(_handle, index, destination, count) != 0)
                        throw new InvalidOperationException($"Could not copy MNN output {index}");
                result.Add(new MnnTensor(name, shape, output, nhwc == 1));
            }
            return result.AsReadOnly();
        }
    }

    public void Dispose()
    {
        lock (_gate) _handle.Dispose();
    }

    private static unsafe int[] ValidateShape(int* dimensions, int rank, int elements, bool allowScalar)
    {
        if (rank < (allowScalar ? 0 : 1) || rank > MaxRank || elements < 1 || elements > int.MaxValue / sizeof(float))
            throw new InvalidOperationException("Invalid tensor metadata from MNN");
        int[] shape = new int[rank];
        int count = 1;
        for (int axis = 0; axis < rank; axis++)
        {
            int extent = dimensions[axis];
            if (extent < 1) throw new NotSupportedException("MNN bridge requires fixed positive tensor dimensions");
            count = checked(count * extent);
            shape[axis] = extent;
        }
        if (count != elements) throw new InvalidOperationException("MNN shape differs from tensor element count");
        return shape;
    }

    private static unsafe string ReadString(byte* text)
    {
        var bytes = new ReadOnlySpan<byte>(text, ErrorCapacity);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
