# Экспорт ONNX → TensorRT engine

`TensorRtExporter` находится в пространстве имен `EyetrackerGlasses.Core`.
Дополнительные NuGet- и Python-пакеты не нужны.

## Компоненты для экспорта

- 64-разрядные Windows/Linux, NVIDIA GPU с compute capability **7.5 или выше**.
- NVIDIA-драйвер, совместимый с выбранной версией CUDA.
- **NVIDIA TensorRT SDK 10.13.x**: полный ZIP для Windows / tar или системный пакет для Linux,
  содержащий `trtexec`. Одна установка `pip install tensorrt` эту утилиту не предоставляет.
- CUDA runtime-библиотеки для выбранной сборки TensorRT. Для данной реализации используйте
  сборку TensorRT 10.13.x для **CUDA 12.x** (в частности, для GTX 1650).
  Версии CUDA/драйвера сверяйте с матрицей NVIDIA для конкретного архива SDK.

Добавьте каталог с `trtexec` и каталоги с DLL TensorRT/CUDA в `PATH` (Linux: соответствующие
каталоги библиотек в `LD_LIBRARY_PATH`) до запуска приложения. Либо передайте полный путь
к `trtexec` вторым аргументом конструктора; его библиотеки всё равно должны быть доступны загрузчику.
Класс не устанавливает компоненты автоматически и не проверяет номер версии SDK.
Другие версии TensorRT требуют отдельной проверки совместимости.

Документация NVIDIA:
- https://docs.nvidia.com/deeplearning/tensorrt/10.x.x/getting-started/support-matrix.html
- https://docs.nvidia.com/deeplearning/tensorrt/10.x.x/getting-started/quick-start-guide.html

## Использование

```csharp
using EyetrackerGlasses.Core;

var exporter = new TensorRtExporter(@"D:\Models\model.onnx");
// Если trtexec отсутствует в PATH:
// var exporter = new TensorRtExporter(@"D:\Models\model.onnx", @"D:\TensorRT\bin\trtexec.exe");

TensorRtExportResult result = await exporter.RunExport();
switch (result.Status)
{
    case TensorRtExportStatus.Success:
        Console.WriteLine(result.EnginePath); // D:\Models\model.engine
        break;
    case TensorRtExportStatus.NoAvaliableGPU:
        Console.WriteLine("Нет доступного поддерживаемого GPU. " + result.Log);
        break;
    default:
        Console.WriteLine(result.Status + ": " + result.Log);
        break;
}
```

`RunExport` асинхронен: вызывайте через `await`, чтобы не блокировать UI. Вначале через CUDA Driver API
проверяются видимые GPU, compute capability и возможность создать CUDA-контекст. Выбирается первый
подходящий GPU, его индекс передается в `trtexec --device`. `CUDA_VISIBLE_DEVICES` учитывается CUDA-драйвером;
не изменяйте эту переменную после инициализации CUDA в приложении.
При отсутствии подходящего GPU возвращается `NoAvaliableGPU`: ONNX не открывается, trtexec не запускается.
Это проверка доступности устройства; совместимость драйвера с CUDA runtime конкретного trtexec,
достаточность видеопамяти для модели и поддержка её операторов окончательно проверяются при сборке.
Такие ошибки возвращаются как `ExportFailed` с журналом утилиты.

Выходной путь можно задать через `enginePath`. Существующий engine не перезаписывается.
При ошибке или отмене временный файл удаляется; при отмене выбрасывается `OperationCanceledException`.
Экспорт не запускает бенчмарк (`--skipInference`). Точность и прочие настройки сборки — стандартные для SDK.
ONNX с external data должен оставаться рядом со своими файлами весов.

Для модели с динамическими входами задайте все три профиля, используя имена её входов:

```csharp
var result = await exporter.RunExport(
    cancellationToken: cancellationToken,
    minShapes: "images:1x3x640x640",
    optShapes: "images:4x3x640x640",
    maxShapes: "images:8x3x640x640");
```

Engine следует собирать на целевой машине: совместимость с другими GPU, ОС и версиями
TensorRT по умолчанию не гарантируется.

## Проверки

Из корня решения:

```powershell
dotnet run --project EyetrackerGlasses/EyetrackerGlasses.Core.ExportChecks
```

Проверки без дополнительных тестовых пакетов: отсутствие GPU (в отдельном процессе), отсутствие trtexec,
пути с пробелами, успех/ошибки дочернего процесса, чтение обоих потоков, пустой engine, динамические
профили, запрет перезаписи, отмена и очистка временных файлов. Ветки дочернего процесса проверяются
с заменой trtexec и требуют доступного GPU; на машине без GPU они пропускаются. Это не проверка
настоящей сборки TensorRT: для неё нужны установленный SDK и реальная ONNX-модель.
