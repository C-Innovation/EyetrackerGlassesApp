using Avalonia.Threading;
using EyetrackerGlasses.Helpers.Net.LSL;
using EyetrackerGlasses.Mnn;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TrtDetector.Models;
using TrtDetector.Visualization;

namespace EyetrackerGlasses.ViewModels;

public sealed partial class RegistrationPageViewModel
{
    private const int PreviewFrameCapacity = 4;
    private readonly object _lslGate = new();
    private CancellationTokenSource? _lslStop;
    private Task? _lslRun;
    private LslSample<GlassesStatus>? _lastLslStatus;
    private LslSample<int>? _lastLslEvent;
    private string? _lslError;

    public LslSample<GlassesStatus>? LastLslStatus => _lastLslStatus;
    public LslSample<int>? LastLslEvent => _lastLslEvent;
    public string? LslError => _lslError;

    /// <summary>Starts receiving from a device. Call on the UI thread.</summary>
    public void StartLsl(GlassesLslStreams streams, bool enableGps = false)
    {
        ArgumentNullException.ThrowIfNull(streams);
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lslGate)
        {
            if (_lslRun is not null)
                throw new InvalidOperationException("Stop the current LSL session before starting another one.");

            var stop = new CancellationTokenSource();
            _lslStop = stop;
            _lslRun = Task.Run(() => RunLslAsync(streams, enableGps, stop));
        }

