# YOLO Ultralytics в Avalonia/.NET 10: ONNX → MNN → кроссплатформенный инференс


## 1. Что входит в эту схему

ONNX используется **только как входной формат для конвертации**. В приложение поставляется сконвертированная модель `.mnn`; в приложении не используется ONNX Runtime.

```text
ONNX
  │ офлайн-конвертация MNNConvert
  ▼
YOLO.mnn
  │
  ├── C# / Avalonia
  │     └── P/Invoke через C ABI
  ▼
Native-мост на C++
  │
  ▼
MNN Runtime
```

Это общий путь для нескольких платформ, но не универсальный TensorRT Engine. Ускорение зависит от версии MNN, сборки runtime, выбранного backend и поддерживаемых устройством операций. Один и тот же файл `.mnn` может работать на разных устройствах, но **одинаковая производительность на них не гарантируется**.

Пример ниже рассчитан на **фиксированный вход FP32** и выдаёт сырые выходные тензоры модели. Он не реализует preprocessing изображения, декодирование YOLO-выходов и NMS: эти части нужно согласовать с задачей модели и параметрами её экспорта.

## 2. NuGet-пакеты

Для вызова native-библиотеки из C# **дополнительный NuGet-пакет не требуется**.

| Компонент | NuGet |
|---|---|
| `DllImport`, `IntPtr`, маршалинг | Не нужен: `System.Runtime.InteropServices` входит в .NET |
| MNN Runtime | Универсальный NuGet для всех целевых платформ здесь не предполагается; его нужно собрать и подключить как native-библиотеку |
| ONNX Runtime | Не устанавливать: эта схема не использует ONNX для инференса |
| Avalonia | Используйте пакеты, уже подключённые в вашем Avalonia-проекте |

Для конвертации понадобится отдельная **native-утилита `MNNConvert`**, но это не NuGet-пакет и не обязательная часть приложения.

## 3. Инструменты сборки

Потребуются:

- CMake;
- C++17-компилятор;
- исходники и сборка MNN для каждой целевой ОС и архитектуры;
- Android NDK для Android;
- Xcode и iOS toolchain для iOS;
- .NET 10 SDK и платформенные workload’ы, необходимые вашему Avalonia-проекту.

Старайтесь собирать `MNNConvert` и runtime на базе одной версии MNN и тестировать их совместно. Нативные библиотеки собираются для конкретных ОС и архитектур — одна сборка `.dll` не заменяет `.so`, `.dylib` или библиотеку для iOS.

## 4. Конвертация ONNX → MNN

Соберите `MNNConvert` из используемой версии MNN. Типовая команда:

```bash
MNNConvert -f ONNX \
  --modelFile best.onnx \
  --MNNModel best.mnn
```

Проверьте синтаксис командной строки в `MNNConvert`, собранном именно у вас. Конвертация зависит от операций в ONNX и возможностей выбранной версии конвертера.

Для первого запуска рекомендуется подготовить модель с:

- фиксированным размером изображения;
- batch `1`;
- FP32;
- без динамических форм, пока базовый вариант не проверен.

Если конвертер сообщает о неподдерживаемых операциях или выходы после конвертации отличаются от исходной модели, нужно разбираться с конкретной моделью и версией конвертера. Сам факт успешного создания `.mnn` ещё не подтверждает корректность результатов.

## 5. C++ C ABI-мост

Мост прячет C++ ABI MNN за обычными функциями C. C# не работает непосредственно с C++-классами.

Создайте `native/yolo_mnn.cpp`:

