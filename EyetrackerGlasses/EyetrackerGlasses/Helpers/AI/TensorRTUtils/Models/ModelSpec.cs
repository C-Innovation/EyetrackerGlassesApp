using System;
using System.Linq;

namespace TrtDetector.Models;

/// <summary>Расположение выходного тензора детектора.</summary>
public enum OutputLayout
{
    /// <summary>[F, N]: элемент (f, a) = o[f*N + a] (YOLOv8/11, ваша модель из C++).</summary>
    ChannelsFirst,
    /// <summary>[N, F]: элемент (f, a) = o[a*F + f] (YOLOv5 и аналоги).</summary>
    AnchorsFirst
}

/// <summary>
/// Универсальное описание модели: размер входа, число каналов, число классов, batch.
/// Число выходных каналов F = 4 (cx,cy,w,h) + [1 objectness] + NumClasses.
/// </summary>
public sealed record ModelSpec(
    string InputName = "images",
    string OutputName = "output0",
    int Channels = 1,                       // любое >= 1 (1, 3, 4, ...)
    int Height = 224,
    int Width = 640,
    int NumClasses = 2,
    bool HasObjectness = false,             // true: score = obj * max(class) (YOLOv5-стиль)
    OutputLayout Layout = OutputLayout.ChannelsFirst,
    int[]? Strides = null,                  // по умолчанию 8/16/32
    int? Anchors = null,                    // явное N, если не совпадает с расчётным
    int MaxBatch = 16,                      // макс. число кадров за запуск (<= max профиля engine)
    int? FixedBatch = null)                 // для engine со статическим batch: всегда этот batch (добивается нулями)
{
    private static readonly int[] DefaultStrides = { 8, 16, 32 };

    public int Outputs => checked(4 + (HasObjectness ? 1 : 0) + NumClasses);

    public int AnchorCount => Anchors ??
        (Strides ?? DefaultStrides).Sum(s => checked((Height / s) * (Width / s)));

    public int EffectiveMaxBatch => FixedBatch ?? MaxBatch;

    public int[] InputItemDims => new[] { Channels, Height, Width };

    public int[] OutputItemDims => Layout == OutputLayout.ChannelsFirst
        ? new[] { Outputs, AnchorCount }
        : new[] { AnchorCount, Outputs };

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputName);
        if (InputName == OutputName) throw new ArgumentException("Input/output names must differ");
        if (Channels is < 1 or > 512) throw new ArgumentException("Channels must be in [1, 512]");
        if (Height < 1 || Width < 1) throw new ArgumentException("Height/Width must be > 0");
        if (NumClasses < 1) throw new ArgumentException("NumClasses must be >= 1");
        if (MaxBatch < 1 || FixedBatch is < 1) throw new ArgumentException("MaxBatch/FixedBatch must be >= 1");
        if (!Enum.IsDefined(Layout)) throw new ArgumentException("Unknown output layout");
        if (Strides is { Length: 0 } || Strides?.Any(s => s <= 0) == true)
            throw new ArgumentException("Strides must be nonempty and positive");
        if (AnchorCount < 1) throw new ArgumentException("AnchorCount must be >= 1");
        // CUDA bindings use signed 32-bit byte counts. Check BEFORE allocations/native calls.
        _ = checked(EffectiveMaxBatch * Channels * Height * Width * sizeof(float));
        _ = checked(EffectiveMaxBatch * Outputs * AnchorCount * sizeof(float));
    }
}
