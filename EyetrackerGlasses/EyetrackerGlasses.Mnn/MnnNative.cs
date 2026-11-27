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
    private const string Library = "eyetracker_mnn";

    static MnnNative()
    {
        if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
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
        return NativeLibrary.Load("eyetracker_mnn", assembly, DllImportSearchPath.SafeDirectories);
    }

    internal static int AbiVersion() =>
        OperatingSystem.IsIOS() ? IosImports.AbiVersion() :
        SharedImports.AbiVersion();

    internal static IntPtr Create(byte* model, nuint modelSize, int threads, int backend, byte* error, nuint errorCapacity) =>
        OperatingSystem.IsIOS() ? IosImports.Create(model, modelSize, threads, backend, error, errorCapacity) :
        SharedImports.Create(model, modelSize, threads, backend, error, errorCapacity);

    internal static void Destroy(IntPtr handle)
    {
        if (OperatingSystem.IsIOS())
            IosImports.Destroy(handle);
        else
            SharedImports.Destroy(handle);
    }

    internal static int InputInfo(MnnHandle handle, int* dimensions, int capacity, out int elements, out int nhwc) =>
        OperatingSystem.IsIOS() ? IosImports.InputInfo(handle, dimensions, capacity, out elements, out nhwc) :
        SharedImports.InputInfo(handle, dimensions, capacity, out elements, out nhwc);

    internal static int Run(MnnHandle handle, float* values, int length, byte* error, nuint capacity) =>
        OperatingSystem.IsIOS() ? IosImports.Run(handle, values, length, error, capacity) :
        SharedImports.Run(handle, values, length, error, capacity);

    internal static int OutputCount(MnnHandle handle) =>
        OperatingSystem.IsIOS() ? IosImports.OutputCount(handle) :
        SharedImports.OutputCount(handle);

    internal static int OutputInfo(MnnHandle handle, int index, int* dimensions, int capacity, out int elements,
        out int nhwc, byte* name, nuint nameCapacity) =>
        OperatingSystem.IsIOS() ? IosImports.OutputInfo(handle, index, dimensions, capacity, out elements, out nhwc, name, nameCapacity) :
        SharedImports.OutputInfo(handle, index, dimensions, capacity, out elements, out nhwc, name, nameCapacity);

    internal static int OutputCopy(MnnHandle handle, int index, float* destination, int length) =>
        OperatingSystem.IsIOS() ? IosImports.OutputCopy(handle, index, destination, length) :
        SharedImports.OutputCopy(handle, index, destination, length);

    private static class SharedImports
    {
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

    // iOS links the bridge from static NativeReference archives.
    private static class IosImports
    {
        [DllImport("__Internal", EntryPoint = "mnn_bridge_abi_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int AbiVersion();
        [DllImport("__Internal", EntryPoint = "mnn_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Create(byte* model, nuint modelSize, int threads, int backend, byte* error, nuint errorCapacity);
        [DllImport("__Internal", EntryPoint = "mnn_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr handle);
        [DllImport("__Internal", EntryPoint = "mnn_input_info", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int InputInfo(MnnHandle handle, int* dimensions, int capacity, out int elements, out int nhwc);
        [DllImport("__Internal", EntryPoint = "mnn_run", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Run(MnnHandle handle, float* values, int length, byte* error, nuint capacity);
        [DllImport("__Internal", EntryPoint = "mnn_output_count", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputCount(MnnHandle handle);
        [DllImport("__Internal", EntryPoint = "mnn_output_info", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputInfo(MnnHandle handle, int index, int* dimensions, int capacity, out int elements,
            out int nhwc, byte* name, nuint nameCapacity);
        [DllImport("__Internal", EntryPoint = "mnn_output_copy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputCopy(MnnHandle handle, int index, float* destination, int length);
    }
}
