namespace TrtDetector.Models;

public sealed class PreprocessOptions
{
    /// <summary>Поменять местами R и B (только для 3 каналов). C++ не меняет (BGR); модели Ultralytics ждут RGB.</summary>
    public bool SwapRB = false;

    /// <summary>Множитель для 8-bit входа (по умолчанию 1/255).</summary>
    public double Scale8 = 1.0 / 255.0;

    /// <summary>Множитель для 16-bit входа (по умолчанию 1/65535).</summary>
    public double Scale16 = 1.0 / 65535.0;
}
