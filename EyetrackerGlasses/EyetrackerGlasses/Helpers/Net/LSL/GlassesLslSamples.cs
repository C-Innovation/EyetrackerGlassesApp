using System;
using System.Numerics;

namespace EyetrackerGlasses.Helpers.Net.LSL;

public sealed record GlassesParameters(
    Vector2 Gaze2D, Vector3 Gaze3D, float RightPupilDiameter, float LeftPupilDiameter,
    float Illumination, uint RightEyeStatus, uint LeftEyeStatus, float FrameNumber,
    Vector2 Target1, Vector2 Target2, Vector2 Target3, Vector2 RightPupil, Vector2 LeftPupil,
    Vector3 Gyroscope, Vector3 Accelerometer, Vector3 Magnetometer,
    Vector2 LeftGaze2D, Vector2 RightGaze2D, Vector4 Reserved)
{
    internal const int ChannelCount = 38;

    internal static GlassesParameters Decode(float[] s) => new(
        new(s[0], s[1]), new(s[2], s[3], s[4]), s[5], s[6], s[7],
        BitConverter.SingleToUInt32Bits(s[8]), BitConverter.SingleToUInt32Bits(s[9]), s[10],
        new(s[11], s[12]), new(s[13], s[14]), new(s[15], s[16]), new(s[17], s[18]), new(s[19], s[20]),
        new(s[21], s[22], s[23]), new(s[24], s[25], s[26]), new(s[27], s[28], s[29]),
        new(s[30], s[31]), new(s[32], s[33]), new(s[34], s[35], s[36], s[37]));
}

public enum GlassesGpsMode : long
{
    NotSeen = 0,
    NoFix = 1,
    Fix2D = 2,
    Fix3D = 3
}

public sealed record GlassesGps(GlassesGpsMode Mode, double Latitude, double Longitude,
    double AltitudeHae, double AltitudeMsl, double Speed, double Climb)
{
    internal const int ChannelCount = 7;

    internal static GlassesGps Decode(double[] s) =>
        new((GlassesGpsMode)BitConverter.DoubleToInt64Bits(s[0]), s[1], s[2], s[3], s[4], s[5], s[6]);
}

public sealed record GlassesImu(Vector3 Gyroscope, Vector3 Accelerometer, Vector3 Magnetometer, float FrameNumber)
{
    internal const int ChannelCount = 10;

    internal static GlassesImu Decode(float[] s) =>
        new(new(s[0], s[1], s[2]), new(s[3], s[4], s[5]), new(s[6], s[7], s[8]), s[9]);
}

public sealed record GlassesStatus(float BatteryPercent, float SdUsagePercent, float DeviceStatus)
{
    internal const int ChannelCount = 3;

    internal static GlassesStatus Decode(float[] s) => new(s[0], s[1], s[2]);
}
