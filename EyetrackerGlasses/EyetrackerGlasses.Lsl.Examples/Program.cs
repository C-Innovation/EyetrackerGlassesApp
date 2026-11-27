using EyetrackerGlasses.Helpers.Net.LSL;
using OpenCvSharp;

bool scan = args is ["scan"] or ["scan", _];
bool receive = args is ["receive", _, _] or ["receive", _, _, "--gps"];
if (!scan && !receive)
{
    Console.Error.WriteLine("Usage: scan [prefix] | receive <group> <user> [--gps]");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

try
{
    EyetrackerGlasses.Desktop.LslDesktopConfiguration.Configure();
    Console.WriteLine($"Native liblsl: {LSL.library_version()}");
    if (scan)
        return await ScanAsync(args.Length == 2 ? args[1] : "", stop.Token);

    return await ReceiveAsync(args[1], args[2], args.Length == 4, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return 0;
}
catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
{
    Console.Error.WriteLine($"Native library unavailable. Check the path and architecture of lsl.dll: {error.Message}");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static async Task<int> ScanAsync(string prefix, CancellationToken cancellationToken)
{
    var scanner = new LslStreamScanner();
    var visible = await scanner.ScanAsync(TimeSpan.FromSeconds(5), cancellationToken);
    var matches = visible.Where(stream => stream.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    foreach (var stream in matches)
        Console.WriteLine($"{stream.Name} @ {stream.Hostname}: {stream.Type}, " +
                          $"{stream.ChannelCount} x {stream.ChannelFormat}, {stream.NominalSampleRate:g} Hz");
    Console.WriteLine($"Visible: {visible.Count}; matching prefix: {matches.Length}");
    return matches.Length == 0 ? 1 : 0;
}

static async Task<int> ReceiveAsync(string group, string user, bool enableGps, CancellationToken cancellationToken)
{
    var streams = GlassesLslStreams.ForDevice(group, user);
    using var synchronizer = new GlassesStreamSynchronizer(enableGps);
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    stop.CancelAfter(TimeSpan.FromSeconds(20));
    var receiver = new GlassesLslReceiver(streams, synchronizer);
    receiver.ConnectionStateChanged += (name, state) => Console.WriteLine($"{name}: {state}");

    int statusCount = 0;
    int imuCount = 0;
    synchronizer.StatusReceived += sample =>
    {
        if (Interlocked.Increment(ref statusCount) == 1)
            Console.WriteLine($"STATUS: battery {sample.Value.BatteryPercent:g}% at {sample.Timestamp:F6}");
    };
    synchronizer.ImuReceived += sample =>
    {
        if (Interlocked.Increment(ref imuCount) == 1)
            Console.WriteLine($"IMU: gyroscope {sample.Value.Gyroscope} at {sample.Timestamp:F6}");
    };

    int eyesCount = 0, sceneCount = 0, gazeCount = 0, eventCount = 0, gpsCount = 0;
    Task reception = receiver.RunAsync(stop.Token);
    try
    {
        while (!reception.IsCompleted && !stop.IsCancellationRequested)
        {
            await Task.WhenAny(reception, Task.Delay(30, stop.Token));
            ReadAvailableFrames();
        }
    }
    finally
    {
        await stop.CancelAsync();
        try { await reception; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    ReadAvailableFrames();
    Console.WriteLine($"Eyes: {eyesCount}; paired scene: {sceneCount}; gaze: {gazeCount}; events: {eventCount}; " +
                      $"GPS: {gpsCount}; IMU: {imuCount}; STATUS: {statusCount}");
    return eyesCount > 0 || cancellationToken.IsCancellationRequested ? 0 : 1;

    void ReadAvailableFrames()
    {
        while (synchronizer.TryDequeue(out var frame))
        {
            using (frame)
            {
                Mat eyes = frame.Eyes.Value;
                Mat? scene = frame.Scene?.Value;
                if (eyes.Type() != MatType.CV_8UC1 || (scene is not null && scene.Type() != MatType.CV_8UC3))
                    throw new InvalidDataException("Unexpected video pixel format.");

                if (++eyesCount == 1)
                    Console.WriteLine($"Eyes: {eyes.Width}x{eyes.Height} Gray8 at {frame.Eyes.Timestamp:F6}");
                if (scene is not null && ++sceneCount == 1)
                    Console.WriteLine($"Scene: {scene.Width}x{scene.Height} BGR24 at {frame.Scene!.Timestamp:F6}");
                if (frame.Parameters is { } gaze && ++gazeCount == 1)
                    Console.WriteLine($"Gaze: {gaze.Value.Gaze2D} at {gaze.Timestamp:F6}");
                if (frame.Gps is not null)
                    gpsCount++;

                foreach (var marker in frame.Events)
                {
                    eventCount++;
                    Console.WriteLine($"Event {marker.Value} at {marker.Timestamp:F6}");
                }
            }
        }
    }
}
