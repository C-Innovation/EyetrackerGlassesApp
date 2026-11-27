using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenCvSharp;

namespace EyetrackerGlasses.Helpers.Net.LSL;

public sealed record LslSample<T>(double Timestamp, T Value);

/// <summary>Owns both images. Dispose after consuming the frame.</summary>
public sealed class SynchronizedGlassesFrame : IDisposable
{
    internal SynchronizedGlassesFrame(LslSample<Mat> eyes, LslSample<Mat>? scene,
        LslSample<GlassesParameters>? parameters, LslSample<int>[] events, LslSample<GlassesGps>? gps,
        TimeSpan eyesFrameInterval)
    {
        Eyes = eyes;
        Scene = scene;
        Parameters = parameters;
        Events = events;
        Gps = gps;
        EyesFrameInterval = eyesFrameInterval;
    }

    public LslSample<Mat> Eyes { get; }
    public TimeSpan EyesFrameInterval { get; }
    public LslSample<Mat>? Scene { get; }
    public LslSample<GlassesParameters>? Parameters { get; }
    public IReadOnlyList<LslSample<int>> Events { get; }
    public int EventCode => Events.Count == 0 ? -1 : Events[0].Value;
    public double? EventTimestamp => Events.Count == 0 ? null : Events[0].Timestamp;
    public LslSample<GlassesGps>? Gps { get; }

    public void Dispose()
    {
        Eyes.Value.Dispose();
        Scene?.Value.Dispose();
    }
}

/// <summary>Aligns samples in the receiver's LSL clock domain, in seconds.</summary>
public sealed class GlassesStreamSynchronizer : IDisposable
{
    private const double DefaultEyeFrameRate = 30;
    private const double SceneLookahead = 0.05;
    private readonly object gate = new();
    private readonly Queue<(LslSample<Mat> Sample, long Arrived, double HalfPeriod)> eyes = new();
    private readonly List<LslSample<Mat>> scene = new();
    private readonly List<LslSample<GlassesParameters>> parameters = new();
    private readonly List<LslSample<int>> events = new();
    private readonly List<LslSample<GlassesGps>> gps = new();
    private readonly Queue<SynchronizedGlassesFrame> output = new();
    private readonly TimeSpan arrivalDelay;
    private readonly int capacity;
    private readonly bool paceFromEyeTimestamps;
    private (double Timestamp, long Arrived)? previewClock;
    private double lastEye = double.NegativeInfinity;
    private double closedThrough = double.NegativeInfinity;
    private LslSample<Mat>? heldScene;
    private bool disposed;

    public GlassesStreamSynchronizer(bool enableGps = false, TimeSpan? arrivalDelay = null,
        int capacity = 120) : this(enableGps, arrivalDelay, capacity, paceFromEyeTimestamps: false)
    {
    }

    internal GlassesStreamSynchronizer(bool enableGps, TimeSpan? arrivalDelay, int capacity,
        bool paceFromEyeTimestamps)
    {
        this.arrivalDelay = arrivalDelay ?? TimeSpan.FromSeconds(1.1);
        ArgumentOutOfRangeException.ThrowIfLessThan(this.arrivalDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        this.paceFromEyeTimestamps = paceFromEyeTimestamps;
        GpsEnabled = enableGps;
    }

    public bool GpsEnabled { get; }
    public event Action<LslSample<GlassesImu>>? ImuReceived;
    public event Action<LslSample<GlassesStatus>>? StatusReceived;
    public event Action<LslSample<int>>? EventReceived;

    /// <summary>Transfers ownership of a valid image. Eye timestamps must be increasing.</summary>
    public void AddEyes(double timestamp, Mat image) => AddEyes(timestamp, image, DefaultEyeFrameRate);

    internal void AddEyes(double timestamp, Mat image, double nominalFrameRate)
    {
        double periodMilliseconds = 1000 / nominalFrameRate;
        // PeriodicTimer requires an interval from 1 ms to UInt32.MaxValue - 1 ms.
        double halfPeriod = periodMilliseconds is >= 1 and <= (uint.MaxValue - 1d)
            ? periodMilliseconds / 2000
            : 0.5 / DefaultEyeFrameRate;
        ValidateImage(timestamp, image, MatType.CV_8UC1);
        lock (gate)
        {
            if (disposed || timestamp <= lastEye)
            {
                image.Dispose();
                return;
            }

            lastEye = timestamp;
            eyes.Enqueue((new(timestamp, image), Stopwatch.GetTimestamp(), halfPeriod));
            PublishReady();
        }
    }

    /// <summary>Transfers ownership of a valid image.</summary>
    public void AddScene(double timestamp, Mat image)
    {
        ValidateImage(timestamp, image, MatType.CV_8UC3);
        Add(scene, new(timestamp, image), 120, static mat => mat.Dispose());
    }

    public void AddParameters(double timestamp, GlassesParameters values) =>
        Add(parameters, new(timestamp, values), 1200);

    public void AddEvent(double timestamp, int code)
    {
        var sample = new LslSample<int>(timestamp, code);
        if (Add(events, sample, 4096))
            EventReceived?.Invoke(sample);
    }

    public void AddGps(double timestamp, GlassesGps values)
    {
        if (GpsEnabled)
            Add(gps, new(timestamp, values), 120);
    }

    public void AddImu(double timestamp, GlassesImu values) => ImuReceived?.Invoke(new(timestamp, values));
    public void AddStatus(double timestamp, GlassesStatus values) => StatusReceived?.Invoke(new(timestamp, values));

    public bool TryDequeue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SynchronizedGlassesFrame? frame)
    {
        lock (gate)
        {
            PublishReady();
            return output.TryDequeue(out frame);
        }
    }

