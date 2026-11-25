using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace EyetrackerGlasses.Helpers.Net.LSL;

/// <summary>Raw video is tightly packed Gray8 (eyes) or BGR24 (scene). Otherwise each sample holds one encoded image.</summary>
public sealed record LslVideoStream(string Name, int Width = 0, int Height = 0)
{
    internal bool IsRaw => Width > 0 && Height > 0;
}

public sealed record GlassesLslStreams(LslVideoStream Eyes, LslVideoStream Scene)
{
    public string Parameters { get; init; } = "glasses_params";
    public string Events { get; init; } = "glasses_event";
    public string Gps { get; init; } = "glasses_gps";
    public string Imu { get; init; } = "glasses_imu";
    public string Status { get; init; } = "glasses_status";
    public string? Hostname { get; init; }

    /// <summary>Builds the stream names used by glasses_pc_viewer.</summary>
    public static GlassesLslStreams ForDevice(string group, string user, string? hostname = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        string prefix = $"{group}.{user}-glasses.";
        return new(new(prefix + "video_eyes"), new(prefix + "video_scene"))
        {
            Parameters = prefix + "gaze",
            Events = prefix + "event",
            Gps = prefix + "gps",
            Imu = prefix + "imu",
            Status = prefix + "status",
            Hostname = hostname
        };
    }
}

public enum LslConnectionState
{
    NotFound,
    Opening,
    OpenTimedOut,
    SynchronizingClock,
    ClockSyncTimedOut,
    Ready,
    Lost
}

/// <summary>Receives the device streams and maps their timestamps to the receiver clock.</summary>
public sealed class GlassesLslReceiver(GlassesLslStreams streams, GlassesStreamSynchronizer synchronizer)
{
    private const double DiscoveryTimeout = 2;
    private const double ConnectionTimeout = 3;
    private const double PullTimeout = 0.1;
    private int running;

    /// <summary>Reports changes on the background threads that receive each stream.</summary>
    public event Action<string, LslConnectionState>? ConnectionStateChanged;

    /// <summary>Runs until cancelled or a stream reports an unrecoverable error. Await before disposing the synchronizer.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("This receiver is already running.");

        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = stop.Token;

