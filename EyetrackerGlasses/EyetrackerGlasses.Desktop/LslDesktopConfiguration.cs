using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EyetrackerGlasses.Desktop;

internal static class LslDesktopConfiguration
{
    internal static void Configure()
    {
        const string variable = "LSLAPICFG";
        if (!OperatingSystem.IsWindows())
            return;

        string? config = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(config) || !File.Exists(config))
        {
            if (!string.IsNullOrWhiteSpace(config))
                System.Diagnostics.Trace.TraceWarning($"Ignoring missing LSL configuration: {config}");

            config = Path.Combine(AppContext.BaseDirectory, "lsl_api.cfg");
            if (!File.Exists(config))
                return;
        }

        config = Path.GetFullPath(config);
        System.Diagnostics.Trace.WriteLine($"LSL configuration: {config}");

        // liblsl 1.13 uses UCRT getenv, which reads the CRT's own environment.
        int error = _putenv_s(variable, config);
        if (error != 0)
            throw new InvalidOperationException($"Could not set {variable} for liblsl (CRT error {error}).");
    }

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int _putenv_s(string name, string value);
}
