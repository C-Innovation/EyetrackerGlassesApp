// Модели данных RPC-сервера очков-айтрекера (helper_rpc.h).
// Все структуры сервера объявлены через MSGPACK_DEFINE_ARRAY, т.е. передаются
// msgpack-массивами. Поэтому здесь используются целочисленные ключи [Key(n)]:
// порядок ключей ОБЯЗАН совпадать с порядком полей в MSGPACK_DEFINE_ARRAY.

using System;
using System.Collections.Generic;
using MessagePack;

namespace EyetrackerGlasses.Helpers.Net.Rpc;

/// <summary>Имена удалённых процедур (константы k* из helper_rpc.h).</summary>
public static class RpcMethods
{
    public const string ServerInfo = "__ServerConfig__";
    public const string StartExamination = "StartExamination";
    public const string StopExamination = "StopExamination";
    public const string GetExaminationState = "GetExaminationState";
    public const string GetCalibrationPoints = "GetCalibrationPoints";
    public const string SetExamFile = "SetExamFile";
    public const string GetExamFile = "GetExamFile";
    public const string SetSceneCamResolution = "SetSceneCamResolution";
    public const string GetSceneCamResolution = "GetSceneCamResolution";
    public const string GetSceneCamResolutionValues = "GetSceneCamResolutionValues";
    public const string SetEyeSamplingRate = "SetEyeSamplingRate";
    public const string GetEyeSamplingRate = "GetEyeSamplingRate";
    public const string GetEyeSamplingRateValues = "GetEyeSamplingRateValues";
    public const string StartCalibrate = "StartCalibrate";
    public const string StartCalibrateRepairing = "StartCalibrateRepairing";
    public const string StopCalibrate = "StopCalibrate";
    public const string GetCalibrationStatus = "GetCalibrationStatus";
    public const string SetEyeSettings = "SetEyeSettings";
    public const string GetEyeSettings = "GetEyeSettings";
    public const string SetSceneSoundRecording = "SetSceneSoundRecording";
    public const string GetSceneSoundRecording = "GetSceneSoundRecording";
    public const string StartRecording = "StartRecording";
    public const string StopRecording = "StopRecording";
    public const string EraseMemoryCard = "EraseMemoryCard";
    public const string GetNtpState = "GetNtpState";
    public const string GetFirmwareInfo = "GetFirmwareInfo";
    public const string SetShiftRecalculation = "SetShiftRecalculation";
    public const string GetShiftRecalculation = "GetShiftRecalculation";
    public const string SetCamSceneAutoExpos = "SetCamSceneAutoExpos";
    public const string GetCamSceneAutoExpos = "GetCamSceneAutoExpos";
    public const string SetCamSceneExposValue = "SetCamSceneExposValue";
    public const string GetCamSceneExposValue = "GetCamSceneExposValue";
}

/// <summary>Коды возврата сервера (namespace error_codes). Значения меньше 100 — успешные.</summary>
public enum RpcReturnCode : int
{
    Success = 0,
    /// <summary>Настройка будет использована при старте следующего исследования.</summary>
    WillBeUsedInNextExamination = 1,
    AlreadyInState = 100,
    DriverError = 101,
    FileNotFound = 102,
    StudyNotCreated = 103,
    NoAccess = 104,
    CalibrationNotAvailable = 200,
    NotImplemented = 300,
    InternalError = 500,
}

public static class RpcReturnCodeExtensions
{
    /// <summary>Аналог error_codes::is_success(): код меньше 100.</summary>
    public static bool IsSuccess(this RpcReturnCode code) => (int)code < 100;
}

/// <summary>Состояние калибровки (namespace calibration_status).</summary>
public enum CalibrationStatus : uint
{
    /// <summary>Калибровка не выполнена, процесс калибровки не идёт.</summary>
    Null = 0,
    /// <summary>Калибровка идёт, точек ещё недостаточно.</summary>
    Unsatisfied = 1,
    /// <summary>Калибровка идёт, минимальное число точек для работы уже есть.</summary>
    MinimalSatisfied = 2,
    /// <summary>Калибровка идёт, точек достаточно для максимального качества.</summary>
    MaximumSatisfied = 3,
    /// <summary>Калибровка выполнена, процесс калибровки не идёт.</summary>
    Ready = 4,
}

/// <summary>Состояние синхронизации времени (namespace time_sync_state).</summary>
public enum TimeSyncState : uint
{
    NotConfigured = 0,
    NotConnected = 1,
    TimeSyncInProgress = 2,
    Synchronized = 3,
}

/// <summary>Общий интерфейс ответов, содержащих код возврата.</summary>
public interface IRpcResponse
{
    RpcReturnCode ReturnCode { get; }
}

/// <summary>size2d_t&lt;size_t&gt; — {width, height}.</summary>
[MessagePackObject]
public sealed class Size2D : IEquatable<Size2D>
{
    public Size2D() { }

    [SerializationConstructor]
    public Size2D(ulong width, ulong height)
    {
        Width = width;
        Height = height;
    }

