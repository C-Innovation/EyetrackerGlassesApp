using System;
using OpenCvSharp;

namespace TrtDetector.Visualization;

public sealed class BBoxDrawOptions
{
    public int Thickness = 2;
    public double FontScale = 0.5;
    public int FontThickness = 1;

    public bool ShowLabel = true;
    public bool ShowClassName = true;
    public bool ShowConfidence = true;
    public string ConfidenceFormat = "0.00";

    /// <summary>Не рисовать детекции с conf ниже этого значения.</summary>
    public float MinConfidence = 0f;

    /// <summary>Палитра BGR по ClassId (циклически). null — цвета генерируются автоматически для любого числа классов.</summary>
    public Scalar[]? Palette;

    /// <summary>Одноканальное изображение превращать в BGR, чтобы рамки были цветными.</summary>
    public bool ConvertGrayToBgr = true;

    internal void Validate()
    {
        if (Thickness is < 1 or > 32767 || FontThickness is < 1 or > 32767)
            throw new ArgumentOutOfRangeException(nameof(Thickness), "Drawing thickness must be in [1, 32767]");
        if (!double.IsFinite(FontScale) || FontScale <= 0 || FontScale > 100)
            throw new ArgumentOutOfRangeException(nameof(FontScale));
        if (!float.IsFinite(MinConfidence) || MinConfidence < 0 || MinConfidence > 1)
            throw new ArgumentOutOfRangeException(nameof(MinConfidence));
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfidenceFormat);
        if (ConfidenceFormat.Length > 64)
            throw new ArgumentException("Confidence format is too long", nameof(ConfidenceFormat));
        // Standard numeric formats can request hundreds of millions of decimal places.
        if (ConfidenceFormat.Length > 1 && char.IsLetter(ConfidenceFormat[0])
            && (!int.TryParse(ConfidenceFormat.AsSpan(1), out int precision) || precision is < 0 or > 16))
            throw new ArgumentException("Numeric format precision must be in [0, 16]", nameof(ConfidenceFormat));
    }
}
