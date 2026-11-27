using System;
using System.IO;
using System.Runtime.InteropServices;
using Android.Content.PM;
using Android.OS;
using AndroidEnvironment = Android.OS.Environment;

internal static class AndroidSharedStorage
{
    internal static string? GetDocumentsRoot()
    {
        bool granted = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? AndroidEnvironment.IsExternalStorageManager
            : Android.App.Application.Context.CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;
        if (!granted || AndroidEnvironment.ExternalStorageState != AndroidEnvironment.MediaMounted)
            return null;

        return AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDocuments)?.AbsolutePath;
    }

    internal static void ConfigureLsl(string appDirectory)
    {
        string settings = Path.Combine(appDirectory, "Settings");
        Directory.CreateDirectory(settings);
        string config = Path.Combine(settings, "lsl_api.cfg");
        if (!File.Exists(config))
            File.WriteAllText(config, "[lab]\nKnownPeers = {10.0.0.80}\n");

        if (SetEnvironment("LSLAPICFG", config, 1) != 0)
            throw new IOException("Не удалось настроить поиск LSL.");
    }

    [DllImport("libc.so", EntryPoint = "setenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetEnvironment(string name, string value, int overwrite);
}
