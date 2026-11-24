namespace EyetrackerGlasses.Mnn;

/// <summary>Unpacked FP32 output; Shape and Values belong to this result, not to the native session.</summary>
public sealed record MnnTensor(string Name, int[] Shape, float[] Values, bool IsNhwc);
