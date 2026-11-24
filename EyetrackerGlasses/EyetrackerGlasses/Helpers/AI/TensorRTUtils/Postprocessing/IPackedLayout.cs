using OpenCvSharp;

namespace TrtDetector.Postprocessing;

/// <summary>Раскладка "упакованных глаз" (eyes_packing в C++). Реализуйте по eyes_packing.h.</summary>
public interface IPackedLayout
{
    /// <summary>kFramesPerImage</summary>
    int TileCount { get; }

    /// <summary>kStride</summary>
    float Stride { get; }

    /// <summary>eyes_packing::clipToTile</summary>
    bool ClipToTile(ref Rect2f box, out int tile);
}