    public SynchronizedGlassesFrame[] Drain()
    {
        lock (gate)
        {
            PublishReady();
            var frames = output.ToArray();
            output.Clear();
            return frames;
        }
    }

    private bool Add<T>(List<LslSample<T>> samples, LslSample<T> sample, int limit, Action<T>? release = null)
    {
        ValidateTimestamp(sample.Timestamp);
        lock (gate)
        {
            if (disposed)
            {
                release?.Invoke(sample.Value);
                return false;
            }

            int index = samples.Count;
            while (index > 0 && samples[index - 1].Timestamp > sample.Timestamp)
                index--;

            samples.Insert(index, sample);
            if (samples.Count > limit)
            {
                release?.Invoke(samples[0].Value);
                samples.RemoveAt(0);
            }

            PublishReady();
            return true;
        }
    }

    private void PublishReady()
    {
        while (eyes.TryPeek(out var pending) && IsReady(pending.Sample.Timestamp, pending.Arrived))
        {
            double time = pending.Sample.Timestamp;
            double start = double.IsNegativeInfinity(closedThrough) ? time - pending.HalfPeriod : closedThrough;
            double end = time + pending.HalfPeriod;
            eyes.Dequeue();
            if (eyes.TryPeek(out var next))
                end = time + (next.Sample.Timestamp - time) / 2;

            var sceneFrame = SelectScene(time);

            var parameterFrame = TakeWindow(parameters, Math.Max(start, time - pending.HalfPeriod),
                    Math.Min(end, time + pending.HalfPeriod))
                .MinBy(sample => Math.Abs(sample.Timestamp - time));
            var windowEvents = TakeWindow(events, start, end);
            int gpsIndex = LastAtOrBefore(gps, time);
            var gpsFrame = gpsIndex >= 0 && time - gps[gpsIndex].Timestamp <= 0.15
                ? gps[gpsIndex]
                : null;
            if (gpsIndex > 0)
                gps.RemoveRange(0, gpsIndex);

            closedThrough = end;
            output.Enqueue(new(pending.Sample, sceneFrame, parameterFrame, windowEvents, gpsFrame,
                TimeSpan.FromSeconds(2 * pending.HalfPeriod)));
            if (output.Count > capacity)
                output.Dequeue().Dispose();
        }
    }

    private bool IsReady(double timestamp, long arrived)
    {
        if (!paceFromEyeTimestamps)
            return Stopwatch.GetElapsedTime(arrived) >= arrivalDelay;

        var clock = previewClock ?? (timestamp, arrived);
        double due = arrivalDelay.TotalSeconds + (timestamp - clock.Timestamp);
        double arrival = Stopwatch.GetElapsedTime(clock.Arrived, arrived).TotalSeconds;
        // Rebuffer after a delivery gap that exhausted the buffer or a source-clock jump.
        if (due < arrival || due - arrival > arrivalDelay.TotalSeconds + 10)
        {
            clock = (timestamp, arrived);
            due = arrivalDelay.TotalSeconds;
        }
        previewClock = clock;
        return Stopwatch.GetElapsedTime(clock.Arrived).TotalSeconds >= due;
    }

    private LslSample<Mat>? SelectScene(double timestamp)
    {
        int index = LastAtOrBefore(scene, timestamp);
        int next = index + 1;
        double previous = index >= 0 ? scene[index].Timestamp : double.NegativeInfinity;
        previous = Math.Max(previous, heldScene?.Timestamp ?? double.NegativeInfinity);
        if (next < scene.Count && scene[next].Timestamp - timestamp <= SceneLookahead &&
            scene[next].Timestamp - timestamp < Math.Abs(previous - timestamp))
            index = next;

        if (index >= 0)
        {
            var candidate = scene[index];
            if (heldScene is null || candidate.Timestamp > heldScene.Timestamp)
            {
                heldScene?.Value.Dispose();
                heldScene = candidate;
            }
            else
            {
                candidate.Value.Dispose();
            }

            for (int i = 0; i < index; i++)
                scene[i].Value.Dispose();
            scene.RemoveRange(0, index + 1);
        }

        return heldScene is null ? null : new(heldScene.Timestamp, heldScene.Value.Clone());
    }

    private static LslSample<T>[] TakeWindow<T>(List<LslSample<T>> samples, double start, double end)
    {
        int first = samples.FindIndex(sample => sample.Timestamp >= start);
        int count = samples.FindIndex(sample => sample.Timestamp >= end);
        if (count < 0)
            count = samples.Count;
        var window = first >= 0 && first < count ? samples.GetRange(first, count - first).ToArray() : [];
        samples.RemoveRange(0, count);
        return window;
    }

    private static int LastAtOrBefore<T>(List<LslSample<T>> samples, double timestamp) =>
        samples.FindLastIndex(sample => sample.Timestamp <= timestamp);

    private static void ValidateImage(double timestamp, Mat image, MatType type)
    {
        ValidateTimestamp(timestamp);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty() || image.Type() != type)
            throw new ArgumentException($"Expected a nonempty {type} image.", nameof(image));
    }

    private static void ValidateTimestamp(double timestamp)
    {
        if (!double.IsFinite(timestamp))
            throw new ArgumentOutOfRangeException(nameof(timestamp));
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            while (eyes.TryDequeue(out var frame))
                frame.Sample.Value.Dispose();
            foreach (var frame in scene)
                frame.Value.Dispose();
            scene.Clear();
            heldScene?.Value.Dispose();
            heldScene = null;
            parameters.Clear();
            events.Clear();
            gps.Clear();
            while (output.TryDequeue(out var frame))
                frame.Dispose();
        }
    }
}
