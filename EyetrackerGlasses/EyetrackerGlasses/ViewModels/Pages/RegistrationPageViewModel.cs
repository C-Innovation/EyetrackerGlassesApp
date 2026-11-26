using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EyetrackerGlasses.Core;
using EyetrackerGlasses.Helpers.Net.LSL;
using EyetrackerGlasses.Helpers.Net.Rpc;
using EyetrackerGlasses.Mnn;
using EyetrackerGlasses.ValueConverters;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TrtDetector.Models;
using TrtDetector.Visualization;

namespace EyetrackerGlasses.ViewModels;

public sealed class RegistrationPageViewModel : ViewModelBase
{
    private enum SettingsMenu
    {
        None,
        Registration,
        Display
    }

    private const double ReferenceVideoWidth = 640;
    private const double SceneStandardHeight = 480;
    private const double SceneWideHeight = 352;
    private const double SceneFormatThreshold = (SceneStandardHeight + SceneWideHeight) / (2 * ReferenceVideoWidth);

    private SettingsMenu _menu;
    private double _sceneHeightRatio = SceneStandardHeight / ReferenceVideoWidth;
    private WriteableBitmap? _eyesImage;
    private WriteableBitmap? _sceneImage;
    private bool _disposed;

    private MnnInferenceEngine _EyesMNN;
    private List<MnnInferenceEngine> _SceneCustomMNN;


    private EyetrackerDevices _Devices;
    private EyeTrackerRpcClient _EyeTrackerRpcClient;
    #region MVVM Properties

    private bool _IsRegistrationStoped = true;

