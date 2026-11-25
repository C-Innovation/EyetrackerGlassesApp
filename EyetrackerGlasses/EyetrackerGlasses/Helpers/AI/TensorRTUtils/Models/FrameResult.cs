using System.Collections.Generic;

namespace TrtDetector.Models;

/// <summary>Результат по одному кадру.</summary>
public sealed class FrameResult
{
    /// <summary>isInferenceDone</summary>
    public bool IsDone;

    /// <summary>Размер исходного кадра, к которому относятся координаты.</summary>
    public int SourceWidth, SourceHeight;

    /// <summary>Детекции после NMS.</summary>
    public List<Detection> Detections { get; } = new();

    /// <summary>inferenceData.infData (с учётом PostprocessOptions.ItemsMinConf / PadItemsTo).</summary>
    public List<InferenceItem> Items { get; } = new();

    /// <summary>Максимальный score среди всех якорей ДО отсечки по порогу (lastMaxScore).</summary>
    public float MaxScore;
}
