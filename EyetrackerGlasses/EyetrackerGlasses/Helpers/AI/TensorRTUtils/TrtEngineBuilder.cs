using JYPPX.TensorRtSharp;
using JYPPX.TensorRtSharp.Shared;
using System;
using System.IO;
using TrtDetector.Models;

namespace TrtDetector.Inference;

/// <summary>
/// ONNX -> serialized engine (TensorRT 11). Профиль batch берётся из ModelSpec (H/W фиксированы).
/// ONNX и его external data должны происходить из доверенного источника.
/// </summary>
public static class TrtEngineBuilder
{
    /// <param name="optBatch">Batch, под который TensorRT оптимизирует (по умолчанию max/2).</param>
    public static void Build(string onnxPath, string enginePath, ModelSpec spec, int? optBatch = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(onnxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(enginePath);
        onnxPath = Path.GetFullPath(onnxPath);
        enginePath = Path.GetFullPath(enginePath);
        if (!File.Exists(onnxPath)) throw new FileNotFoundException("ONNX model not found", onnxPath);
        if (string.Equals(onnxPath, enginePath, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Engine path must differ from the ONNX path", nameof(enginePath));
        spec = spec with { Strides = (int[]?)spec.Strides?.Clone() };
        spec.Validate();
        int maxB = spec.EffectiveMaxBatch;
        int minB = spec.FixedBatch ?? 1;
        int opt = spec.FixedBatch ?? Math.Clamp(optBatch ?? Math.Max(1, maxB / 2), minB, maxB);
        if (optBatch is int requested && (requested < minB || requested > maxB))
            throw new ArgumentOutOfRangeException(nameof(optBatch), "optBatch must be within the batch profile");

        using var logger  = new TensorRtLogger(JYPPX.TensorRtSharp.Shared.Interop.TensorRtApiLine.TensorRt11);
        using var builder = new TensorRtBuilder(logger);
        using var config  = builder.CreateBuilderConfig();
        using var network = builder.CreateNetwork(TensorRtNetworkDefinitionCreationFlags.ExplicitBatch);
        using var parser  = new TensorRtOnnxParser(logger, network);

        // Keep the absolute ONNX path so external weight files resolve relative to the model.
        if (!parser.ParseFromFile(onnxPath, 2))
            throw new InvalidOperationException(parser.GetErrorSummary());

        using var profile = builder.CreateOptimizationProfile();
        TensorRtDims Dims(int b) => new(new[] { b, spec.Channels, spec.Height, spec.Width });
        profile.SetShape(spec.InputName, Dims(minB), Dims(opt), Dims(maxB));
        config.AddOptimizationProfile(profile);

        using var plan = builder.BuildSerializedNetwork(network, config);
        Directory.CreateDirectory(Path.GetDirectoryName(enginePath)!);
        string temporaryPath = Path.Combine(Path.GetDirectoryName(enginePath)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            plan.SaveToFile(temporaryPath);
            if (new FileInfo(temporaryPath).Length == 0)
                throw new InvalidOperationException("TensorRT produced an empty engine");
            // Replace only after a complete successful build; a failure preserves the old engine.
            File.Move(temporaryPath, enginePath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