    public bool IsRegistrationStoped
    {
        get => _IsRegistrationStoped;
        set
        {
            if (_IsRegistrationStoped != value)
            {

                _IsRegistrationStoped = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _IsAwaitScreenVisible = false;

    public bool IsAwaitScreenVisible
    {
        get => _IsAwaitScreenVisible;
        set
        {
            if (_IsAwaitScreenVisible != value)
            {
                _IsAwaitScreenVisible = value;
                OnPropertyChanged();
            }
        }
    }

    private string _AwaitScreenText = string.Empty;

    public string AwaitScreenText
    {
        get => _AwaitScreenText;
        set
        {
            if (_AwaitScreenText != value)
            {
                _AwaitScreenText = value;
                OnPropertyChanged();
            }
        }
    }

    private string _ActiveDeviceName = string.Empty;

    public string ActiveDeviceName
    {
        get => _ActiveDeviceName;
        set
        {
            if (_ActiveDeviceName != value)
            {
                _ActiveDeviceName = value;
                OnPropertyChanged();
            }
        }
    }

    private ObservableCollection<string> _DeviceHostNames;

    public ObservableCollection<string> DeviceHostNames
    {
        get => _DeviceHostNames;
        set => SetProperty(ref _DeviceHostNames, value);
    }

    private int _DeviceSelectedIndex = 0;

    public int DeviceSelectedIndex
    {
        get => _DeviceSelectedIndex;
        set
        {
            _DeviceSelectedIndex = value;
            OnPropertyChanged();
        }
    }


    #endregion


    Mat img1;
    Mat img2;

    public RegistrationPageViewModel()
    {
        IsSingleView = Application.Current?.ApplicationLifetime
            is ISingleViewApplicationLifetime or IActivityApplicationLifetime;
        GoHomeCommand = new RelayCommand(() => IoC.Application.GoToPage(ApplicationPage.Main));
        ShowRegistrationSettingsCommand = new RelayCommand(() => SetMenu(SettingsMenu.Registration));
        ShowDisplaySettingsCommand = new RelayCommand(() => SetMenu(SettingsMenu.Display));
        HideSettingsCommand = new RelayCommand(() => SetMenu(SettingsMenu.None));
        StartStopRegistrationCommand = new AsyncRelayCommand(StartStopRegistrationCommandAction);
        EyesCentersShowCommand = new AsyncRelayParameterizedCommand(EyesCentersShowCommandActionAsync);
        TargetsCentersShowCommand = new AsyncRelayParameterizedCommand(TargetsCentersShowCommandActionAsync);
        ConnectToRegistratorCommand = new AsyncRelayCommand(ConnectToRegistratorCommandActionAsync);


        _SceneCustomMNN = new List<MnnInferenceEngine>();
        _Devices = AppConfig.GetInstance().Devices;
        List<string> devices = new List<string>();
        foreach (var device in _Devices.Devices)
        {
            devices.Add(device.HostName);
        }
        DeviceHostNames = new ObservableCollection<string>(devices);
        DeviceSelectedIndex = 0;
        

        img1 = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test003.jpg"));
        img2 = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test001.jpg"));

        UpdateVideoFramesAsync(img1, img2);
        //TestLoad();
    }

    

    public bool IsSingleView { get; }
    public double MinimumVideoWidth => IsSingleView ? 0 : ReferenceVideoWidth / 2;
    public double EyesHeightRatio => 200 / ReferenceVideoWidth;
    public double SceneHeightRatio => _sceneHeightRatio;
    public bool IsRegistrationSettingsOpen => _menu == SettingsMenu.Registration;
    public bool IsDisplaySettingsOpen => _menu == SettingsMenu.Display;
    public bool IsSettingsOpen => _menu != SettingsMenu.None;
    public WriteableBitmap? EyesImage => _eyesImage;
    public WriteableBitmap? SceneImage => _sceneImage;

    public ICommand GoHomeCommand { get; }
    public ICommand ShowRegistrationSettingsCommand { get; }
    public ICommand ShowDisplaySettingsCommand { get; }
    public ICommand HideSettingsCommand { get; }
    public ICommand StartStopRegistrationCommand { get; }
    public ICommand EyesCentersShowCommand { get; }
    public ICommand TargetsCentersShowCommand { get; }
    public ICommand ConnectToRegistratorCommand { get; }

    private void SetMenu(SettingsMenu menu)
    {
        if (_menu == menu)
            return;

        _menu = menu;
        OnPropertyChanged(nameof(IsRegistrationSettingsOpen));
        OnPropertyChanged(nameof(IsDisplaySettingsOpen));
        OnPropertyChanged(nameof(IsSettingsOpen));
    }

    /// <summary>
    /// Copies frames into images owned by the view model. Keep the Mats valid until the task
    /// completes. A null frame clears its image. Callers retain ownership of both Mats.
    /// </summary>
    public async Task UpdateVideoFramesAsync(Mat? eyes, Mat? scene)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
                return;

            WriteableBitmap? eyesImage = eyes is null ? null : MatBitmapConverter.Convert(eyes);
            WriteableBitmap? sceneImage;
            try
            {
                sceneImage = scene is null ? null : MatBitmapConverter.Convert(scene);
            }
            catch
            {
                eyesImage?.Dispose();
                throw;
            }

            if (sceneImage is not null)
            {
                var size = sceneImage.PixelSize;
                double height = (double)size.Height / size.Width <= SceneFormatThreshold
                    ? SceneWideHeight : SceneStandardHeight;
                SetProperty(ref _sceneHeightRatio, height / ReferenceVideoWidth, nameof(SceneHeightRatio));
            }

            ReplaceImage(ref _eyesImage, eyesImage, nameof(EyesImage));
            ReplaceImage(ref _sceneImage, sceneImage, nameof(SceneImage));
        });
    }

    public async Task UpdateEyesVideoFrameAsync(Mat? eyes)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
                return;

            WriteableBitmap? eyesImage = eyes is null ? null : MatBitmapConverter.Convert(eyes);
            try
            {
                
            }
            catch
            {
                eyesImage?.Dispose();
                throw;
            }

