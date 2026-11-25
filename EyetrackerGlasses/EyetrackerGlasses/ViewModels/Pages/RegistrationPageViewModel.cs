using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EyetrackerGlasses.Core;
using EyetrackerGlasses.Mnn;
using EyetrackerGlasses.ValueConverters;
using OpenCvSharp;
using System;
using System.IO;
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
    private MnnInferenceEngine _TargetMNN;
    private MnnInferenceEngine _CustomMNN;


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

        LoadModels();

        img1 = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test003.jpg"));
        img2 = Cv2.ImRead(Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "TMP", "test001.jpg"));

        UpdateVideoFramesAsync(img1, img2);
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
                FrameResult result = await Task.Run(() => _EyesMNN.Run(img));
                img = BBoxDrawer.AddBBox(img, result);
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
                FrameResult result1 = await Task.Run(() => _TargetMNN.Run(img));
                FrameResult result2 = await Task.Run(() => _CustomMNN.Run(img));
                img = BBoxDrawer.AddBBox(img, result1);
                img = BBoxDrawer.AddBBox(img, result2);
            }


            await UpdateSceneVideoFrameAsync(img);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            ReplaceImage(ref _eyesImage, null, nameof(EyesImage));
            ReplaceImage(ref _sceneImage, null, nameof(SceneImage));
        }
        _EyesMNN.Dispose();
        _TargetMNN.Dispose();
        _CustomMNN.Dispose();
        base.Dispose(disposing);
    }

    private void ReplaceImage(ref WriteableBitmap? target, WriteableBitmap? image, string property)
    {
        var previous = target;
        if (SetProperty(ref target, image, property))
            previous?.Dispose();
    }

    private void LoadModels(string glassesHostname = "")
    {
        string eyesModel = string.Empty;
        string targetModel = string.Empty;
        string customModel = string.Empty;

        if(string.IsNullOrEmpty(glassesHostname))
        {
            eyesModel = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "gaze_detector_v5_nano_mono_yolov26_b1.mnn");
            targetModel = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "StartTarget.mnn");
            customModel = Path.Combine(AppConfig.GetInstance().AiDirectoryPath, "yolov26n.mnn");
        }
        else
        {
            // TODO: Add xml glasses file parser
            throw new NotImplementedException();

        }
        ModelSpec spec = new ModelSpec(Channels: 1, Height: 224, Width: 640,
        NumClasses: 2, FixedBatch: 1);
        _EyesMNN = new MnnInferenceEngine(eyesModel, spec);

        spec = new ModelSpec(Channels: 3, Height: 352, Width: 640,
        NumClasses: 1, FixedBatch: 1);
        _TargetMNN = new MnnInferenceEngine(targetModel, spec);

        spec = new ModelSpec(Channels: 3, Height: 640, Width: 640,
        NumClasses: 80, FixedBatch: 1);
        _CustomMNN = new MnnInferenceEngine(customModel, spec);

        //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\yolov26n.mnn", spec);
        //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\StartTarget.mnn", spec);
        //using var engine = new MnnInferenceEngine("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\gaze_detector_v5_nano_mono_yolov26_b1.mnn", spec);
        //FrameResult result = await Task.Run(() => engine.Run(img));
        //Mat imgBB = BBoxDrawer.AddBBox(img, result);

        //imgBB.ImWrite("E:\\workspace_Avalonia\\EyetrackerGlasses\\EyetrackerGlasses\\Models\\test001mnnBByolo.jpg");
    }
}
