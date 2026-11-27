using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Avalonia;
using EyetrackerGlasses;
using System;
using Android.Content.PM;
using Avalonia.Android;

// Класс намеренно объявлен в глобальном пространстве имён.
// Внутри пространства имён вида EyetrackerGlasses.Android имя Android в директивах
// using разрешалось бы неоднозначно (Android.App против EyetrackerGlasses.Android).

/// <summary>
/// Активность-носитель приложения на Android.
/// <para>
/// В Avalonia 12 активность больше не параметризуется типом приложения и не
/// настраивает <c>AppBuilder</c> — этим занимается <see cref="MainApplication"/>.
/// Представление создаётся фабрикой
/// <c>IActivityApplicationLifetime.MainViewFactory</c> в <c>App</c>, поэтому
/// активность можно пересоздавать без повторной настройки Avalonia.
/// </para>
/// </summary>
[Activity(
    Label = "EyetrackerGlasses",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/Icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int StorageRequest = 1;
    private bool _requestedStorage;
    private WifiManager.MulticastLock? _multicastLock;

    protected override void OnResume()
    {
        base.OnResume();
        App.RequestSharedStorageAccess = RequestStorageAccess;
        (Avalonia.Application.Current as App)?.RefreshSharedStorageAccess();
        if (!_requestedStorage && AndroidSharedStorage.GetDocumentsRoot() is null)
            RequestStorageAccess();

        if (GetSystemService(WifiService) is WifiManager wifi)
        {
            _multicastLock ??= wifi.CreateMulticastLock("EyetrackerGlasses.LSL");
            if (_multicastLock is { IsHeld: false })
                _multicastLock.Acquire();
        }
    }

    protected override void OnPause()
    {
        if (_multicastLock is { IsHeld: true })
            _multicastLock.Release();
        if (App.RequestSharedStorageAccess == RequestStorageAccess)
            App.RequestSharedStorageAccess = null;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _multicastLock?.Dispose();
        base.OnDestroy();
    }

    private void RequestStorageAccess()
    {
        _requestedStorage = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            using var intent = new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                Android.Net.Uri.Parse($"package:{PackageName}"));
            try
            {
                StartActivity(intent);
            }
            catch (ActivityNotFoundException)
            {
                using var fallback = new Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission);
                StartActivity(fallback);
            }
        }
        else
        {
            RequestPermissions([Android.Manifest.Permission.ReadExternalStorage, Android.Manifest.Permission.WriteExternalStorage],
                StorageRequest);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == StorageRequest)
            (Avalonia.Application.Current as App)?.RefreshSharedStorageAccess();
    }
}