            ReplaceImage(ref _eyesImage, eyesImage, nameof(EyesImage));
            
        });
    }

    public async Task UpdateSceneVideoFrameAsync(Mat? scene)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed)
                return;

            WriteableBitmap? sceneImage;
            try
            {
                sceneImage = scene is null ? null : MatBitmapConverter.Convert(scene);
            }
            catch
            {
                
                throw;
            }

            if (sceneImage is not null)
            {
                var size = sceneImage.PixelSize;
                double height = (double)size.Height / size.Width <= SceneFormatThreshold
                    ? SceneWideHeight : SceneStandardHeight;
                SetProperty(ref _sceneHeightRatio, height / ReferenceVideoWidth, nameof(SceneHeightRatio));
            }

            ReplaceImage(ref _sceneImage, sceneImage, nameof(SceneImage));
        });
    }

    private async Task StartStopRegistrationCommandAction()
    {
        IsRegistrationStoped ^= true;
    }


    private async Task EyesCentersShowCommandActionAsync(object? obj)
    {
        if (obj is bool state)
        {
            Mat img = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test003.jpg"));
            
            if(state)
            {
                if (_EyesMNN != null)
                {
                    FrameResult result = await Task.Run(() => _EyesMNN.Run(img));
                    img = BBoxDrawer.AddBBox(img, result);
                }
            }
            

            await UpdateEyesVideoFrameAsync(img);
        }
    }

    private async Task TargetsCentersShowCommandActionAsync(object? arg)
    {
        if (arg is bool state)
        {
            
            Mat img = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test001.jpg"));
            if (state)
            {
                foreach (var model in _SceneCustomMNN)
                {
                    FrameResult result = await Task.Run(() => model.Run(img));
                    img = BBoxDrawer.AddBBox(img, result);
                }
            }


            await UpdateSceneVideoFrameAsync(img);
        }
    }

    private async Task ConnectToRegistratorCommandActionAsync()
    {
        string awaittext = string.Empty;
        ActiveDeviceName = _Devices.Devices[DeviceSelectedIndex].HostName;
        IsAwaitScreenVisible = true;
        AwaitScreenText = AppResources.GetString("RegistrationAwaitConnectionText", string.Empty);
        int streamsCount = await ScanLslAsync (CancellationToken.None);
        if(streamsCount < 2)
        {
            AwaitScreenText = string.Empty;
            IsAwaitScreenVisible = false;
            await ShowErrorDialog(AppResources.GetString("RegistrationPageDialogUnableToConnectText", "Error"));
            return;
        }

        AwaitScreenText = AppResources.GetString("RegistrationAwaitRegistrationStatusText", string.Empty);
        _EyeTrackerRpcClient = new EyeTrackerRpcClient(_Devices.Devices[DeviceSelectedIndex].HostName);
        await _EyeTrackerRpcClient.ConnectAsync();
        var registrationState = await _EyeTrackerRpcClient.GetExaminationStateAsync();

        if (registrationState == null)
        {
            AwaitScreenText = string.Empty;
            IsAwaitScreenVisible = false;
            // TODO: Add message dialog with error
            return;
        }
        LoadModels(_Devices.Devices[DeviceSelectedIndex].HostName);
        IsRegistrationStoped = !registrationState.IsRunning;
        if (registrationState.IsRunning)
        {
            // TODO: Add start LSL thread here
            AwaitScreenText = string.Empty;
            IsAwaitScreenVisible = false;
            return;
        }
        await Task.Delay(3000);
        AwaitScreenText = AppResources.GetString("RegistrationAwaitRegistratorInfoText", string.Empty);
        await Task.Delay(3000);
        AwaitScreenText = string.Empty;
        IsAwaitScreenVisible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            ReplaceImage(ref _eyesImage, null, nameof(EyesImage));
            ReplaceImage(ref _sceneImage, null, nameof(SceneImage));
        }
        _EyesMNN?.Dispose();
        foreach(var model in _SceneCustomMNN)
            model.Dispose();

        base.Dispose(disposing);
    }

    private void ReplaceImage(ref WriteableBitmap? target, WriteableBitmap? image, string property)
    {
        var previous = target;
        if (SetProperty(ref target, image, property))
            previous?.Dispose();
    }

    private async void LoadModels(string glassesHostname = "")
    {
        string eyesModel = string.Empty;
        string targetModel = string.Empty;
        string customModel = string.Empty;
        _SceneCustomMNN = new List<MnnInferenceEngine>();
        if(string.IsNullOrEmpty(glassesHostname))
        {
            eyesModel = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "gaze_detector_v5_nano_mono_yolov26_b1.mnn");
            targetModel = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "StartTarget.mnn");
            customModel = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "yolov26n.mnn");
            ModelSpec spec = new ModelSpec(Channels: 1, Height: 224, Width: 640,
                                           NumClasses: 2, FixedBatch: 1);
            _EyesMNN = new MnnInferenceEngine(eyesModel, spec);

        }
        else
        {
            eyesModel = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "gaze_detector_v5_nano_mono_yolov26_b1.mnn");
            ModelSpec spec = new ModelSpec(Channels: 1, Height: 224, Width: 640,
                                           NumClasses: 2, FixedBatch: 1);
            _EyesMNN = new MnnInferenceEngine(eyesModel, spec);
            foreach (string model in _Devices.Devices[DeviceSelectedIndex].AiModels)
            {
                string modelPath = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, model);
                if (File.Exists(modelPath))
                {
                    string modelPathConf = modelPath + ".xml";
                    if(File.Exists(modelPathConf))
                    {
                        AiModelParams aiparams = XmlSerialization.Deserialize<AiModelParams>(modelPathConf);
                        spec = new ModelSpec(Channels: aiparams.Channels, Height: aiparams.Height, Width: aiparams.Width,
                                                       NumClasses: aiparams.NumClasses, FixedBatch: 1);

                        _SceneCustomMNN.Add(new MnnInferenceEngine(modelPath, spec));
                    }
                }
            }
            // TODO: Add xml glasses file parser
            //throw new NotImplementedException();

        }
        


        //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\yolov26n.mnn", spec);
        //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.mnn", spec);
        //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\gaze_detector_v5_nano_mono_yolov26_b1.mnn", spec);
        //FrameResult result = await Task.Run(() => engine.Run(img));
        //Mat imgBB = BBoxDrawer.AddBBox(img, result);

        //imgBB.ImWrite("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\test001mnnBByolo.jpg");
    }

    private async Task<int> ScanLslAsync(CancellationToken cancellationToken)
    {
        int foundedStreams = 0;
        try
        {
            var scanner = new LslStreamScanner();
            var streams = await scanner.ScanAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var device = GlassesLslStreams.ForDevice(_Devices.Devices[DeviceSelectedIndex].GroupLSL, _Devices.Devices[DeviceSelectedIndex].UserLSL);
            var eyeStreams = streams.Where(stream => stream.Name == device.Eyes.Name).ToArray();

            foreach (var stream in streams)
                System.Diagnostics.Trace.WriteLine(
                    $"{stream.Name} @ {stream.Hostname}: {stream.ChannelCount} x {stream.ChannelFormat}, {stream.NominalSampleRate:g} Hz");

            foundedStreams = streams.Count;
            //StatusText = eyeStreams.Length == 0
            //    ? $"LSL streams found: {streams.Count}. GroupE.user0 eye video unavailable."
            //    : $"LSL streams found: {streams.Count}. Eye video: {eyeStreams[0].Hostname}, {eyeStreams[0].NominalSampleRate:g} Hz.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InternalException)
        {
            //StatusText = $"LSL scan failed: {ex.Message}";
            foundedStreams = 0;
        }
        return foundedStreams;
    }

    private async Task ShowErrorDialog(string text = "")
    {
        

        await IoC.UI.ShowMessageAsync(text, "OK");

        
    }

    private async void TestLoad()
    {
        string awaittext = string.Empty;
        ActiveDeviceName = _Devices.Devices[DeviceSelectedIndex].HostName;
        IsAwaitScreenVisible = true;
        AwaitScreenText = AppResources.GetString("RegistrationAwaitConnectionText", string.Empty);
        await Task.Delay(3000);
        AwaitScreenText = AppResources.GetString("RegistrationAwaitRegistrationStatusText", string.Empty);
        await Task.Delay(3000);
        AwaitScreenText = AppResources.GetString("RegistrationAwaitRegistratorInfoText", string.Empty);
        await Task.Delay(3000);
        AwaitScreenText = string.Empty;
        IsAwaitScreenVisible = false;
    }
}
