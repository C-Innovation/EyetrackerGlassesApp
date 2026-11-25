# MNN для EyetrackerGlasses

Проект `EyetrackerGlasses.Mnn` загружает `.mnn` и принимает один фиксированный FP32-тензор.
`Infer` возвращает сырые именованные FP32-выходы с формами и признаком NHWC. Подготовка кадра
и интерпретация выходов зависят от конкретной YOLO-модели. Поставляемый вариант использует CPU.

```csharp
using EyetrackerGlasses.Mnn;

using var runtime = new MnnYoloRuntime(modelPath, threads: 4);
// Подготовьте input по runtime.InputShape и runtime.InputIsNhwc.
IReadOnlyList<MnnTensor> outputs = await Task.Run(() => runtime.Infer(input));
```

## Инференс `StartTarget` из `Mat`

В общем проекте `EyetrackerGlasses` доступен `MnnInferenceEngine` с привычными
`Run(Mat)`, `RunBatch(...)`, `SubVals`, `DivVals` и `LastMaxScore`. Используются те же
`ModelSpec`, `PreprocessOptions`, `PostprocessOptions` и `FrameResult`, что и в TensorRT.
Обработка `Mat` и NMS требуют нативный OpenCV для целевой платформы. Пример ниже запускается
с уже подключённым в решении Windows runtime OpenCvSharp.

```csharp
using EyetrackerGlasses.Mnn;
using OpenCvSharp;
using TrtDetector.Models;
using TrtDetector.Visualization;

var spec = new ModelSpec(Channels: 3, Height: 352, Width: 640,
    NumClasses: 1, FixedBatch: 1);
var post = new PostprocessOptions { ClassNames = new[] { "target" } };

using var engine = new MnnInferenceEngine("StartTarget.mnn", spec, post);
using var frame = Cv2.ImRead("Models/test001.jpg", ImreadModes.Color);
if (frame.Empty()) throw new InvalidOperationException("Не удалось открыть изображение.");

FrameResult result = await Task.Run(() => engine.Run(frame));
using var preview = BBoxDrawer.AddBBox(frame, result);
Cv2.ImWrite("StartTarget-mnn.jpg", preview);
```

Если у вас уже открыт `MnnYoloRuntime`, используйте преобразователи напрямую.

```csharp
using var runtime = new MnnYoloRuntime("StartTarget.mnn");
var converter = new MnnYoloTensorConverter(runtime, spec, post);
float[] input = converter.PrepareInput(new[] { frame }, out var geometry);
IReadOnlyList<MnnTensor> raw = runtime.Infer(input);
FrameResult result = converter.DecodeOutput(raw, geometry)[0];
```

Второй фрагмент показывает синхронный API. В Avalonia запускайте всю цепочку на рабочем потоке.
Преобразователь использует layout входа, сообщённый MNN (NCHW или NHWC), и сохраняет геометрию
масштабирования для восстановления координат исходного кадра. `safeResize: true` сохраняет
пропорции с заполнением справа и снизу, как в текущем TensorRT API. Укажите `SwapRB = true`
в `PreprocessOptions`, если ваша модель обучалась на RGB.

`ModelSpec.OutputName` должен совпадать с именем выхода в MNN. Для плотных детекторов
поддерживаются `[batch, features, anchors]` и `[batch, anchors, features]` согласно
`ModelSpec.Layout`. Укажите `Anchors`, если число кандидатов не совпадает с расчётом по strides.
Декодер ожидает `cx, cy, w, h`, опциональный objectness и оценки классов. Для модели, которая уже
возвращает готовые рамки после NMS, используйте собственный декодер сырых тензоров.
`RunBatch` использует размер пакета из MNN-модели и заполняет нулями оставшиеся слоты последнего пакета.

На Windows можно запустить пример без добавления кода в приложение. Выполните из каталога
`EyetrackerGlasses`, содержащего проекты и папку `Models`.

```powershell
& ./artifacts/mnn/tools/win-x64/MNNConvert.exe -f ONNX --modelFile Models/StartTarget.onnx --MNNModel StartTarget.mnn
dotnet run --project EyetrackerGlasses.AI.Checks -p:EnableMnnNative=true -- --infer-mnn StartTarget.mnn Models/test001.jpg StartTarget-mnn.jpg
```