            Task Start(string name, Action<StreamInlet, StreamInfo, CancellationToken> read) =>
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        Receive(name, read, token);
                    }
                    catch
                    {
                        stop.Cancel();
                        throw;
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Task[] readers =
            [
                Start(streams.Eyes.Name, (inlet, info, ct) =>
                {
                    double frameRate = info.nominal_srate();
                    ReadVideo(inlet, info, streams.Eyes, true,
                        (timestamp, image) => synchronizer.AddEyes(timestamp, image, frameRate), ct);
                }),
                Start(streams.Scene.Name, (inlet, info, ct) =>
                    ReadVideo(inlet, info, streams.Scene, false, synchronizer.AddScene, ct)),
                Start(streams.Parameters, (inlet, info, ct) => ReadFloat32(inlet, info, GlassesParameters.ChannelCount,
                    GlassesParameters.Decode, synchronizer.AddParameters, ct)),
                Start(streams.Events, ReadEvents),
                Start(streams.Imu, (inlet, info, ct) => ReadFloat32(inlet, info, GlassesImu.ChannelCount,
                    GlassesImu.Decode, synchronizer.AddImu, ct)),
                Start(streams.Status, (inlet, info, ct) => ReadFloat32(inlet, info, GlassesStatus.ChannelCount,
                    GlassesStatus.Decode, synchronizer.AddStatus, ct)),
                synchronizer.GpsEnabled
                    ? Start(streams.Gps, ReadGps)
                    : Task.CompletedTask
            ];
            await Task.WhenAll(readers).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private void Receive(string name, Action<StreamInlet, StreamInfo, CancellationToken> read,
        CancellationToken token)
    {
        LslConnectionState? state = null;
        void Report(LslConnectionState next)
        {
            if (state == next)
                return;

            state = next;
            ConnectionStateChanged?.Invoke(name, next);
        }

        while (!token.IsCancellationRequested)
        {
            // KnownPeers discovery may run after the initial multicast wave.
            var matches = LSL.resolve_stream("name", name, 1, DiscoveryTimeout);
            try
            {
                var candidates = matches.Where(info => streams.Hostname is null ||
                    string.Equals(info.hostname(), streams.Hostname, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length > 1)
                    throw new InvalidOperationException($"Multiple LSL streams named '{name}' were found.");

                if (token.IsCancellationRequested)
                    return;

                if (candidates.Length == 0)
                {
                    Report(LslConnectionState.NotFound);
                    continue;
                }

                var info = candidates[0];
                using var inlet = new StreamInlet(info, max_buflen: 3, recover: false,
                    postproc_flags: processing_options_t.proc_clocksync);
                Report(LslConnectionState.Opening);
                inlet.open_stream(ConnectionTimeout);
                if (token.IsCancellationRequested)
                    return;

                Report(LslConnectionState.SynchronizingClock);
                inlet.time_correction(ConnectionTimeout);
                if (token.IsCancellationRequested)
                    return;

                Report(LslConnectionState.Ready);
                read(inlet, info, token);
            }
            catch (LostException)
            {
                Report(LslConnectionState.Lost);
            }
            catch (TimeoutException) when (state == LslConnectionState.Opening)
            {
                Report(LslConnectionState.OpenTimedOut);
            }
            catch (TimeoutException) when (state == LslConnectionState.SynchronizingClock)
            {
                Report(LslConnectionState.ClockSyncTimedOut);
            }
            finally
            {
                foreach (var info in matches)
                    info.Dispose();
            }

            token.WaitHandle.WaitOne(50);
        }
    }

    private static void ReadFloat32<T>(StreamInlet inlet, StreamInfo info, int channels, Func<float[], T> decode,
        Action<double, T> add, CancellationToken token)
    {
        RequireLayout(info, channels, channel_format_t.cf_float32);
        var values = new float[channels];
        while (!token.IsCancellationRequested)
        {
            double timestamp = inlet.pull_sample(values, PullTimeout);
            if (timestamp != 0)
                add(timestamp, decode(values));
        }
    }

    private void ReadGps(StreamInlet inlet, StreamInfo info, CancellationToken token)
    {
        RequireLayout(info, GlassesGps.ChannelCount, channel_format_t.cf_double64);
        var values = new double[GlassesGps.ChannelCount];
        while (!token.IsCancellationRequested)
        {
            double timestamp = inlet.pull_sample(values, PullTimeout);
            if (timestamp != 0)
                synchronizer.AddGps(timestamp, GlassesGps.Decode(values));
        }
    }

    private static void RequireLayout(StreamInfo info, int channels, channel_format_t format)
    {
        if (info.channel_count() != channels || info.channel_format() != format)
            throw new InvalidDataException($"'{info.name()}' must have {channels} channels in the {format} format.");
    }

    private void ReadEvents(StreamInlet inlet, StreamInfo info, CancellationToken token)
    {
        if (info.channel_count() != 1)
            throw new InvalidDataException($"'{info.name()}' must have one event channel.");

        var value = new int[1];
        while (!token.IsCancellationRequested)
        {
            double timestamp = inlet.pull_sample(value, PullTimeout);
            if (timestamp != 0)
                synchronizer.AddEvent(timestamp, value[0]);
        }
    }

    private static void ReadVideo(StreamInlet inlet, StreamInfo info, LslVideoStream stream, bool grayscale,
        Action<double, Mat> add, CancellationToken token)
    {
        if ((stream.Width != 0 || stream.Height != 0) && !stream.IsRaw)
            throw new ArgumentException("Raw video requires positive width and height.", nameof(stream));

        int rawBytes = stream.IsRaw ? checked(stream.Width * stream.Height * (grayscale ? 1 : 3)) : 0;
        bool binaryString = info.channel_format() == channel_format_t.cf_string && info.channel_count() == 1;
        bool rawChannels = stream.IsRaw && info.channel_format() == channel_format_t.cf_int8 &&
            info.channel_count() == rawBytes;

        if (!binaryString && !rawChannels)
            throw new InvalidDataException($"Unsupported LSL video channel layout for '{stream.Name}'.");

        var raw = rawChannels ? new byte[rawBytes] : Array.Empty<byte>();
        var pointers = new IntPtr[1];
        var lengths = new uint[1];
        while (!token.IsCancellationRequested)
        {
            int error = 0;
            byte[] bytes = raw;
            double timestamp;
            if (binaryString)
            {
                try
                {
                    timestamp = dll.lsl_pull_sample_buf(inlet, pointers, lengths, 1, PullTimeout, ref error);
                    LSL.check_error(error);
                    if (timestamp == 0)
                        continue;

                    bytes = new byte[checked((int)lengths[0])];
                    if (bytes.Length > 0)
                        Marshal.Copy(pointers[0], bytes, 0, bytes.Length);
                }
                finally
                {
                    if (pointers[0] != IntPtr.Zero)
                    {
                        dll.lsl_destroy_string(pointers[0]);
                        pointers[0] = IntPtr.Zero;
                    }
                }
            }
            else
            {
                timestamp = dll.lsl_pull_sample_bytes(inlet, raw, raw.Length, PullTimeout, ref error);
                LSL.check_error(error);
                if (timestamp == 0)
                    continue;
            }

            var image = DecodeVideo(stream, grayscale, bytes, rawBytes);
            try
            {
                add(timestamp, image);
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }
    }

    private static Mat DecodeVideo(LslVideoStream stream, bool grayscale, byte[] bytes, int rawBytes)
    {
        if (bytes.Length == 0 || (stream.IsRaw && bytes.Length != rawBytes))
            throw new InvalidDataException($"Invalid frame length in '{stream.Name}'.");

        if (stream.IsRaw)
        {
            using var view = Mat.FromPixelData(stream.Height, stream.Width,
                grayscale ? MatType.CV_8UC1 : MatType.CV_8UC3, bytes);
            return view.Clone();
        }

        var frame = Cv2.ImDecode(bytes, grayscale ? ImreadModes.Grayscale : ImreadModes.Color);
        if (!frame.Empty())
            return frame;

        frame.Dispose();
        throw new InvalidDataException($"Unable to decode an image from '{stream.Name}'.");
    }
}
