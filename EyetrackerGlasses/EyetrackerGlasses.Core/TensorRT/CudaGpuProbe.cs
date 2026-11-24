using System;
using System.Runtime.InteropServices;

namespace EyetrackerGlasses.Core
{
    /// <summary>Проверяет доступные CUDA-устройства без зависимости от nvidia-smi или CUDA Toolkit.</summary>
    internal static class CudaGpuProbe
    {
        // TensorRT 10.13: Turing (SM 7.5) и новее.
        private const int MinimumComputeCapability = 75;

        internal static int? FindDevice(out string diagnostic)
        {
            diagnostic = "Нет доступного CUDA GPU с compute capability >= 7.5.";
            string? libraryName = OperatingSystem.IsWindows() ? "nvcuda.dll"
                : OperatingSystem.IsLinux() ? "libcuda.so.1" : null;
            if (libraryName == null || !Environment.Is64BitProcess)
            {
                diagnostic = "Экспорт поддерживается только в 64-разрядном процессе Windows/Linux.";
                return null;
            }

            if (!NativeLibrary.TryLoad(libraryName, out IntPtr library))
            {
                diagnostic = "CUDA-драйвер NVIDIA не найден.";
                return null;
            }

            try
            {
                var init = Load<Init>(library, "cuInit");
                var getCount = Load<GetCount>(library, "cuDeviceGetCount");
                var getDevice = Load<GetDevice>(library, "cuDeviceGet");
                var getAttribute = Load<GetAttribute>(library, "cuDeviceGetAttribute");
                var retain = Load<RetainContext>(library, "cuDevicePrimaryCtxRetain");
                var release = Load<ReleaseContext>(library, "cuDevicePrimaryCtxRelease_v2");
                int code = init(0);
                if (code != 0)
                {
                    diagnostic = $"CUDA cuInit завершился с кодом {code}.";
                    return null;
                }

                code = getCount(out int count);
                if (code != 0)
                {
                    diagnostic = $"CUDA cuDeviceGetCount завершился с кодом {code}.";
                    return null;
                }

                for (int index = 0; index < count; index++)
                {
                    if (getDevice(out int device, index) != 0
                        || getAttribute(out int major, 75, device) != 0
                        || getAttribute(out int minor, 76, device) != 0
                        || major * 10 + minor < MinimumComputeCapability)
                        continue;

                    // Проверяем возможность создать контекст, включая compute mode и доступную память.
                    code = retain(out _, device);
                    if (code != 0)
                    {
                        diagnostic += $" GPU {index}: cuDevicePrimaryCtxRetain = {code}.";
                        continue;
                    }

                    release(device);
                    diagnostic = $"Выбран CUDA GPU {index}, compute capability {major}.{minor}.";
                    return index;
                }

                return null;
            }
            catch (EntryPointNotFoundException ex)
            {
                diagnostic = "CUDA-драйвер не поддерживает необходимые функции: " + ex.Message;
                return null;
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }

        private static T Load<T>(IntPtr library, string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Init(uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetCount(out int count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetDevice(out int device, int ordinal);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetAttribute(out int value, int attribute, int device);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int RetainContext(out IntPtr context, int device);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ReleaseContext(int device);
    }
}