Для поставки модели добавьте `-p:MnnModelPath=<абсолютный путь к модели.mnn>` при сборке приложения.
Ресурс имеет URI `avares://EyetrackerGlasses/Assets/Models/Detector.mnn`. Откройте ресурс
через `Avalonia.Platform.AssetLoader.Open` и передайте поток в
`MnnModelFile.CopyToLocalFileAsync` вместе с доступным для записи приватным каталогом приложения.
Функция вернёт путь, который принимает `MnnYoloRuntime`.

## Сборка и поставка

Команды ниже выполняются из каталога `EyetrackerGlasses`, содержащего проекты.
Нужны .NET 10, Git, Python 3.10+, CMake 3.24+ и компилятор C++17.
Скрипт загружает [MNN 3.6.2](https://github.com/alibaba/MNN/releases/tag/3.6.2), проверяет коммит
и сохраняет native-файлы с лицензией и контрольными суммами в `artifacts/mnn/runtimes/<RID>/native`.
Дополнительные NuGet-пакеты для MNN не нужны.

| Цель | RID | Инструменты |
| --- | --- | --- |
| Windows | `win-x64`, `win-arm64` | Visual Studio 2022 C++, соответствующие MSVC tools и Windows SDK |
| Linux | `linux-x64`, `linux-arm64` | Linux/WSL и GCC. Для кросс-сборки ARM64 нужен `g++-aarch64-linux-gnu` |
| macOS | `osx-arm64`, `osx-x64` | macOS с Xcode |
| Android | `android-arm64`, `android-x64` | Android NDK r29, Ninja, OpenJDK 21 и SDK Platform/Build Tools 36 |
| iOS | `ios-arm64`, `iossimulator-arm64`, `iossimulator-x64` | macOS с подходящим для .NET iOS workload Xcode |

Desktop. Подставьте нужный RID в обе команды. Для Linux собирайте на дистрибутиве
с подходящей версией glibc. В CI за базу взят Ubuntu 22.04.

```sh
python native/mnn/build.py --rid win-x64 --converter
dotnet publish EyetrackerGlasses.Desktop -c Release -r win-x64 --self-contained false -p:EnableMnnNative=true
```

`--converter` собирает отдельную утилиту для хоста. Пример конвертации на Windows:

```powershell
& ./artifacts/mnn/tools/win-x64/MNNConvert.exe -f ONNX --modelFile Models/StartTarget.onnx --MNNModel StartTarget.mnn
```

Android. Скрипт принимает `--ndk <каталог NDK>` или переменную `ANDROID_NDK_HOME`.
Обе библиотеки включаются в соответствующие каталоги ABI внутри APK.

```sh
dotnet workload restore EyetrackerGlasses.Android
python native/mnn/build.py --rid android-arm64
python native/mnn/build.py --rid android-x64
dotnet publish EyetrackerGlasses.Android -c Release -p:EnableMnnNative=true -o artifacts/mnn/deploy/android
```

iOS. Сборка приложения связывает `libeyetracker_mnn.a` и `libMNN.a` для указанного RID.
Для установки на iPhone нужны сертификат и provisioning profile проекта.

```sh
dotnet workload restore EyetrackerGlasses.iOS
python native/mnn/build.py --rid iossimulator-arm64
dotnet build EyetrackerGlasses.iOS -c Release -r iossimulator-arm64 -p:EnableMnnNative=true
```

Параметр `EnableMnnNative=true` делает отсутствие бинарников ошибкой сборки.
Обычная сборка подхватывает готовые бинарники автоматически. Матрица в
[`.github/workflows/mnn.yml`](../../../.github/workflows/mnn.yml) собирает библиотеки и приложения.
Запуск workflow требует репозитория с GitHub Actions.

## Проверка

Установите `onnx==1.19.1` в отдельное окружение Python, если нужно сгенерировать тестовый ONNX.
После конвертации тестовой модели проверьте формы, имена и численные выходы через C#.

```sh
python native/mnn/make_fixture.py artifacts/mnn/checks/fixture.onnx
artifacts/mnn/tools/win-x64/MNNConvert.exe -f ONNX --modelFile artifacts/mnn/checks/fixture.onnx --MNNModel artifacts/mnn/checks/fixture.mnn
dotnet run --project EyetrackerGlasses.Mnn.Checks -p:EnableMnnNative=true -- artifacts/mnn/checks/fixture.mnn
```

Для проверки состава APK используйте `python native/mnn/check_apk.py <путь к APK>`.
