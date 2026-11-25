using OpenCvSharp;
using TrtDetector.Inference;
using TrtDetector.Models;
using TrtDetector.Postprocessing;
using TrtDetector.Preprocessing;
using TrtDetector.Visualization;
using EyetrackerGlasses.Mnn;

if (args.Length == 4 && args[0] == "--infer-mnn")
{
    // StartTarget has a batch-1 BGR input and a dense [1, 5, 4620] output.
    var startTarget = new ModelSpec(Channels: 3, Height: 352, Width: 640, NumClasses: 1, FixedBatch: 1);
    using var frame = Cv2.ImRead(args[2], ImreadModes.Color);
    if (frame.Empty()) throw new InvalidOperationException($"Could not decode image '{args[2]}'.");
    using var detector = new MnnInferenceEngine(args[1], startTarget,
        post: new PostprocessOptions { ClassNames = new[] { "target" } });
    FrameResult result = detector.Run(frame);
    using var preview = BBoxDrawer.AddBBox(frame, result);
    if (!Cv2.ImWrite(args[3], preview)) throw new IOException($"Could not write image '{args[3]}'.");
    Console.WriteLine($"MNN: {result.Detections.Count} detections, MaxScore={result.MaxScore:F4}. Saved to {args[3]}");
    return;
}

// Compare a trusted ONNX model converted to MNN against its existing TensorRT engine.
if (args.Length is 3 or 4 && args[0] == "--compare-mnn")
{
    using var mnn = new MnnYoloRuntime(args[1], threads: 4, preferAutoBackend: false);
    int[] shape = mnn.InputShape;
    if (shape[0] != 1 || mnn.InputIsNhwc) throw new NotSupportedException("Comparison expects batch-1 NCHW");
    var input = new float[mnn.InputElementCount];
    var random = new Random(1234);
    for (int i = 0; i < input.Length; i++) input[i] = random.NextSingle();
    var output = mnn.Infer(input).Single();
    using var trt = new TrtRunner(args[2], "images", shape[1..], output.Name, output.Shape[1..]);
    float[] expected = trt.Run(input, 1);
    if (expected.Length != output.Values.Length) throw new Exception("Output lengths differ");
    double maxAbsolute = 0, maxNormalized = 0, sum = 0;
    for (int i = 0; i < expected.Length; i++)
    {
        double delta = Math.Abs(expected[i] - output.Values[i]);
        if (!double.IsFinite(delta)) throw new Exception("Nonfinite model output");
        maxAbsolute = Math.Max(maxAbsolute, delta);
        maxNormalized = Math.Max(maxNormalized, delta / Math.Max(1, Math.Abs(expected[i])));
        sum += delta;
    }
    Console.WriteLine($"MNN vs TensorRT ({expected.Length} values): max absolute={maxAbsolute:G6}, " +
        $"mean absolute={sum / expected.Length:G6}, max normalized={maxNormalized:G6}");
    if (maxNormalized > 0.01) throw new Exception("MNN/TensorRT normalized difference exceeds 1%");
    Console.WriteLine("PASS: converted YOLO numeric comparison");
    if (args.Length == 4)
    {
        var model = new ModelSpec(Channels: shape[1], Height: shape[2], Width: shape[3],
            NumClasses: output.Shape[1] - 4, OutputName: output.Name, Anchors: output.Shape[2], FixedBatch: 1);
        using var frame = Cv2.ImRead(args[3], ImreadModes.Color);
        Check(!frame.Empty(), "Comparison image could not be decoded");
        using var empty = new Mat();
        using var detector = new MnnInferenceEngine(args[1], model);
        using var reference = new InferenceEngine(args[2], model);
        var frames = new[] { frame, empty, frame };
        List<FrameResult> actual = detector.RunBatch(frames);
        List<FrameResult> baseline = reference.RunBatch(frames);
        Check(actual.Count == frames.Length && actual[1].IsDone && actual[1].Detections.Count == 0,
            "MNN should preserve empty frames in the batch");
        Check(baseline[0].Detections.Count > 0, "Comparison image must produce at least one detection");
        for (int i = 0; i < actual.Count; i++)
        {
            Check(actual[i].Detections.Count == baseline[i].Detections.Count, $"Detection count differs at frame {i}");
            Check(Math.Abs(actual[i].MaxScore - baseline[i].MaxScore) < 0.01f, $"MaxScore differs at frame {i}");
            foreach (var (a, b) in actual[i].Detections.Zip(baseline[i].Detections))
                Check(a.ClassId == b.ClassId && Math.Abs(a.Confidence - b.Confidence) < 0.01f
                    && Math.Abs(a.Box.X - b.Box.X) < 1 && Math.Abs(a.Box.Y - b.Box.Y) < 1
                    && Math.Abs(a.Box.Width - b.Box.Width) < 1 && Math.Abs(a.Box.Height - b.Box.Height) < 1,
                    $"Detection differs at frame {i}");
        }
        var converter = new MnnYoloTensorConverter(mnn, model);
        float[] imageInput = converter.PrepareInput(new[] { frame }, out var imageGeometry);
        var tensors = mnn.Infer(imageInput);
        Throws<InvalidOperationException>(() => converter.DecodeOutput(Array.Empty<MnnTensor>(), imageGeometry));
        var namedOutput = tensors.Single(t => t.Name == model.OutputName);
        var wrongShape = namedOutput with { Shape = new[] { 1, model.AnchorCount, model.Outputs } };
        Throws<NotSupportedException>(() => converter.DecodeOutput(new[] { wrongShape }, imageGeometry));
        var extra = namedOutput with { Name = "other" };
        Check(converter.DecodeOutput(new[] { extra, namedOutput }, imageGeometry)[0].Detections.Count
            == actual[0].Detections.Count, "MNN output selection must use the tensor name");
        Check(detector.Run(empty).IsDone && detector.LastMaxScore == 0, "Empty frame must reset LastMaxScore");
        Console.WriteLine("PASS: Mat-to-detections MNN vs TensorRT, batch order and output validation");
    }
    return;
}