        SetProperty(ref _lastLslStatus, null, nameof(LastLslStatus));
        SetProperty(ref _lastLslEvent, null, nameof(LastLslEvent));
        SetProperty(ref _lslError, null, nameof(LslError));
    }

    /// <summary>Stops receiving and waits for all LSL tasks to release their resources.</summary>
    public Task StopLslAsync()
    {
        lock (_lslGate)
        {
            _lslStop?.Cancel();
            return _lslRun ?? Task.CompletedTask;
        }
    }

    private async Task RunLslAsync(GlassesLslStreams streams, bool enableGps, CancellationTokenSource stop)
    {
        var token = stop.Token;
        try
        {
            using var synchronizer = new GlassesStreamSynchronizer(enableGps, arrivalDelay: null,
                capacity: PreviewFrameCapacity, paceFromEyeTimestamps: true);
            var receiver = new GlassesLslReceiver(streams, synchronizer);
            using var sceneAnalysis = new ScenePreviewAnalysis();
            receiver.SceneFrameReceived += scene =>
            {
                if (Volatile.Read(ref _SceneCustomMNN).Count > 0)
                    sceneAnalysis.Queue(scene);
            };
            var statuses = Channel.CreateUnbounded<LslSample<GlassesStatus>>();
            var events = Channel.CreateUnbounded<LslSample<int>>();
            synchronizer.StatusReceived += sample => statuses.Writer.TryWrite(sample);
            synchronizer.EventReceived += sample => events.Writer.TryWrite(sample);

            using var completeStatus = token.Register(() => statuses.Writer.TryComplete());
            using var completeEvents = token.Register(() => events.Writer.TryComplete());
            using var completeAnalysis = token.Register(sceneAnalysis.Complete);
            Task analysis = sceneAnalysis.RunAsync(() => Volatile.Read(ref _SceneCustomMNN), token);

            Task[] tasks =
            [
                receiver.RunUntilStoppedAsync(token),
                PollFramesAsync(synchronizer, sceneAnalysis, token),
                ReadSamplesAsync(statuses.Reader, sample =>
                    SetProperty(ref _lastLslStatus, sample, nameof(LastLslStatus)), token),
                ReadSamplesAsync(events.Reader, sample =>
                {
                    _lastLslEvent = sample;
                    OnPropertyChanged(nameof(LastLslEvent));
                }, token)
            ];

            await Task.WhenAny(tasks).ConfigureAwait(false);
            stop.Cancel();
            await Task.WhenAll(Task.WhenAll(tasks), analysis).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"LSL session failed: {error}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed)
                    SetProperty(ref _lslError, error.Message, nameof(LslError));
            });
        }
        finally
        {
            lock (_lslGate)
            {
                _lslStop = null;
                _lslRun = null;
                stop.Dispose();
            }
        }
    }

    private async Task PollFramesAsync(GlassesStreamSynchronizer synchronizer, ScenePreviewAnalysis analysis,
        CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopSignal = token.Register(() => stopped.TrySetResult());
        while (!token.IsCancellationRequested)
        {
            Task<bool> tick = timer.WaitForNextTickAsync().AsTask();
            if (await Task.WhenAny(tick, stopped.Task).ConfigureAwait(false) != tick ||
                token.IsCancellationRequested)
                break;

            if (!synchronizer.TryDequeue(out var frame))
                continue;

            using (frame)
            {
                var scene = frame.Scene;
                if (scene is not null)
                    analysis.Draw(scene);
                await UpdateVideoFramesAsync(frame.Eyes.Value, scene?.Value, token).ConfigureAwait(false);
            }
        }
    }

    private sealed class ScenePreviewAnalysis : IDisposable
    {
        private const double MaxOverlayAgeSeconds = 1;
        private sealed record Overlay(double Timestamp, int Width, int Height, FrameResult[] Results);

        private readonly Channel<LslSample<Mat>> pending = Channel.CreateBounded<LslSample<Mat>>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            sample => sample.Value.Dispose());
        private readonly object overlayGate = new();
        private readonly Queue<Overlay> overlays = new();

        public void Queue(LslSample<Mat> scene)
        {
            if (pending.Reader.Completion.IsCompleted)
                return;
            var copy = new LslSample<Mat>(scene.Timestamp, scene.Value.Clone());
            if (!pending.Writer.TryWrite(copy))
                copy.Value.Dispose();
        }

        public void Draw(LslSample<Mat> scene)
        {
            int width = scene.Value.Width, height = scene.Value.Height;
            Overlay? latest = null;
            lock (overlayGate)
            {
                foreach (var candidate in overlays)
                {
                    double age = scene.Timestamp - candidate.Timestamp;
                    if (age >= 0 && age <= MaxOverlayAgeSeconds && candidate.Width == width &&
                        candidate.Height == height && (latest is null || candidate.Timestamp >= latest.Timestamp))
                        latest = candidate;
                }
            }

            if (latest is not null)
                foreach (var result in latest.Results)
                    BBoxDrawer.DrawInPlace(scene.Value, result);
        }

        public async Task RunAsync(Func<IReadOnlyList<MnnInferenceEngine>> getModels, CancellationToken token)
        {
            try
            {
                await foreach (var scene in pending.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    using var image = scene.Value;
                    if (token.IsCancellationRequested)
                        break;

                    var models = getModels();
                    var results = new FrameResult[models.Count];
                    for (int i = 0; i < models.Count; i++)
                    {
                        if (token.IsCancellationRequested)
                            return;
                        results[i] = models[i].Run(image);
                    }
                    lock (overlayGate)
                    {
                        overlays.Enqueue(new Overlay(scene.Timestamp, image.Width, image.Height, results));
                        if (overlays.Count > 32)
                            overlays.Dequeue();
                    }
                }
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                    Trace.TraceError($"Scene analysis failed: {error}");
            }
            finally
            {
                Complete();
                while (pending.Reader.TryRead(out var scene))
                    scene.Value.Dispose();
            }
        }

        public void Complete() => pending.Writer.TryComplete();

        public void Dispose() => Complete();
    }

    private async Task ReadSamplesAsync<T>(ChannelReader<LslSample<T>> reader,
        Action<LslSample<T>> publish, CancellationToken token)
    {
        await foreach (var sample in reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (token.IsCancellationRequested)
                break;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && !token.IsCancellationRequested)
                    publish(sample);
            });
        }
    }
}