```cpp
#include <MNN/Interpreter.hpp>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#if defined(_WIN32)
    #define YOLO_API extern "C" __declspec(dllexport)
#else
    #define YOLO_API extern "C" __attribute__((visibility("default")))
#endif

struct Output {
    std::string name;
    MNN::Tensor* tensor = nullptr;
};

struct Context {
    MNN::Interpreter* net = nullptr;
    MNN::Session* session = nullptr;
    MNN::Tensor* input = nullptr;
    std::vector<Output> outputs;
    std::mutex mutex;

    ~Context()
    {
        if (net && session) {
            net->releaseSession(session);
            session = nullptr;
        }

        delete net;
        net = nullptr;
    }
};

static void set_error(char* dst, size_t cap, const char* message)
{
    if (dst && cap > 0) {
        std::snprintf(dst, cap, "%s",
                      message ? message : "Unknown error");
    }
}

static int checked_count(size_t value)
{
    if (value > static_cast<size_t>(0x7fffffff)) {
        return -1;
    }
    return static_cast<int>(value);
}

YOLO_API void* yolo_create(
    const char* model_path,
    int threads,
    int backend,              // 0 = CPU, другое значение = MNN AUTO
    char* error,
    size_t error_capacity)
{
    set_error(error, error_capacity, "");

    try {
        if (!model_path || !model_path[0]) {
            set_error(error, error_capacity, "Model path is empty");
            return nullptr;
        }

        auto ctx = std::make_unique<Context>();

        ctx->net = MNN::Interpreter::createFromFile(model_path);
        if (!ctx->net) {
            set_error(error, error_capacity, "MNN failed to load the model");
            return nullptr;
        }

        MNN::ScheduleConfig config;
        config.numThread = std::max(1, threads);
        config.type = (backend == 0)
            ? MNN_FORWARD_CPU
            : MNN_FORWARD_AUTO;

        ctx->session = ctx->net->createSession(config);
        if (!ctx->session) {
            set_error(error, error_capacity, "MNN failed to create a session");
            return nullptr;
        }

        ctx->input = ctx->net->getSessionInput(ctx->session, nullptr);
        if (!ctx->input) {
            set_error(error, error_capacity, "MNN session has no input tensor");
            return nullptr;
        }

        for (int dim : ctx->input->shape()) {
            if (dim <= 0) {
                set_error(
                    error,
                    error_capacity,
                    "Dynamic or non-positive input dimensions are not supported by this bridge");
                return nullptr;
            }
        }

        return ctx.release();
    }
    catch (...) {
        set_error(error, error_capacity,
                  "Unhandled native exception in yolo_create");
        return nullptr;
    }
}

YOLO_API void yolo_destroy(void* handle)
{
    delete static_cast<Context*>(handle);
}

YOLO_API int yolo_input_rank(void* handle)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx || !ctx->input) {
        return -1;
    }

    return static_cast<int>(ctx->input->shape().size());
}

YOLO_API int yolo_input_dim(void* handle, int axis)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx || !ctx->input) {
        return -1;
    }

    const auto shape = ctx->input->shape();
    if (axis < 0 || axis >= static_cast<int>(shape.size())) {
        return -1;
    }

    return shape[axis];
}

YOLO_API int yolo_input_is_nhwc(void* handle)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx || !ctx->input) {
        return -1;
    }

    return ctx->input->getDimensionType() == MNN::Tensor::TENSORFLOW
        ? 1
        : 0;
}

YOLO_API int yolo_input_element_count(void* handle)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx || !ctx->input) {
        return -1;
    }

    return checked_count(ctx->input->elementSize());
}

YOLO_API int yolo_run(
    void* handle,
    const float* input_data,
    int input_count,
    char* error,
    size_t error_capacity)
{
    set_error(error, error_capacity, "");

    auto* ctx = static_cast<Context*>(handle);
    if (!ctx || !input_data) {
        set_error(error, error_capacity, "Invalid handle or input pointer");
        return -1;
    }

    std::lock_guard<std::mutex> guard(ctx->mutex);

    try {
        const int expected = checked_count(ctx->input->elementSize());
        if (expected < 0 || input_count != expected) {
            set_error(error, error_capacity,
                      "Input element count does not match model input");
            return -1;
        }

        MNN::Tensor host(ctx->input, ctx->input->getDimensionType());
        float* dst = host.host<float>();

        if (!dst) {
            set_error(error, error_capacity,
                      "MNN could not allocate host input tensor");
            return -1;
        }

        std::memcpy(
            dst,
            input_data,
            static_cast<size_t>(input_count) * sizeof(float));

        if (!ctx->input->copyFromHostTensor(&host)) {
            set_error(error, error_capacity,
                      "Failed to copy input tensor to MNN");
            return -1;
        }

        if (ctx->net->runSession(ctx->session) != MNN::NO_ERROR) {
            set_error(error, error_capacity, "MNN inference failed");
            return -1;
        }

        ctx->outputs.clear();
        const auto output_map =
            ctx->net->getSessionOutputAll(ctx->session);

        for (const auto& item : output_map) {
            if (item.second) {
                ctx->outputs.push_back({item.first, item.second});
            }
        }

        if (ctx->outputs.empty()) {
            set_error(error, error_capacity,
                      "MNN session has no output tensors");
            return -1;
        }

        return 0;
    }
    catch (...) {
        set_error(error, error_capacity,
                  "Unhandled native exception in yolo_run");
        return -1;
    }
}

YOLO_API int yolo_output_count(void* handle)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx) {
        return -1;
    }

    std::lock_guard<std::mutex> guard(ctx->mutex);
    return static_cast<int>(ctx->outputs.size());
}

YOLO_API int yolo_output_rank(void* handle, int index)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx) {
        return -1;
    }

    std::lock_guard<std::mutex> guard(ctx->mutex);

    if (index < 0 || index >= static_cast<int>(ctx->outputs.size())) {
        return -1;
    }

    return static_cast<int>(
        ctx->outputs[index].tensor->shape().size());
}

YOLO_API int yolo_output_dim(void* handle, int index, int axis)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx) {
        return -1;
    }

    std::lock_guard<std::mutex> guard(ctx->mutex);

    if (index < 0 || index >= static_cast<int>(ctx->outputs.size())) {
        return -1;
    }

    const auto shape = ctx->outputs[index].tensor->shape();
    if (axis < 0 || axis >= static_cast<int>(shape.size())) {
        return -1;
    }

    return shape[axis];
}

YOLO_API int yolo_output_element_count(void* handle, int index)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx) {
        return -1;
    }

    std::lock_guard<std::mutex> guard(ctx->mutex);

    if (index < 0 || index >= static_cast<int>(ctx->outputs.size())) {
        return -1;
    }

    return checked_count(ctx->outputs[index].tensor->elementSize());
}

YOLO_API int yolo_output_copy(
    void* handle,
    int index,
    float* destination,
    int destination_count)
{
    auto* ctx = static_cast<Context*>(handle);
    if (!ctx || !destination) {
        return -1;
    }

    std::lock_guard<std::mutex> guard(ctx->mutex);

    if (index < 0 || index >= static_cast<int>(ctx->outputs.size())) {
        return -1;
    }

    MNN::Tensor* output = ctx->outputs[index].tensor;
    const int expected = checked_count(output->elementSize());

    if (expected < 0 || destination_count != expected) {
        return -1;
    }

    MNN::Tensor host(output, output->getDimensionType());
    if (!output->copyToHostTensor(&host)) {
        return -1;
    }

    const float* src = host.host<float>();
    if (!src) {
        return -1;
    }

    std::memcpy(
        destination,
        src,
        static_cast<size_t>(expected) * sizeof(float));

    return 0;
}
```

