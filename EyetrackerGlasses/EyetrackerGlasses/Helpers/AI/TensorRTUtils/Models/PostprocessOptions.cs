using System;

namespace TrtDetector.Models;

public sealed class PostprocessOptions
{
    public float ScoreThreshold = 0.50f;
    public float NmsThreshold = 0.50f;

    /// <summary>true: один NMS на все классы (как в C++). false: NMS отдельно по каждому классу.</summary>
    public bool ClassAgnosticNms = true;

    /// <summary>Имена классов; если null или короче — "class{id}".</summary>
    public string[]? ClassNames;

    /// <summary>Минимальный conf для записи в FrameResult.Items (0 — все).</summary>
    public float ItemsMinConf = 0f;

    /// <summary>Дополнять Items записями (-1,-1,-1,-1, conf 0) до этого числа (0 — не дополнять).</summary>
    public int PadItemsTo = 0;

    /// <summary>Предел кандидатов до NMS (защита от квадратичного времени на больших выходах).</summary>
    public int MaxNmsCandidates = 30000;

    public void Validate()
    {
        ValidateThreshold(ScoreThreshold, nameof(ScoreThreshold));
        ValidateThreshold(NmsThreshold, nameof(NmsThreshold));
        ValidateThreshold(ItemsMinConf, nameof(ItemsMinConf));
        if (PadItemsTo is < 0 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(PadItemsTo));
        if (MaxNmsCandidates is < 1 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(MaxNmsCandidates));
    }

    private static void ValidateThreshold(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(name, "Threshold must be finite and in [0, 1]");
    }

    internal PostprocessOptions Snapshot()
    {
        Validate();
        var copy = (PostprocessOptions)MemberwiseClone();
        copy.ClassNames = (string[]?)ClassNames?.Clone();
        return copy;
    }

    /// <summary>Поведение inference_engine.cpp: 1 класс (F==5) — пороги 0.75, дополнение до 3; 2 класса — 0.5.</summary>
    public static PostprocessOptions Legacy(int numClasses) => numClasses == 1
        ? new PostprocessOptions
        {
            ScoreThreshold = 0.75f, NmsThreshold = 0.75f,
            ItemsMinConf = 0.75f, PadItemsTo = 3,
            ClassNames = new[] { "target" }
        }
        : new PostprocessOptions
        {
            ClassNames = numClasses == 2 ? new[] { "pupil", "reflex" } : null
        };
}
