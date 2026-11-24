using System.Diagnostics;
using EyetrackerGlasses.Core;

// Без тестовых NuGet-пакетов. Этот же executable служит контролируемой заменой trtexec.
if (args.Any(a => a.StartsWith("--onnx=")))
{
    string model = args.Single(a => a.StartsWith("--onnx="))[7..];
    string output = args.Single(a => a.StartsWith("--saveEngine="))[13..];
    if (!args.Contains("--skipInference") || !args.Any(a => a.StartsWith("--device=")))
        return 9;
    await File.WriteAllTextAsync(output, "partial");
    if (model.Contains("cancel"))
        await Task.Delay(Timeout.Infinite);
    if (model.Contains("failure"))
    {
        // Заполняем оба pipe: проверяем, что экспорт не блокируется на чтении журнала.
        await Task.WhenAll(Console.Out.WriteAsync(new string('o', 100_000)),
            Console.Error.WriteAsync(new string('e', 100_000)));
        return 5;
    }
    if (model.Contains("empty"))
        await File.WriteAllTextAsync(output, "");
    if (model.Contains("dynamic") && (!args.Contains("--minShapes=images:1x3x64x64")
        || !args.Contains("--optShapes=images:2x3x64x64")
        || !args.Contains("--maxShapes=images:4x3x64x64")))
        return 8;
    return 0;
}

if (args.Contains("--no-gpu"))
{
    // Несуществующие модель и trtexec: проверка GPU должна иметь приоритет.
    var result = await new TensorRtExporter("missing.onnx", "missing-trtexec").RunExport();
    Check(result.Status == TensorRtExportStatus.NoAvaliableGPU, result.Log);
    Console.WriteLine("PASS: NoAvaliableGPU, экспорт не запускался.");
    return 0;
}

var noGpuInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
noGpuInfo.ArgumentList.Add("--no-gpu");
noGpuInfo.Environment["CUDA_VISIBLE_DEVICES"] = "-1";
using (var child = Process.Start(noGpuInfo)!)
{
    await child.WaitForExitAsync();
    Check(child.ExitCode == 0, "Проверка без GPU не пройдена.");
}

string directory = Path.Combine(Path.GetTempPath(), "TensorRT checks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string model = Path.Combine(directory, "model with spaces.onnx");
    await File.WriteAllTextAsync(model, "fake ONNX for process checks");
    var missingTool = await new TensorRtExporter(model, Path.Combine(directory, "missing-trtexec")).RunExport();
    if (missingTool.Status == TensorRtExportStatus.NoAvaliableGPU)
    {
        Console.WriteLine("SKIP: проверки дочернего процесса требуют доступного GPU. " + missingTool.Log);
        return 0;
    }
    Check(missingTool.Status == TensorRtExportStatus.TensorRtNotFound, missingTool.Log);
    Console.WriteLine("PASS: TensorRtNotFound.");

    var success = await new TensorRtExporter(model, Environment.ProcessPath).RunExport();
    Check(success.Status == TensorRtExportStatus.Success && File.Exists(success.EnginePath), success.Log);
    Check(success.EnginePath == Path.ChangeExtension(model, ".engine"), "Неверный выходной путь.");
    var repeat = await new TensorRtExporter(model, Environment.ProcessPath).RunExport();
    Check(repeat.Status == TensorRtExportStatus.ExportFailed, "Существующий engine перезаписан.");
    Check(await File.ReadAllTextAsync(success.EnginePath!) == "partial", "Изменился существующий engine.");
    Console.WriteLine("PASS: успешный процесс, пути с пробелами, защита существующего engine.");

    foreach (string name in new[] { "failure", "empty", "dynamic", "cancel" })
    {
        string input = Path.Combine(directory, name + ".onnx");
        await File.WriteAllTextAsync(input, "fake");
        var exporter = new TensorRtExporter(input, Environment.ProcessPath);
        if (name == "cancel")
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await exporter.RunExport(cancellationToken: cancellation.Token);
                throw new Exception("Отмена не выбросила OperationCanceledException.");
            }
            catch (OperationCanceledException) { }
            Check(!File.Exists(Path.ChangeExtension(input, ".engine")), "После отмены остался engine.");
        }
        else if (name == "dynamic")
        {
            var invalid = await exporter.RunExport(minShapes: "images:1x3x64x64");
            Check(invalid.Status == TensorRtExportStatus.ExportFailed, "Принят неполный профиль.");
            var result = await exporter.RunExport(minShapes: "images:1x3x64x64",
                optShapes: "images:2x3x64x64", maxShapes: "images:4x3x64x64");
            Check(result.Status == TensorRtExportStatus.Success, result.Log);
        }
        else
        {
            var result = await exporter.RunExport();
            Check(result.Status == TensorRtExportStatus.ExportFailed, "Не обнаружена ошибка trtexec.");
            Check(!File.Exists(Path.ChangeExtension(input, ".engine")), "После ошибки остался engine.");
            if (name == "failure")
                Check(result.Log.Contains(new string('e', 1000)) && result.Log.Contains(new string('o', 1000)),
                    "Потеряна диагностика stdout/stderr.");
        }
        Check(Directory.GetFiles(directory, ".*.engine").Length == 0, "Остался временный engine.");
        Console.WriteLine("PASS: " + name);
    }
}
finally
{
    // Только созданные этим запуском файлы; рекурсивное удаление не используется.
    foreach (string path in Directory.GetFiles(directory))
        File.Delete(path);
    Directory.Delete(directory);
}
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
