using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EyetrackerGlasses.Core
{
    /// <summary>
    /// Экспорт ONNX через trtexec из TensorRT SDK 10.13 (Windows/Linux x64).
    /// Проверка GPU выполняется до обращения к модели и запуска trtexec.
    /// </summary>
    public sealed class TensorRtExporter
    {
        private readonly string _onnxPath;
        private readonly string _trtexecPath;

        /// <param name="onnxPath">Путь к исходной ONNX-модели.</param>
        /// <param name="trtexecPath">Полный путь к trtexec либо имя утилиты из PATH.</param>
        public TensorRtExporter(string onnxPath, string? trtexecPath = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(onnxPath);
            _onnxPath = Path.GetFullPath(onnxPath);
            _trtexecPath = trtexecPath ?? (OperatingSystem.IsWindows() ? "trtexec.exe" : "trtexec");
            ArgumentException.ThrowIfNullOrWhiteSpace(_trtexecPath);
        }

        /// <summary>
        /// Создает engine рядом с ONNX или по указанному пути. Существующий engine не перезаписывается.
        /// При отсутствии GPU возвращает NoAvaliableGPU и не запускает экспорт.
        /// Отмена завершает дочерний процесс и выбрасывает OperationCanceledException.
        /// </summary>
        /// <param name="minShapes">Профиль динамических входов: например images:1x3x640x640.</param>
        /// <param name="optShapes">Оптимальные формы; задаются вместе с minShapes и maxShapes.</param>
        /// <param name="maxShapes">Максимальные формы; несколько входов разделяются запятой.</param>
        public async Task<TensorRtExportResult> RunExport(
            string? enginePath = null,
            CancellationToken cancellationToken = default,
            string? minShapes = null,
            string? optShapes = null,
            string? maxShapes = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await Task.Run(() =>
            {
                int? index = CudaGpuProbe.FindDevice(out string diagnostic);
                return (Device: index, Diagnostic: diagnostic);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            int? device = probe.Device;
            if (device == null)
                return new TensorRtExportResult(TensorRtExportStatus.NoAvaliableGPU,
                    Log: probe.Diagnostic);

            string? temporaryPath = null;
            string log = "";
            try
            {
                if (!System.IO.File.Exists(_onnxPath))
                    return Failure("ONNX-модель не найдена: " + _onnxPath, device);

                bool anyShape = minShapes != null || optShapes != null || maxShapes != null;
                if (anyShape && (string.IsNullOrWhiteSpace(minShapes)
                    || string.IsNullOrWhiteSpace(optShapes) || string.IsNullOrWhiteSpace(maxShapes)))
                    return Failure("Для динамических входов задайте minShapes, optShapes и maxShapes вместе.", device);

                string outputPath = Path.GetFullPath(enginePath ?? Path.ChangeExtension(_onnxPath, ".engine"));
                if (System.IO.File.Exists(outputPath))
                    return Failure("Выходной файл уже существует: " + outputPath, device);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                // Промежуточный файл в том же каталоге: не оставляем частичный engine при ошибке.
                temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath)!,
                    "." + Guid.NewGuid().ToString("N") + ".engine");
                var startInfo = new ProcessStartInfo
                {
                    FileName = _trtexecPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("--onnx=" + _onnxPath);
                startInfo.ArgumentList.Add("--saveEngine=" + temporaryPath);
                startInfo.ArgumentList.Add("--device=" + device.Value.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--noTF32");
                startInfo.ArgumentList.Add("--useSpinWait");
                startInfo.ArgumentList.Add("--threads");
                startInfo.ArgumentList.Add("--streams=2");
                //startInfo.ArgumentList.Add("--skipInference"); --noTF32 --useSpinWait --threads --streams=2 
                if (anyShape)
                {
                    startInfo.ArgumentList.Add("--minShapes=" + minShapes);
                    startInfo.ArgumentList.Add("--optShapes=" + optShapes);
                    startInfo.ArgumentList.Add("--maxShapes=" + maxShapes);
                }

                using var process = new Process { StartInfo = startInfo };
                try
                {
                    process.Start();
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
                {
                    return new TensorRtExportResult(TensorRtExportStatus.TensorRtNotFound,
                        Log: "Не найден trtexec. Установите TensorRT SDK и укажите путь к утилите. " + ex.Message,
                        DeviceIndex: device);
                }

                // Одновременное чтение обоих потоков предотвращает блокировку заполненного pipe.
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { } // Процесс уже завершился.
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    throw;
                }

                log = await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    return Failure($"trtexec завершился с кодом {process.ExitCode}.{Environment.NewLine}{log}", device);
                if (!System.IO.File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
                    return Failure("trtexec не создал непустой engine." + Environment.NewLine + log, device);

                System.IO.File.Move(temporaryPath, outputPath);
                return new TensorRtExportResult(TensorRtExportStatus.Success, outputPath, log, device);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is Win32Exception || ex is ArgumentException || ex is NotSupportedException)
            {
                return Failure(ex.Message + Environment.NewLine + log, device);
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try { System.IO.File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static TensorRtExportResult Failure(string log, int? device)
            => new TensorRtExportResult(TensorRtExportStatus.ExportFailed, Log: log, DeviceIndex: device);
    }
}
