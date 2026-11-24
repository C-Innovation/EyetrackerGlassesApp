namespace TrtDetector.Models;

/// <summary>Аналог InferenceData из C++ (org_bbox_coords + conf + object).</summary>
public readonly record struct InferenceItem(
    float Left, float Top, float Width, float Height, float Conf, int Object);