    [Key(0)] public ulong Width { get; set; }
    [Key(1)] public ulong Height { get; set; }

    public bool Equals(Size2D? other) => other is not null && Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => Equals(obj as Size2D);
    public override int GetHashCode() => HashCode.Combine(Width, Height);
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>server_info — ответ на "__ServerConfig__".</summary>
[MessagePackObject]
public sealed class ServerInfo
{
    [Key(0)] public string Name { get; set; } = "";
    [Key(1)] public string Type { get; set; } = "";
    [Key(2)] public string Guid { get; set; } = "";
    [Key(3)] public ushort Version { get; set; }
    [Key(4)] public ushort Port { get; set; }

    public override string ToString() => $"{Name} ({Type}) v{Version}, port {Port}, guid {Guid}";
}

[MessagePackObject]
public sealed class ExaminationStateResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    /// <summary>1 — регистрация запущена, 0 — остановлена.</summary>
    [Key(1)] public uint State { get; set; }

    [IgnoreMember] public bool IsRunning => State == 1;
}

/// <summary>position_data_t.</summary>
[MessagePackObject]
public sealed class PositionData
{
    /// <summary>0 — данные можно использовать, иначе — ошибка.</summary>
    [Key(0)] public int State { get; set; }
    /// <summary>Нормализованная координата 0..1 (0 — левый край, 1 — правый).</summary>
    [Key(1)] public float X { get; set; } = -1f;
    /// <summary>Нормализованная координата 0..1 (0 — верх, 1 — низ).</summary>
    [Key(2)] public float Y { get; set; } = -1f;

    [IgnoreMember] public bool IsValid => State == 0;
}

/// <summary>glasses_calibrate_target_t.</summary>
[MessagePackObject]
public sealed class CalibrationTarget
{
    [Key(0)] public PositionData LeftEye { get; set; } = new();
    [Key(1)] public PositionData RightEye { get; set; } = new();
    /// <summary>Целевая точка калибровки.</summary>
    [Key(2)] public PositionData TargetPoint { get; set; } = new();
}

[MessagePackObject]
public sealed class CalibrationPointsResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public List<CalibrationTarget> Points { get; set; } = new();
}

[MessagePackObject]
public sealed class ExamFileResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public byte[] Data { get; set; } = Array.Empty<byte>();
}

[MessagePackObject]
public sealed class SceneCamResolutionResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public Size2D Resolution { get; set; } = new();
}

[MessagePackObject]
public sealed class EyeSamplingRateResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    /// <summary>Частота съёма данных с камер глаз, Гц.</summary>
    [Key(1)] public float SampleRate { get; set; }
}

/// <summary>
/// Ответ GetCalibrationStatus. Сервер фактически возвращает CalibrationStateResponse
/// {return_code, state}; по составу полей он совпадает с CalibrationStatusResponse.
/// </summary>
[MessagePackObject]
public sealed class CalibrationStatusResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public CalibrationStatus Status { get; set; }
}

[MessagePackObject]
public sealed class SceneSoundRecordingResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public bool IsEnabled { get; set; }
}

[MessagePackObject]
public sealed class FileWriteInformationResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    /// <summary>Системная метка времени последнего кадра видео глаз.</summary>
    [Key(1)] public double EyesTimestamp { get; set; }
    /// <summary>Системная метка времени последнего кадра видео сцены.</summary>
    [Key(2)] public double SceneTimestamp { get; set; }
    /// <summary>Индекс текущего чанка видео глаз.</summary>
    [Key(3)] public uint ChunkIndexEyes { get; set; }
    /// <summary>Индекс текущего чанка видео сцены.</summary>
    [Key(4)] public uint ChunkIndexScene { get; set; }
    /// <summary>Абсолютный путь к папке записей на SD-карте.</summary>
    [Key(5)] public string DataFilesPath { get; set; } = "";
}

[MessagePackObject]
public sealed class ShiftRecalculationResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public bool State { get; set; }
}

[MessagePackObject]
public sealed class NtpStateResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public TimeSyncState State { get; set; }
}

[MessagePackObject]
public sealed class CamSceneAutoExposureResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    /// <summary>true — автоэкспозиция включена.</summary>
    [Key(1)] public bool State { get; set; }
}

[MessagePackObject]
public sealed class CamSceneExposureValueResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    /// <summary>Значение экспозиции (-1 при ошибке драйвера).</summary>
    [Key(1)] public int Value { get; set; }
}

[MessagePackObject]
public sealed class FirmwareInfoResponse : IRpcResponse
{
    [Key(0)] public RpcReturnCode ReturnCode { get; set; }
    [Key(1)] public string VersionApp { get; set; } = "";
    [Key(2)] public string VersionCalc { get; set; } = "";
    [Key(3)] public string VersionGazeAI { get; set; } = "";
    [Key(4)] public string VersionTargetAI { get; set; } = "";
    [Key(5)] public string BuildDate { get; set; } = "";
    [Key(6)] public string BuildTime { get; set; } = "";
    [Key(7)] public string VersionCoprocessorFw { get; set; } = "";
}
