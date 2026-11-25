Для MNN на GPU нужна **нативная сборка с включённым backend ускорителя**. Сейчас наш `eyetracker_mnn` собран только с CPU. Параметр `preferAutoBackend: true` выбирает среди backend’ов, доступных в сборке, поэтому сейчас он тоже приводит к CPU. [Настройки CMake](/E:/workspace_Avalonia/EyetrackerGlasses/EyetrackerGlasses/native/mnn/CMakeLists.txt:20)

**На вашем Windows я бы начал с OpenCL.** Проверил через OpenCL API: драйвер уже видит **GeForce GTX 1650**. Для первого теста достаточно разрешить `MNN_OPENCL` в CMake, пересобрать мост с `-DMNN_OPENCL=ON` и запустить существующий `StartTarget.mnn` с `preferAutoBackend: true`. Для этого пути дополнительные NuGet-пакеты, CUDA Toolkit и повторный экспорт модели не нужны. Инференс на GPU пока не проверял.

| Платформа | Что включать |
|---|---|
| **Windows / Linux** | OpenCL с драйвером производителя GPU. Для NVIDIA можно отдельно собрать CUDA backend с совместимым CUDA Toolkit. |
| **Android** | Vulkan или OpenCL, если устройство и драйвер предоставляют нужный интерфейс. Для NPU Qualcomm — QNN или Hexagon на совместимых устройствах. |
| **macOS / iOS** | Metal для GPU или Core ML, если нужно дать системе возможность задействовать Neural Engine. Core ML сам выбирает устройство для операций. |

Источники: [поддержка ускорителей в MNN](https://github.com/alibaba/MNN#architecture), [ограничения доступа к библиотекам производителя на Android](https://developer.android.com/guide/topics/manifest/uses-native-library-element).

Для автоматического режима в приложении я бы добавил явный выбор backend и свойство `ActiveBackend`, сохранив переход на CPU при недоступности ускорителя. Часть операторов может выполняться на CPU даже при активном GPU, поэтому проверять стоит и результат детекции, и **время всего `Run(Mat)`** после прогрева. Обработка кадра, копирование тензоров и NMS тоже входят в это время.