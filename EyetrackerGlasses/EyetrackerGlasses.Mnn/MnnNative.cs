using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EyetrackerGlasses.Mnn;

internal sealed class MnnHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MnnHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle()
    {
        try { MnnNative.Destroy(handle); return true; }
        catch { return false; } // Never throw from the SafeHandle finalizer.
    }
}

internal static unsafe class MnnNative
{
    // iOS AOT links these symbols from NativeReference static archives. Using the
    // literal __Internal in this net10.0 assembly keeps this working without #if IOS.
    private const string Library = "__Internal";

    static MnnNative()
    {
        if (!OperatingSystem.IsIOS())
            NativeLibrary.SetDllImportResolver(typeof(MnnNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        string file = OperatingSystem.IsWindows() ? "eyetracker_mnn.dll"
            : OperatingSystem.IsMacOS() ? "libeyetracker_mnn.dylib" : "libeyetracker_mnn.so";
        // Desktop publishes contain exactly one matching architecture alongside the app.
        string local = Path.Combine(AppContext.BaseDirectory, file);
        if (File.Exists(local)) return NativeLibrary.Load(local);
        // Android's loader resolves the ABI-specific library installed from the APK.
        return NativeLibrary.Load("eyetracker_mnn", assembly, DllImportSearchPath.SafeDirectories);
    }

    [DllImport(Library, EntryPoint = "mnn_bridge_abi_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AbiVersion();
    [DllImport(Library, EntryPoint = "mnn_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Create(byte* model, nuint modelSize, int threads, int backend, byte* error, nuint errorCapacity);
    [DllImport(Library, EntryPoint = "mnn_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
    [DllImport(Library, EntryPoint = "mnn_input_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int InputInfo(MnnHandle handle, int* dimensions, int capacity, out int elements, out int nhwc);
    [DllImport(Library, EntryPoint = "mnn_run", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Run(MnnHandle handle, float* values, int length, byte* error, nuint capacity);
    [DllImport(Library, EntryPoint = "mnn_output_count", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int OutputCount(MnnHandle handle);
    [DllImport(Library, EntryPoint = "mnn_output_info", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int OutputInfo(MnnHandle handle, int index, int* dimensions, int capacity, out int elements,
        out int nhwc, byte* name, nuint nameCapacity);
    [DllImport(Library, EntryPoint = "mnn_output_copy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int OutputCopy(MnnHandle handle, int index, float* destination, int length);
}
