namespace EyetrackerGlasses.Core
{
    public enum TensorRtExportStatus
    {
        Success,
        // Написание сохранено согласно контракту вызывающего кода.
        NoAvaliableGPU,
        TensorRtNotFound,
        ExportFailed
    }

    /// <param name="EnginePath">Путь к созданному engine; заполнен только при успехе.</param>
    /// <param name="Log">Диагностика проверки GPU или stdout/stderr trtexec.</param>
    public sealed record TensorRtExportResult(
        TensorRtExportStatus Status,
        string? EnginePath = null,
        string Log = "",
        int? DeviceIndex = null);
}