int passed = 0;
void Test(string name, Action test)
{
    test();
    Console.WriteLine("PASS: " + name);
    passed++;
}

Test("Reject invalid model dimensions and integer overflow before native calls", () =>
{
    Throws<ArgumentException>(() => new ModelSpec(Strides: new[] { 0 }).Validate());
    Throws<ArgumentException>(() => new ModelSpec(Anchors: -1).Validate());
    Throws<ArgumentException>(() => new ModelSpec(Layout: (OutputLayout)99).Validate());
    Throws<ArgumentException>(() => new ModelSpec(InputName: "output0").Validate());
    Throws<OverflowException>(() => new ModelSpec(Width: int.MaxValue, Height: 2, Anchors: 1).Validate());
    Throws<OverflowException>(() => new ModelSpec(Anchors: int.MaxValue).Validate());
    Throws<ArgumentException>(() => new TrtRunner("missing.engine", "images", new[] { 0 }, "output0", new[] { 1 }));
    Throws<OverflowException>(() => new TrtRunner("missing.engine", "images", new[] { int.MaxValue }, "output0", new[] { 1 }));
});

var spec = new ModelSpec(Channels: 1, Height: 8, Width: 8, NumClasses: 1, Anchors: 1, MaxBatch: 2);
var geometry = new FrameGeometry(8, 8, 1, 1);

Test("Reject invalid thresholds, padding and truncated tensor output", () =>
{
    Throws<ArgumentOutOfRangeException>(() => new DetectionPostprocessor(spec,
        new PostprocessOptions { ScoreThreshold = float.NaN }));
    Throws<ArgumentOutOfRangeException>(() => new DetectionPostprocessor(spec,
        new PostprocessOptions { PadItemsTo = int.MaxValue }));
    var post = new DetectionPostprocessor(spec);
    Throws<ArgumentException>(() => post.Parse(new float[4], geometry));
    Throws<ArgumentException>(() => post.Parse(new float[5], new FrameGeometry(0, 8, 1, 1)));
});

Test("Skip nonfinite output and correctly clip left/top edges", () =>
{
    var post = new DetectionPostprocessor(spec);
    foreach (int index in Enumerable.Range(0, 5))
    {
        float[] data = { 0, 0, 4, 4, 0.9f };
        data[index] = float.PositiveInfinity;
        Check(post.Parse(data, geometry).Detections.Count == 0, "Nonfinite candidate accepted");
    }
    var result = post.Parse(new float[] { 0, 0, 4, 4, 0.9f }, geometry);
    Check(result.Detections.Count == 1 && result.Detections[0].Box == new Rect2f(0, 0, 2, 2),
        "Partly negative box has wrong clipped width/height");
});

Test("NMS candidate limit retains original candidate indices", () =>
{
    var post = new DetectionPostprocessor(spec with { Anchors = 3 },
        new PostprocessOptions { MaxNmsCandidates = 1 });
    // ChannelsFirst: three centers x, centers y, widths, heights, scores.
    float[] data = { 1, 3, 6, 1, 3, 6, 1, 1, 1, 1, 1, 1, .6f, .7f, .9f };
    var result = post.Parse(data, geometry);
    Check(result.Detections.Count == 1 && result.Detections[0].Box.X == 5.5f,
        "NMS returned an index from the temporary subset");
});

