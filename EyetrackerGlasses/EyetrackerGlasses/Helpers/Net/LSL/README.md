# Приём LSL от Eyetracker Glasses

На Windows x64 сборка Desktop и консольного примера копирует нативную библиотеку под именем `lsl.dll` рядом с приложением, в том числе при публикации. Для текущей структуры рабочих папок MSBuild находит `liblsl64.dll` в соседнем проекте `workspace_Qt/glasses_pc_viewer`. При другой структуре положите библиотеку в `native/lsl/win-x64/lsl.dll` относительно корня репозитория или задайте путь через `LslNativeLibraryPath`. Для сборки из Visual Studio эту переменную окружения задайте до запуска IDE. Для видео OpenCvSharp подключается через NuGet проекта.

## Запускаемый пример

Проект [EyetrackerGlasses.Lsl.Examples](../../../../EyetrackerGlasses.Lsl.Examples/Program.cs) поддерживает два режима. Запускайте команды из родительской папки `EyetrackerGlasses.Lsl.Examples`. В переменной `$lsl` укажите путь к Windows x64 DLL из C++ проекта. Сборка копирует её как `lsl.dll` рядом с программой.

```powershell
$example = '.\EyetrackerGlasses.Lsl.Examples\EyetrackerGlasses.Lsl.Examples.csproj'
$lsl = 'E:\workspace_Qt\glasses_pc_viewer\lsl\lsl_lib\liblsl64.dll'

dotnet run --project $example "-p:LslNativeLibraryPath=$lsl" -- scan 'GroupE.user0-glasses.'
dotnet run --project $example "-p:LslNativeLibraryPath=$lsl" -- receive GroupE user0 --gps
```

`scan` собирает объявления в течение пяти секунд. Без префикса команда выводит все видимые LSL-потоки. `receive` читает данные до 20 секунд или до Ctrl+C. Он выводит состояния подключения, типы кадров и итоговые счётчики. Флаг `--gps` включает GPS. Для прямого обнаружения по IP используйте `LSLAPICFG`, как показано ниже.

В проверке с реальным прибором `GroupE.user0` на `10.0.0.80` обнаружились семь потоков. Приёмник выдал 439 кадров глаз на частоте источника 25 Гц. Во всех 439 записях были данные взгляда и кадр сцены, в 415 был GPS. Отдельно получено 38 отсчётов STATUS. Счётчик сцены включает повторное использование кадров. IMU прибор не объявил, а поток `scientific` виден в сканере.

## Найти доступные потоки

Сканер возвращает снимок результатов обнаружения. Частота 0 означает нерегулярный поток. `Hostname` можно передать в `ForDevice`, если в сети несколько приборов с одинаковыми Group и User.

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EyetrackerGlasses.Helpers.Net.LSL;

static async Task ScanAsync(CancellationToken cancellationToken)
{
    var scanner = new LslStreamScanner();
    var streams = await scanner.ScanAsync(TimeSpan.FromSeconds(3), cancellationToken);

    foreach (var stream in streams)
    {
        Console.WriteLine(
            $"{stream.Name} @ {stream.Hostname}: {stream.Type}, " +
            $"{stream.ChannelCount} × {stream.ChannelFormat}, {stream.NominalSampleRate:g} Hz");
    }

    var device = GlassesLslStreams.ForDevice("GroupE", "user0");
    var eyeStreams = streams.Where(s => s.Name == device.Eyes.Name);
    foreach (var stream in eyeStreams)
        Console.WriteLine($"Камеры глаз GroupE.user0: {stream.Hostname}");
}
```

## Получать синхронизированные кадры

Передайте токен окна, службы или консольного приложения. Пример завершает приём по отмене и показывает асинхронные STATUS и IMU. Для GPS передайте `enableGps: true`, если прибор публикует этот поток.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using EyetrackerGlasses.Helpers.Net.LSL;

static async Task ReceiveAsync(CancellationToken cancellationToken)
{
    var streams = GlassesLslStreams.ForDevice("GroupE", "user0");
    using var synchronizer = new GlassesStreamSynchronizer(enableGps: false);
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var receiver = new GlassesLslReceiver(streams, synchronizer);
    receiver.ConnectionStateChanged += (name, state) => Console.WriteLine($"{name}: {state}");

    synchronizer.StatusReceived += sample =>
        Console.WriteLine($"Battery: {sample.Value.BatteryPercent:g}%");
    synchronizer.ImuReceived += sample =>
    {
        // Gyroscope, Accelerometer и Magnetometer доступны в sample.Value.
        // Для обновления интерфейса Avalonia используйте Dispatcher.UIThread.Post.
    };

    Task reception = receiver.RunAsync(stop.Token);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));

    try
    {
        while (!reception.IsCompleted && await timer.WaitForNextTickAsync(stop.Token))
        {
            while (synchronizer.TryDequeue(out var frame))
            {
                using (frame)
                {
                    var eyes = frame.Eyes.Value;         // Mat CV_8UC1
                    var scene = frame.Scene?.Value;      // Mat CV_8UC3 (BGR) или null
                    var gaze = frame.Parameters?.Value;
                    double timestamp = frame.Eyes.Timestamp;
                    if (frame.EventCode != -1)
                        Console.WriteLine($"Event {frame.EventCode} at {frame.EventTimestamp:F6}");

                    // Обработайте Mat здесь. Вызовите Clone(), если кадр нужен после Dispose().
                }
            }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }
    finally
    {
        await stop.CancelAsync();
        try { await reception; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
```

`Drain()` возвращает массив всех готовых кадров в порядке от старого к новому. При использовании этого метода вызывайте `Dispose()` для каждого возвращённого элемента. Таймштампы выражены в секундах по локальным часам LSL, после коррекции часов источника. Появление одного потока в результатах сканирования подтверждает обнаружение, а поступление отсчётов проверяется отдельным запуском приёмника.

## Обнаружение по известному IP

В Windows-версии Avalonia файл `EyetrackerGlasses.Desktop/lsl_api.cfg` копируется рядом с приложением и автоматически загружается при старте. Сейчас в нём задан адрес прибора `10.0.0.80`. Если адрес изменится, отредактируйте файл конфигурации и перезапустите приложение. Явно заданная до запуска переменная `LSLAPICFG` имеет приоритет.

Для консольного примера можно создать свой файл `lsl_api.cfg` со следующим содержимым.

```ini
[lab]
KnownPeers = {10.0.0.80}
```

Перед запуском приложения укажите путь к этому файлу в PowerShell. Запускайте приложение из того же окна, чтобы оно унаследовало переменную. Затем выполните `ScanAsync()` и проверьте, что в списке появились потоки с префиксом `GroupE.user0-glasses.`.

```powershell
$env:LSLAPICFG = (Resolve-Path .\lsl_api.cfg).Path
```

При подключении `ConnectionStateChanged` сообщает состояние отдельно для каждого потока. `NotFound` означает, что приёмник пока не обнаружил поток по имени. `OpenTimedOut` означает, что поток найден, но не открылся вовремя. `ClockSyncTimedOut` означает, что поток открылся, но не получена поправка часов. `Ready` означает готовность приёмника к чтению. Факт получения данных проверяйте по кадрам и событиям синхронизатора.
