using EyetrackerGlasses.Core;
using EyetrackerGlasses.Helpers.Net.LSL;
using EyetrackerGlasses.Mnn;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using System.Windows.Input;
using TrtDetector.Inference;
using TrtDetector.Models;
using TrtDetector.Visualization;

namespace EyetrackerGlasses.ViewModels
{
    /// <summary>
    /// Модель представления главной страницы.
    /// Служит примером: команды навигации, диалог и запись в журнал.
    /// </summary>
    public class MainPageViewModel : ViewModelBase
    {
        /// <summary>
        /// Переход на страницу настроек.
        /// </summary>
        public ICommand GoToSettingsCommand { get; }

        /// <summary>
        /// Показ диалога — проверка, что <see cref="IUIManager"/> подключён.
        /// </summary>
        public ICommand ShowDialogCommand { get; }


        public ICommand GoToRegistrationPageCommand {  get; }
        public ICommand GoToViewerPageCommand {  get; }
        public ICommand GoToDevicesPageCommand {  get; }
        private string _statusText = string.Empty;

        /// <summary>
        /// Строка состояния под кнопками.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public MainPageViewModel()
        {
            GoToSettingsCommand = new RelayCommand(() => IoC.Application.GoToPage(ApplicationPage.Settings));

            // Асинхронная команда вместо async void: ошибка внутри
            // попадёт в журнал, а не в необработанные исключения.
            ShowDialogCommand = new AsyncRelayCommand(ShowDialogAsync);

            GoToRegistrationPageCommand = new RelayCommand(() => IoC.Application.GoToPage(ApplicationPage.Registation));
        }

        private async Task ShowDialogAsync()
        {
            /*
            string question = AppResources.GetString("DialogQuestionText", "Close this dialog?");

            MessageBoxResult result = await IoC.UI.ShowConfirmAsync(question);

            StatusText = result switch
            {
                MessageBoxResult.Yes => AppResources.GetString("YesText", "Yes"),
                MessageBoxResult.No => AppResources.GetString("NoText", "No"),
                _ => AppResources.GetString("CancelText", "Cancel"),
            };

            IoC.Logger.Log("Диалог закрыт с результатом " + result, LogLevel.Informative);
            */

            /*
            EngineBuildOptions options = new EngineBuildOptions()
            {
                OptBatch=1,
                MinBatch=1,
                MaxBatch=1,
                Height=352
            };

            TrtEngineBuilder.Build("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.onnx",
                "E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.engine", options);
            */

            //Yolo26Options yolo26Options = new Yolo26Options()
            //{
            //     Height = 224,
            //     Width = 640,
            //     Channels = 1,     // 1 для моделей, обученных на grayscale
            //     MaxBatch = 1,     // не больше max профиля engine
            //};
            //Yolo26DetectionParser yolo26DetectionParser = new Yolo26DetectionParser();
            //Yolo26Detector yolo26Detector = new Yolo26Detector("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\gaze_detector_v5_nano_mono_yolov26_b32.engine", yolo26Options, yolo26DetectionParser);

            //using var img = Cv2.ImRead("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\test001.jpg");
            //var results = yolo26Detector.Detect(new[] { img });

            //foreach (var result in results)
            //{
            //    var obj = result.ToObjects(640, 200);
            //}

            //ModelSpec modelSpec = new ModelSpec()
            //{
            //    InputName = "images",
            //    OutputName = "output0",
            //    Channels = 1,          // 1 или 3
            //    Height = 224,
            //    Width = 640,
            //    Outputs = 6,           // 6: cx,cy,w,h,s0,s1; 5: cx,cy,w,h,score
            //    Anchors = null,       // если null — считается по страйдам 8/16/32 (224x640 -> 2940)
            //    MaxBatch = 32
            //};
            //var modelSpec = new ModelSpec(Channels: 3, Height: 352, Width: 640, NumClasses: 1, MaxBatch: 1, FixedBatch: 1);
            //PostprocessOptions postprocessOptions = new PostprocessOptions();

            ////InferenceEngine inferenceEngine = new InferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\gaze_detector_v5_nano_mono_yolov26_b32.engine",
            ////    modelSpec, postprocessOptions);
            //TrtEngineBuilder.Build("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.onnx",
            //    "E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.engine", modelSpec, 1);
            //InferenceEngine inferenceEngine = new InferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.engine",
            //    modelSpec, postprocessOptions);
            //var infData = inferenceEngine.Run(img);

            //Mat imgBB = BBoxDrawer.AddBBox(img, infData);

            //imgBB.ImWrite("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\test001BB.jpg");
            /*
            using var img = Cv2.ImRead("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\test001.jpg");

            //var spec = new ModelSpec(Channels: 3, Height: 352, Width: 640,
            //NumClasses: 1, FixedBatch: 1);
            var spec = new ModelSpec(Channels: 3, Height: 640, Width: 640,
            NumClasses: 80, FixedBatch: 1);
            //var spec = new ModelSpec(Channels: 1, Height: 224, Width: 640,
            //NumClasses: 2, FixedBatch: 1);

            using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\yolov26n.mnn", spec);
            //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.mnn", spec);
            //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\gaze_detector_v5_nano_mono_yolov26_b1.mnn", spec);
            FrameResult result = await Task.Run(() => engine.Run(img));
            Mat imgBB = BBoxDrawer.AddBBox(img, result);

            imgBB.ImWrite("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\test001mnnBByolo.jpg");
            */
            //TensorRtExporter tensorRtExporter = new TensorRtExporter("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\gaze_detector_v5_nano_mono_yolov26_b32.onnx",
            //    "C:\\TensorRT-11.5.0.63\\bin\\trtexec.exe");
            //var res = await tensorRtExporter.RunExport("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\gaze_detector_v5_nano_mono_yolov26_b32.engine",
            //    minShapes: "images:1x1x224x640",
            //    optShapes: "images:16x1x224x640",
            //    maxShapes: "images:32x1x224x640");
            await ScanAsync(CancellationToken.None);
        }

        private async Task ScanAsync(CancellationToken cancellationToken)
        {
            StatusText = "Scanning LSL streams…";
            try
            {
                var scanner = new LslStreamScanner();
                var streams = await scanner.ScanAsync(TimeSpan.FromSeconds(5), cancellationToken);
                var device = GlassesLslStreams.ForDevice("GroupE", "user0");
                var eyeStreams = streams.Where(stream => stream.Name == device.Eyes.Name).ToArray();

                foreach (var stream in streams)
                    System.Diagnostics.Trace.WriteLine(
                        $"{stream.Name} @ {stream.Hostname}: {stream.ChannelCount} x {stream.ChannelFormat}, {stream.NominalSampleRate:g} Hz");

                StatusText = eyeStreams.Length == 0
                    ? $"LSL streams found: {streams.Count}. GroupE.user0 eye video unavailable."
                    : $"LSL streams found: {streams.Count}. Eye video: {eyeStreams[0].Hostname}, {eyeStreams[0].NominalSampleRate:g} Hz.";
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InternalException)
            {
                StatusText = $"LSL scan failed: {ex.Message}";
            }
        }
    }
}