### Ограничения этого моста

- Вход и выход предполагаются типа FP32.
- Входные размеры должны быть фиксированными.
- `MNN_FORWARD_AUTO` не гарантирует запуск на GPU. Соответствующий backend должен быть собран, поддерживаться устройством и моделью.
- Метод `yolo_input_is_nhwc` сообщает тип разметки MNN-тензора; проверьте его вместе с формой входа и ожидаемым форматом модели.
- Если нужна модель FP16 или INT8, потребуется отдельно реализовать передачу данных подходящего типа.
- Вызовы `copyToHostTensor`/`copyFromHostTensor`, enum backend’ов и прочие сигнатуры необходимо сверить с конкретной версией MNN.

## 6. CMake для моста

Создайте `native/CMakeLists.txt`:

```cmake
cmake_minimum_required(VERSION 3.20)
project(speshu_yolo LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(MNN_ROOT "" CACHE PATH "Path to MNN installation")

find_path(MNN_INCLUDE_DIR
    NAMES MNN/Interpreter.hpp
    HINTS "${MNN_ROOT}/include"
    REQUIRED)

find_library(MNN_LIBRARY
    NAMES MNN
    HINTS "${MNN_ROOT}/lib" "${MNN_ROOT}/lib64"
    REQUIRED)

set(YOLO_LIBRARY_TYPE SHARED)
if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
    set(YOLO_LIBRARY_TYPE STATIC)
endif()

add_library(speshu_yolo ${YOLO_LIBRARY_TYPE}
    yolo_mnn.cpp)

target_include_directories(speshu_yolo PRIVATE
    "${MNN_INCLUDE_DIR}")

target_link_libraries(speshu_yolo PRIVATE
    "${MNN_LIBRARY}")
```

