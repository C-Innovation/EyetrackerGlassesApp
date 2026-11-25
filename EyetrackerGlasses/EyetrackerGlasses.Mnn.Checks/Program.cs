using EyetrackerGlasses.Mnn;

if (args.Length == 0) throw new ArgumentException("Pass the .mnn model generated from native/mnn/make_fixture.py");
string path = Path.GetFullPath(args[0]);
var input = Enumerable.Range(0, 12).Select(i => (float)(i - 6)).ToArray();
using var runtime = new MnnYoloRuntime(path, threads: 2, preferAutoBackend: false);
Check(runtime.InputShape.SequenceEqual(new[] { 1, 3, 2, 2 }), "Input shape");
Check(runtime.InputElementCount == 12 && !runtime.InputIsNhwc, "Input metadata/layout");
int[] copy = runtime.InputShape;
copy[0] = 999;
Check(runtime.InputShape[0] == 1, "Input shape must be immutable from caller code");
Throws<ArgumentException>(() => runtime.Infer(new float[11]));
Verify(runtime.Infer(input));
await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Verify(runtime.Infer(input)))));
Console.WriteLine("PASS: C# interop, tensor values/names/layout, concurrent Infer and copied metadata");

string directory = Path.Combine(Path.GetTempPath(), "MNN checks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string unicodePath = Path.Combine(directory, "модель с пробелами.mnn");
    File.Copy(path, unicodePath);
    using (var unicode = new MnnYoloRuntime(unicodePath, preferAutoBackend: true)) Verify(unicode.Infer(input));
    Throws<ArgumentOutOfRangeException>(() => new MnnYoloRuntime(path, threads: 0));
    Throws<ArgumentException>(() => new MnnYoloRuntime(path, maxModelBytes: 1));
    Throws<FileNotFoundException>(() => new MnnYoloRuntime(Path.Combine(directory, "missing.mnn")));
    await using var source = File.OpenRead(path);
    string cached = await MnnModelFile.CopyToLocalFileAsync(source, directory);
    source.Position = 0;
    Check(cached == await MnnModelFile.CopyToLocalFileAsync(source, directory), "Cache identity");
    Check(File.ReadAllBytes(cached).SequenceEqual(File.ReadAllBytes(path)), "Cache content");
    await File.WriteAllTextAsync(cached, "corrupt cache");
    source.Position = 0;
    bool rejected = false;
    try { await MnnModelFile.CopyToLocalFileAsync(source, directory); }
    catch (IOException) { rejected = true; }
    Check(rejected, "Corrupt cached model must not be silently accepted");
    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary model cleanup");
    Console.WriteLine("PASS: Unicode paths, CPU/Auto, argument validation and content-addressed model cache");
}
finally
{
    foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}

runtime.Dispose();
runtime.Dispose();
Throws<ObjectDisposedException>(() => runtime.Infer(input));
for (int i = 0; i < 10; i++)
{
    using var repeated = new MnnYoloRuntime(path, threads: 1);
    Verify(repeated.Infer(input));
}
Console.WriteLine("PASS: repeated create/infer/dispose, double Dispose, use after Dispose");

static void Verify(IReadOnlyList<MnnTensor> outputs)
{
    Check(outputs.Count == 2, "Output count");
    foreach (var output in outputs)
    {
        Check(output.Shape.SequenceEqual(new[] { 1, 3, 2, 2 }) && !output.IsNhwc, "Output layout");
        Check(output.Name is "affine" or "relu", "Output name");
        Check(output.Values.Length == 12, "Output logical element count (no channel padding)");
        for (int i = 0; i < 12; i++)
        {
            float expected = (i - 6) * .5f + .25f;
            if (output.Name == "relu") expected = Math.Max(i - 6, 0);
            Check(Math.Abs(output.Values[i] - expected) < 1e-5, $"Numeric mismatch in {output.Name}[{i}]");
        }
    }
}
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