Test("Packed NMS respects classes and the tile returned by ClipToTile", () =>
{
    var post = new DetectionPostprocessor(spec with { NumClasses = 2, Anchors = 2 },
        new PostprocessOptions { ClassAgnosticNms = false }, new TestLayout());
    float[] data = { 4, 4, 4, 4, 4, 4, 4, 4, .9f, .1f, .1f, .9f };
    Check(post.Parse(data, geometry).Detections.Count == 2, "Different classes suppressed in a tile");
    var invalid = new DetectionPostprocessor(spec, packed: new TestLayout { InvalidTile = true });
    Check(invalid.Parse(new float[] { 4, 4, 4, 4, .9f }, geometry).Detections.Count == 0,
        "Invalid tile accepted");
});

Test("Postprocess options are snapshotted", () =>
{
    var options = new PostprocessOptions { ClassNames = new[] { "original" } };
    var post = new DetectionPostprocessor(spec, options);
    options.ScoreThreshold = float.NaN;
    options.ClassNames[0] = "changed";
    var result = post.Parse(new float[] { 4, 4, 4, 4, .9f }, geometry);
    Check(result.Detections.Single().ClassName == "original", "Caller changed active parser options");
});

Test("Preprocess handles non-contiguous Mat and batch padding", () =>
{
    using var parent = new Mat(4, 6, MatType.CV_8UC1, Scalar.All(255));
    using var roi = new Mat(parent, new Rect(1, 1, 3, 2));
    var pre = new FramePreprocessor(spec, new float[1], new float[] { 1 });
    float[] data = pre.Process(new[] { roi }, false, 2, out var geo);
    Check(data.Take(64).All(v => Math.Abs(v - 1) < 1e-5) && data.Skip(64).All(v => v == 0),
        "Invalid NCHW data/padding");
    Check(geo[0] == new FrameGeometry(3, 2, 3f / 8, 2f / 8), "Wrong resize geometry");
    Throws<ArgumentOutOfRangeException>(() => pre.Process(new[] { roi }, true, 3, out _));
});

Test("Normalization validation is repeated between runs", () =>
{
    float[] div = { 1 };
    var pre = new FramePreprocessor(spec, new float[1], div);
    div[0] = 0;
    Throws<ArgumentException>(() => pre.Process(Array.Empty<Mat>(), true, 1, out _));
    div[0] = float.NaN;
    Throws<ArgumentException>(() => pre.Process(Array.Empty<Mat>(), true, 1, out _));
});

Test("RGB planar ordering and normalization", () =>
{
    using var image = new Mat(1, 1, MatType.CV_8UC3, new Scalar(10, 20, 30));
    var pre = new FramePreprocessor(spec with { Channels = 3, Height = 1, Width = 1 },
        new float[] { 1, 2, 3 }, new float[] { 2, 2, 2 }, new PreprocessOptions { SwapRB = true, Scale8 = 1 });
    float[] result = pre.Process(new[] { image }, false, 1, out _);
    Check(result.SequenceEqual(new float[] { 14.5f, 9, 3.5f }), "Invalid RGB NCHW normalization");
});

Test("Rounded letterbox geometry matches the actual resized ROI", () =>
{
    using var image = new Mat(2, 3, MatType.CV_8UC1, Scalar.All(255));
    var pre = new FramePreprocessor(spec with { Width = 4, Height = 4 }, new float[1], new float[] { 1 });
    pre.Process(new[] { image }, true, 1, out var geo);
    Check(geo[0].XScale == 3f / 4 && geo[0].YScale == 2f / 3, "Rounding ignored in letterbox geometry");
});

Test("Draw 1x1/border boxes and int.MinValue class IDs", () =>
{
    using var image = new Mat(1, 1, MatType.CV_8UC3, Scalar.Black);
    var frame = new FrameResult { SourceWidth = 1, SourceHeight = 1 };
    frame.Detections.Add(new Detection(int.MinValue, "edge", .9f, new Rect2f(0, 0, 1, 1)));
    frame.Detections.Add(new Detection(0, "bad", .9f, new Rect2f(float.NaN, 0, 1, 1)));
    using var rendered = BBoxDrawer.AddBBox(image, frame, new BBoxDrawOptions { ShowLabel = false });
    Check(rendered.At<Vec3b>(0, 0) != new Vec3b(0, 0, 0), "Border box not drawn");
    Check(image.At<Vec3b>(0, 0) == new Vec3b(0, 0, 0), "Source image modified");
    Throws<ArgumentOutOfRangeException>(() => BBoxDrawer.DrawInPlace(image, frame,
        new BBoxDrawOptions { FontScale = double.NaN }));
    Throws<ArgumentException>(() => BBoxDrawer.DrawInPlace(image, frame,
        new BBoxDrawOptions { ConfidenceFormat = "F999999999" }));
});