Типовая сборка для настольной платформы, если MNN уже установлен в каталог:

```bash
cmake -S native -B build \
  -DMNN_ROOT=/path/to/mnn/install \
  -DCMAKE_BUILD_TYPE=Release

cmake --build build --config Release
```

Это общий пример, а не полная инструкция сборки MNN для каждого toolchain. MNN может требовать дополнительных библиотек и параметров линковки, особенно при статической сборке и включении ускорителей.

## 7. C#-обёртка

В проекте добавьте:

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

Создайте `MnnYoloRuntime.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed record MnnTensor(int[] Shape, float[] Values);

public sealed class MnnYoloRuntime : IDisposable
{
#if IOS
    private const string NativeLibraryName = "__Internal";
#else
    private const string NativeLibraryName = "speshu_yolo";
#endif

    private const int ErrorCapacity = 1024;

    private IntPtr _handle;
    private readonly object _gate = new();

    public int[] InputShape { get; }
    public bool InputIsNhwc { get; }
    public int InputElementCount { get; }

    public MnnYoloRuntime(
        string modelPath,
        int threads = 4,
        bool preferAutoBackend = true)
    {
        unsafe
        {
            byte* error = stackalloc byte[ErrorCapacity];
            new Span<byte>(error, ErrorCapacity).Clear();

            _handle = Native.Create(
                modelPath,
                threads,
                preferAutoBackend ? 1 : 0,
                error,
                (nuint)ErrorCapacity);

            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException(ReadError(error));
        }

        int rank = Native.InputRank(_handle);
        if (rank <= 0)
        {
            Dispose();
            throw new InvalidOperationException(
                "MNN вернул неверный ранг входного тензора.");
        }

        InputShape = new int[rank];

        for (int i = 0; i < rank; i++)
        {
            InputShape[i] = Native.InputDim(_handle, i);

            if (InputShape[i] <= 0)
            {
                Dispose();
                throw new InvalidOperationException(
                    "Модель должна иметь фиксированные размеры входа.");
            }
        }

        InputIsNhwc = Native.InputIsNhwc(_handle) == 1;
        InputElementCount = Native.InputElementCount(_handle);

        if (InputElementCount <= 0)
        {
            Dispose();
            throw new InvalidOperationException(
                "Неверный размер входного тензора.");
        }
    }

    public IReadOnlyList<MnnTensor> Infer(float[] input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Length != InputElementCount)
        {
            throw new ArgumentException(
                $"Ожидалось {InputElementCount} значений, " +
                $"получено {input.Length}.",
                nameof(input));
        }

        lock (_gate)
        {
            ThrowIfDisposed();

            unsafe
            {
                byte* error = stackalloc byte[ErrorCapacity];
                new Span<byte>(error, ErrorCapacity).Clear();

                fixed (float* inputPtr = input)
                {
                    int result = Native.Run(
                        _handle,
                        inputPtr,
                        input.Length,
                        error,
                        (nuint)ErrorCapacity);

                    if (result != 0)
                        throw new InvalidOperationException(ReadError(error));
                }
            }

            int outputCount = Native.OutputCount(_handle);
            if (outputCount <= 0)
            {
                throw new InvalidOperationException(
                    "У модели не найдены выходные тензоры.");
            }

            var resultTensors = new List<MnnTensor>(outputCount);

            for (int outputIndex = 0;
                 outputIndex < outputCount;
                 outputIndex++)
            {
                int rank = Native.OutputRank(_handle, outputIndex);
                if (rank < 0)
                {
                    throw new InvalidOperationException(
                        "Не удалось получить ранг выходного тензора.");
                }

                var shape = new int[rank];

                for (int axis = 0; axis < rank; axis++)
                {
                    shape[axis] = Native.OutputDim(
                        _handle,
                        outputIndex,
                        axis);

                    if (shape[axis] <= 0)
                    {
                        throw new InvalidOperationException(
                            "Неверная форма выходного тензора.");
                    }
                }

                int count = Native.OutputElementCount(
                    _handle,
                    outputIndex);

                if (count < 0)
                {
                    throw new InvalidOperationException(
                        "Неверный размер выходного тензора.");
                }

                var values = new float[count];

                unsafe
                {
                    fixed (float* destination = values)
                    {
                        int copied = Native.OutputCopy(
                            _handle,
                            outputIndex,
                            destination,
                            values.Length);

                        if (copied != 0)
                        {
                            throw new InvalidOperationException(
                                "Не удалось скопировать выход MNN.");
                        }
                    }
                }

                resultTensors.Add(new MnnTensor(shape, values));
            }

            return resultTensors;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(MnnYoloRuntime));
        }
    }

    private static unsafe string ReadError(byte* error)
    {
        int length = 0;

        while (length < ErrorCapacity && error[length] != 0)
            length++;

        return length == 0
            ? "Ошибка native MNN-моста без подробного сообщения."
            : Encoding.UTF8.GetString(
                new ReadOnlySpan<byte>(error, length));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
                return;

            Native.Destroy(_handle);
            _handle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    ~MnnYoloRuntime()
    {
        Dispose();
    }

    private static class Native
    {
        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_create",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe IntPtr Create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
            int threads,
            int backend,
            byte* error,
            nuint errorCapacity);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr handle);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_input_rank",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int InputRank(IntPtr handle);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_input_dim",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int InputDim(IntPtr handle, int axis);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_input_is_nhwc",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int InputIsNhwc(IntPtr handle);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_input_element_count",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int InputElementCount(IntPtr handle);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_run",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int Run(
            IntPtr handle,
            float* input,
            int inputCount,
            byte* error,
            nuint errorCapacity);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_output_count",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputCount(IntPtr handle);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_output_rank",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputRank(IntPtr handle, int index);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_output_dim",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputDim(
            IntPtr handle,
            int index,
            int axis);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_output_element_count",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputElementCount(
            IntPtr handle,
            int index);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "yolo_output_copy",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int OutputCopy(
            IntPtr handle,
            int index,
            float* destination,
            int destinationCount);
    }
}
```

