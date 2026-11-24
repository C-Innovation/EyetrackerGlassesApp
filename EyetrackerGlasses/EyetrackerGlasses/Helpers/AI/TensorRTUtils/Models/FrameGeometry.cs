namespace TrtDetector.Models;

/// <summary>Как вернуть координаты модели в исходное изображение.</summary>
public readonly record struct FrameGeometry(int SrcW, int SrcH, float XScale, float YScale);
