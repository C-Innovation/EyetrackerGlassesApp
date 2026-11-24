using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EyetrackerGlasses.Desktop;

internal static class LslDesktopConfiguration
{
    internal static void Configure()
    {
        const string variable = "LSLAPICFG";
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            return;

        string config = Path.Combine(AppContext.BaseDirectory, "lsl_api.cfg");
        if (!File.Exists(config))
            return;

        // liblsl 1.13 uses UCRT getenv, which reads the CRT's own environment.
        int error = _putenv_s(variable, config);
        if (error != 0)
            throw new InvalidOperationException($"Could not set {variable} for liblsl (CRT error {error}).");
    }

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int _putenv_s(string name, string value);
}