Test("Integer detection conversion validates image sizes and nonfinite coordinates", () =>
{
    var detection = new Detection(0, "test", .9f, new Rect2f(float.MaxValue, 0, float.MaxValue, 1));
    var result = detection.ToObject(8, 8);
    Check(result.X == 7 && result.W == 1, "Floating point overflow while clipping");
    Throws<ArgumentOutOfRangeException>(() => detection.ToObject(0, 8));
    Throws<ArgumentException>(() => (detection with { Box = new Rect2f(float.NaN, 0, 1, 1) }).ToObject(8, 8));
});

// Optional smoke check for a trusted local engine. No engine is built or overwritten.
// Usage: dotnet run --project EyetrackerGlasses/EyetrackerGlasses.AI.Checks -- --engine <path>
if (args.Length == 2 && args[0] == "--engine")
{
    Test("GPU runner: metadata, execution, failed batch recovery and disposal", () =>
    {
        using var logger = new JYPPX.TensorRtSharp.TensorRtLogger(
            JYPPX.TensorRtSharp.Shared.Interop.TensorRtApiLine.TensorRt11);
        using var runtime = new JYPPX.TensorRtSharp.TensorRtRuntime(logger);
        using var engine = runtime.DeserializeFromFile(Path.GetFullPath(args[1]));
        string inputName = "images", outputName = "output0";
        int[] inputDims = engine.GetTensorShape(inputName).Values;
        int[] outputDims = engine.GetTensorShape(outputName).Values;
        Console.WriteLine($"Engine shapes: {string.Join('x', inputDims)} -> {string.Join('x', outputDims)}");
        Check(inputDims.Skip(1).All(d => d > 0) && outputDims.Skip(1).All(d => d > 0),
            "Smoke check requires fixed non-batch dimensions");
        int batch = inputDims[0] > 0 ? inputDims[0] : 1;
        inputDims = inputDims[1..];
        outputDims = outputDims[1..];
        int[] incorrect = (int[])outputDims.Clone();
        incorrect[^1]++;
        Throws<ArgumentException>(() => new TrtRunner(args[1], inputName, inputDims, outputName, incorrect));
        using var runner = new TrtRunner(args[1], inputName, inputDims, outputName, outputDims);
        var input = new float[batch * inputDims.Aggregate(1, (a, b) => a * b)];
        float[] result = runner.Run(input, batch);
        Check(result.Length == batch * outputDims.Aggregate(1, (a, b) => a * b), "Wrong output length");
        Throws<ArgumentException>(() => runner.Run(Array.Empty<float>(), batch));
        // Static engines reject a different batch; a subsequent valid run must still work.
        if (engine.GetTensorShape(inputName).Values[0] > 0)
        {
            bool rejected = false;
            try { runner.Run(new float[input.Length * 2], batch * 2); }
            catch (Exception) { rejected = true; }
            Check(rejected, "Static batch mismatch accepted");
            Check(runner.Run(input, batch).Length == result.Length, "Runner failed to recover after invalid batch");
        }
        runner.Dispose();
        runner.Dispose();
        Throws<ObjectDisposedException>(() => runner.Run(input, batch));
        if (inputDims.Length == 3 && outputDims.Length == 2 && outputDims[0] >= 5)
        {
            var model = new ModelSpec(Channels: inputDims[0], Height: inputDims[1], Width: inputDims[2],
                NumClasses: outputDims[0] - 4, Anchors: outputDims[1], FixedBatch: batch);
            using var pipeline = new InferenceEngine(args[1], model);
            using var frame = new Mat(inputDims[1], inputDims[2], MatType.CV_8UC(inputDims[0]), Scalar.All(0));
            using var empty = new Mat();
            var frames = pipeline.RunBatch(new[] { frame, empty });
            Check(frames.Count == 2 && frames.All(f => f.IsDone) && pipeline.LastMaxScore == 0,
                "Empty frame did not reset LastMaxScore");
            pipeline.Dispose();
            Throws<ObjectDisposedException>(() => pipeline.Run(frame));
        }
    });
}

Console.WriteLine($"Passed {passed} AI regression checks. GPU smoke check runs only with --engine <trusted-path>.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class TestLayout : IPackedLayout
{
    public int TileCount => 2;
    public float Stride => 4;
    public bool InvalidTile { get; init; }
    public bool ClipToTile(ref Rect2f box, out int tile)
    {
        tile = InvalidTile ? -1 : 0;
        return true;
    }
}
