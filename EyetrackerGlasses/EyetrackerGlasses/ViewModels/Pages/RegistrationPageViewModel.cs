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

public sealed partial class RegistrationPageViewModel : ViewModelBase
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

    private MnnInferenceEngine? _EyesMNN;
    private List<MnnInferenceEngine> _SceneCustomMNN = [];


    private EyetrackerDevices _Devices;
    private EyeTrackerRpcClient? _EyeTrackerRpcClient;
    private GlassesLslStreams? _connectedLslStreams;
    private bool _recorderRestartPending;
    private static readonly TimeSpan RecorderRestartTimeout = TimeSpan.FromSeconds(80);

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
        

        //img1 = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test003.jpg"));
        //img2 = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test001.jpg"));

        //UpdateVideoFramesAsync(img1, img2);
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
    public async Task UpdateVideoFramesAsync(Mat? eyes, Mat? scene, CancellationToken cancellationToken = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
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

            var previousEyes = _eyesImage;
            var previousScene = _sceneImage;
            _eyesImage = eyesImage;
            _sceneImage = sceneImage;

            if (sceneImage is not null)
            {
                var size = sceneImage.PixelSize;
                double height = (double)size.Height / size.Width <= SceneFormatThreshold
                    ? SceneWideHeight : SceneStandardHeight;
                SetProperty(ref _sceneHeightRatio, height / ReferenceVideoWidth, nameof(SceneHeightRatio));
            }

            if (!ReferenceEquals(previousEyes, eyesImage))
                OnPropertyChanged(nameof(EyesImage));
            if (!ReferenceEquals(previousScene, sceneImage))
                OnPropertyChanged(nameof(SceneImage));
            previousEyes?.Dispose();
            previousScene?.Dispose();
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
        if (_disposed || IsAwaitScreenVisible || _connectedLslStreams is not { } streams ||
            _EyeTrackerRpcClient is not { } client)
            return;

        bool starting = IsRegistrationStoped;
        IsAwaitScreenVisible = true;
        AwaitScreenText = AppResources.GetString(starting
            ? "RegistrationAwaitStartRegistrationText" : "RegistrationAwaitStopRegistrationText", string.Empty);
        try
        {
            RpcReturnCode result;
            if (starting)
            {
                await StopLslAsync();
                if (_disposed)
                    return;

                // The recorder can restart its RPC server after StopExamination.
                if (_recorderRestartPending)
                {
                    await client.DisconnectAsync();
                    await client.WaitForServerAsync(RecorderRestartTimeout);
                }
                if (_disposed)
                    return;

                // The device publishes its acquisition streams after StartExamination.
                result = await client.StartExaminationAsync("");
                if (result == RpcReturnCode.DriverError)
                    _recorderRestartPending = true;
                if (result == RpcReturnCode.Success && !_disposed)
                {
                    StartLsl(streams);
                    _recorderRestartPending = false;
                    IsRegistrationStoped = false;
                }
            }
            else
            {
                // Close all local inlets before asking the device to remove its outlets.
                await StopLslAsync();
                if (_disposed)
                    return;

                bool stopped = false;
                try
                {
                    result = await client.StopExaminationAsync();
                    stopped = result == RpcReturnCode.Success;
                    if (stopped && !_disposed)
                    {
                        _recorderRestartPending = true;
                        IsRegistrationStoped = true;
                        await client.DisconnectAsync();
                    }
                }
                finally
                {
                    if (!stopped && !_disposed)
                        StartLsl(streams);
                }
            }

            if (result != RpcReturnCode.Success && !_disposed)
                await ShowErrorDialog(result.ToString());
        }
        catch (RpcException error)
        {
            if (!_disposed)
                await ShowErrorDialog(error.Message);
        }
        finally
        {
            if (!_disposed)
            {
                AwaitScreenText = string.Empty;
                IsAwaitScreenVisible = false;
            }
        }
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
        if (_disposed || IsAwaitScreenVisible || (uint)DeviceSelectedIndex >= (uint)_Devices.Devices.Count)
            return;

        var device = _Devices.Devices[DeviceSelectedIndex];
        var streams = GlassesLslStreams.ForDevice(device.GroupLSL, device.UserLSL) with { PeerAddress = device.HostName };
        ActiveDeviceName = device.HostName;
        IsAwaitScreenVisible = true;
        AwaitScreenText = AppResources.GetString("RegistrationAwaitConnectionText", string.Empty);
        EyeTrackerRpcClient? pendingClient = null;
        try
        {
            int res = await ScanLslAsync(streams);
            if(res < 2)
            {
                if (!_disposed)
                {
                    AwaitScreenText = string.Empty;
                    IsAwaitScreenVisible = false;
                }
                await ShowErrorDialog(AppResources.GetString("RegistrationPageDialogUnableToConnectText", string.Empty));
                return;
            }
            pendingClient = new EyeTrackerRpcClient(device.HostName);
            if (_recorderRestartPending &&
                string.Equals(_EyeTrackerRpcClient?.Host, device.HostName, StringComparison.OrdinalIgnoreCase))
                await pendingClient.WaitForServerAsync(RecorderRestartTimeout);
            else
                await pendingClient.ConnectAsync();
            if (_disposed)
                return;

            AwaitScreenText = AppResources.GetString("RegistrationAwaitRegistrationStatusText", string.Empty);
            var registrationState = await pendingClient.GetExaminationStateAsync();
            if (_disposed || registrationState is null)
                return;
            if (registrationState.ReturnCode != RpcReturnCode.Success)
            {
                await ShowErrorDialog(registrationState.ReturnCode.ToString());
                return;
            }

            await StopLslAsync();
            if (_disposed)
                return;

            _EyeTrackerRpcClient?.Dispose();
            _EyeTrackerRpcClient = pendingClient;
            pendingClient = null;
            _connectedLslStreams = streams;
            _recorderRestartPending = false;
            ActiveDeviceName = device.HostName;
            IsRegistrationStoped = !registrationState.IsRunning;

            if (registrationState.IsRunning)
            {
                int lslStreamCount = await ScanLslAsync(streams);
                System.Diagnostics.Trace.WriteLine(
                    $"LSL Status/Event for {device.HostName}: {lslStreamCount}/2 streams found.");
                if (_disposed)
                    return;

                StartLsl(streams);
                if (lslStreamCount < 2)
                    await ShowErrorDialog(AppResources.GetString("RegistrationPageDialogUnableToConnectText", string.Empty));
            }

            if (!_disposed)
                LoadModels(device.HostName);
        }
        catch (RpcException error)
        {
            if (!_disposed)
                await ShowErrorDialog(error.Message);
        }
        finally
        {
            pendingClient?.Dispose();
            if (!_disposed)
            {
                AwaitScreenText = string.Empty;
                IsAwaitScreenVisible = false;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            Task stopped = StopLslAsync();
            _ = DisposeModelsAfterStopAsync(stopped);
            _EyeTrackerRpcClient?.Dispose();
            ReplaceImage(ref _eyesImage, null, nameof(EyesImage));
            ReplaceImage(ref _sceneImage, null, nameof(SceneImage));
        }
        base.Dispose(disposing);
    }

    private async Task DisposeModelsAfterStopAsync(Task stopped)
    {
        await stopped.ConfigureAwait(false);
        _EyesMNN?.Dispose();
        foreach (var model in _SceneCustomMNN)
            model.Dispose();
    }

    private void ReplaceImage(ref WriteableBitmap? target, WriteableBitmap? image, string property)
    {
        var previous = target;
        if (SetProperty(ref target, image, property))
            previous?.Dispose();
    }

    private void LoadModels(string glassesHostname = "")
    {
        string directory = AppConfig.GetInstance().AiDirectoryPath;
        string eyesModel = Path.Combine(directory, "gaze_detector_v5_nano_mono_yolov26_b1.mnn");
        var eyesSpec = new ModelSpec(Channels: 1, Height: 224, Width: 640, NumClasses: 2, FixedBatch: 1);
        var eyesEngine = new MnnInferenceEngine(eyesModel, eyesSpec);
        var sceneModels = new List<MnnInferenceEngine>();
        try
        {
            if (!string.IsNullOrEmpty(glassesHostname))
            {
                foreach (string model in _Devices.Devices[DeviceSelectedIndex].AiModels)
                {
                    string modelPath = Path.Combine(directory, model);
                    string modelPathConf = modelPath + ".xml";
                    if (!File.Exists(modelPath) || !File.Exists(modelPathConf))
                        continue;

                    var parameters = XmlSerialization.Deserialize<AiModelParams>(modelPathConf);
                    var spec = new ModelSpec(Channels: parameters.Channels, Height: parameters.Height,
                        Width: parameters.Width, NumClasses: parameters.NumClasses, FixedBatch: 1);
                    sceneModels.Add(new MnnInferenceEngine(modelPath, spec));
                }
            }
        }
        catch
        {
            eyesEngine.Dispose();
            foreach (var model in sceneModels)
                model.Dispose();
            throw;
        }

        Interlocked.Exchange(ref _EyesMNN, eyesEngine)?.Dispose();
        foreach (var model in Interlocked.Exchange(ref _SceneCustomMNN, sceneModels))
            model.Dispose();
        IoC.Logger.Log($"MNN initialized: {Path.GetFileName(eyesModel)} and {sceneModels.Count} scene models.",
            EyetrackerGlasses.Core.LogLevel.Success);
    }

    private static async Task<int> ScanLslAsync(GlassesLslStreams device)
    {
        try
        {
            var scanner = new LslStreamScanner();
            var snapshot = await scanner.ScanAsync(TimeSpan.FromSeconds(2));
            string[] expected = [device.Status, device.Events];
            var names = snapshot.Select(stream => stream.Name).ToHashSet(StringComparer.Ordinal);

            string[] missing = expected.Where(name => !names.Contains(name)).ToArray();
            var streams = snapshot.AsEnumerable();
            if (missing.Length > 0)
            {
                System.Diagnostics.Trace.WriteLine($"LSL targeted discovery: {string.Join(", ", missing)}");
                if (device.PeerAddress is { Length: > 0 } peer)
                {
                    var direct = await new LslTcpDiscovery(peer).GetSnapshotAsync();
                    foreach (string name in missing.Where(direct.Contains))
                    {
                        names.Add(name);
                        System.Diagnostics.Trace.WriteLine($"{name} @ {peer} discovered over TCP.");
                    }
                }
                else
                {
                    var matches = await scanner.FindByNamesAsync(missing, TimeSpan.FromSeconds(5));
                    streams = streams.Concat(matches);
                }
            }
            foreach (var stream in streams)
            {
                names.Add(stream.Name);
                System.Diagnostics.Trace.WriteLine(
                    $"{stream.Name} @ {stream.Hostname}: {stream.ChannelCount} x {stream.ChannelFormat}, {stream.NominalSampleRate:g} Hz");
            }

            return expected.Count(names.Contains);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or InternalException)
        {
            System.Diagnostics.Trace.TraceWarning($"LSL scan failed: {error.Message}");
            return 0;
        }
    }

    //private async Task<bool> ScanLslOnStatusAndEventAsync(CancellationToken cancellationToken)
    //{
    //    int streamsCount = 0;
    //    try
    //    {
    //        var scanner = new LslStreamScanner();
    //        var streams = await scanner.ScanAsync(TimeSpan.FromSeconds(3), cancellationToken);
    //        var device = GlassesLslStreams.ForDevice(, "user0");
    //        var eyeStreams = streams.Where(stream => stream.Name == device.Eyes.Name).ToArray();

    //        foreach (var stream in streams)
    //        {
    //            System.Diagnostics.Trace.WriteLine(
    //                $"{stream.Name} @ {stream.Hostname}: {stream.ChannelCount} x {stream.ChannelFormat}, {stream.NominalSampleRate:g} Hz");


    //        }

            
    //    }
    //    catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InternalException)
    //    {
            
    //    }
    //    return streamsCount >= 2;
    //}

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