Пример вызова:

```csharp
using var runtime = new MnnYoloRuntime("best.mnn");

Console.WriteLine(
    $"Input shape: {string.Join("x", runtime.InputShape)}");

Console.WriteLine($"NHWC: {runtime.InputIsNhwc}");

// Подготовьте тензор в форме и формате,
// ожидаемых моделью.
float[] input = new float[runtime.InputElementCount];

IReadOnlyList<MnnTensor> outputs =
    await Task.Run(() => runtime.Infer(input));
```

`Infer` синхронный, поэтому в UI-потоке Avalonia запускать его не следует. В примере он вызывается через `Task.Run`. Один экземпляр runtime сериализует вызовы; если нужны параллельные сессии, создавайте отдельные экземпляры и проверяйте расход памяти и производительность.

## 8. Preprocessing и модель

До вызова `Infer` приложение должно подготовить входной тензор так же, как ожидает экспортированная модель:

- resize или letterbox;
- правильный порядок каналов;
- нормализация значений;
- layout NCHW или NHWC;
- правильное число каналов и batch size.

Не предполагайте автоматически, что любая YOLO-модель принимает RGB NCHW с тремя каналами. Сверьте `InputShape`, `InputIsNhwc` и preprocessing исходного экспорта. Например, модель, обученная на одноканальных изображениях, требует соответствующей формы входа и данных.

