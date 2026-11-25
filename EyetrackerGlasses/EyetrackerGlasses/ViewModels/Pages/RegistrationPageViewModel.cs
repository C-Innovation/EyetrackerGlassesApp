using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EyetrackerGlasses.Core;
using EyetrackerGlasses.ValueConverters;
using OpenCvSharp;

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

        img1 = Cv2.ImRead("D:\\workspace_Avalonia\\EyetrackerGlassesApp\\EyetrackerGlasses\\Models\\test003.jpg");
        img2 = Cv2.ImRead("D:\\workspace_Avalonia\\EyetrackerGlassesApp\\EyetrackerGlasses\\Models\\test001.jpg");

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

    private async Task StartStopRegistrationCommandAction()
    {
        IsRegistrationStoped ^= true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            ReplaceImage(ref _eyesImage, null, nameof(EyesImage));
            ReplaceImage(ref _sceneImage, null, nameof(SceneImage));
        }

        base.Dispose(disposing);
    }

    private void ReplaceImage(ref WriteableBitmap? target, WriteableBitmap? image, string property)
    {
        var previous = target;
        if (SetProperty(ref target, image, property))
            previous?.Dispose();
    }
}
