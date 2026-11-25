using OpenCvSharp;

namespace TrtDetector.Models;

/// <summary>Аналог struct Detection из C++ (без цвета).</summary>
public sealed record Detection(int ClassId, string ClassName, float Confidence, Rect2f Box);