Выходы в примере — **сырые тензоры**, а не готовые bounding boxes. Интерпретация зависит от задачи (detect, segment, pose), версии Ultralytics, параметров экспорта и того, включён ли NMS в модель. Сначала выведите формы `outputs[i].Shape`, затем реализуйте декодирование для конкретной модели.

## 9. Упаковка `.mnn` в приложение Avalonia

MNN загружает модель из файла. Если модель включена в приложение как ресурс, скопируйте её в локальный файл перед созданием runtime.

Например, ресурс в `.csproj`:

```xml
<ItemGroup>
  <AvaloniaResource Include="Assets\best.mnn" />
</ItemGroup>
```

Копирование в локальный каталог:

```csharp
using Avalonia.Platform;
using System;
using System.IO;
using System.Threading.Tasks;

public static class ModelFile
{
    public static async Task<string> CopyToLocalFileAsync()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "MyApp");

        Directory.CreateDirectory(directory);

        string destination = Path.Combine(directory, "best.mnn");

        if (File.Exists(destination))
            return destination;

        await using var source = AssetLoader.Open(
            new Uri("avares://MyApp/Assets/best.mnn"));

        await using var target = File.Create(destination);
        await source.CopyToAsync(target);

        return destination;
    }
}
```

Замените `MyApp` на идентификатор вашей сборки. Для больших моделей можно предпочесть обычный файл приложения, а не ресурс в сборке. На мобильных системах модель также должна быть доступна нативному коду по обычному файловому пути.

## 10. Размещение native-библиотек

| Платформа | Что требуется |
|---|---|
| Windows | `speshu_yolo.dll` и необходимые библиотеки MNN для нужной архитектуры |
| Linux | `libspeshu_yolo.so` и зависимости MNN |
| macOS | `libspeshu_yolo.dylib` и зависимости MNN |
| Android | `.so`-библиотеки для каждой ABI, например `arm64-v8a` |
| iOS | Статически собранные мост и MNN, связанные с приложением |

В настольной сборке мост и зависимости обычно копируют рядом с приложением и выбирают соответствующие .NET RID, например `win-x64`, `linux-x64`, `osx-arm64`. Для Android и iOS дополнительно нужна нативная интеграция в пакет приложения. Конкретные элементы `.csproj` зависят от созданного Avalonia-проекта и его платформенной конфигурации.

Для iOS в C# обычно используется:

```csharp
#if IOS
private const string NativeLibraryName = "__Internal";
#endif
```

При статической линковке проверьте, что линкер сохраняет экспортируемые функции C ABI и что библиотека действительно включена в приложение. Нельзя просто скопировать произвольную `.dylib` рядом с iOS-приложением и ожидать, что она будет загружена как на desktop.

## 11. Проверка перед использованием

1. Конвертируйте ONNX в `.mnn` и проверьте лог `MNNConvert`.
2. Убедитесь, что native runtime открывает `.mnn` на целевой платформе.
3. Выведите `InputShape`, layout входа, число и формы выходных тензоров.
4. Подайте одинаковые изображения в исходную модель и MNN, сравните выходы и конечные детекции.
5. Проверьте preprocessing, включая resize/letterbox, каналы и нормализацию.
6. Измерьте производительность на каждой платформе и убедитесь, какой backend действительно используется.
7. Отдельно проверьте CPU fallback, чтобы приложение могло работать без ускорителя.
8. Проверьте упаковку и загрузку native-зависимостей в реальных APK, iOS-приложении и desktop-сборках.

## 12. Что не включено

- Готовая сборка MNN под все ОС и архитектуры: она зависит от toolchain и настроек backend.
- Код preprocessing изображения.
- Декодер выходов YOLO и NMS.
- Поддержка динамических размеров, FP16 и INT8 в C++-мосте.
- Конкретные инструкции включения библиотек в ваш `.csproj`: для этого нужны структура проекта и выбранные платформенные TFM/RID.

Приведённый код — основа интеграции, а не собранный и проверенный проект: он не компилировался против вашей версии MNN и не запускался на вашей модели. Перед использованием проверьте сигнатуры API по заголовкам той версии MNN, с которой будете собирать runtime.