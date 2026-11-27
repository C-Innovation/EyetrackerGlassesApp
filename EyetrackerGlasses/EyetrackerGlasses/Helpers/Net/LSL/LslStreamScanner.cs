using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EyetrackerGlasses.Helpers.Net.LSL;

/// <summary>Metadata returned by LSL discovery. The rate is zero for irregular streams.</summary>
public sealed record LslStreamDescriptor(string Name, string Type, string Hostname, string SourceId,
    string Uid, int ChannelCount, channel_format_t ChannelFormat, double NominalSampleRate);

public sealed class LslStreamScanner
{
    /// <summary>
    /// Collects the streams visible during one discovery window. Cancellation is observed before
    /// and after the native discovery call, which is bounded by the scan duration.
    /// </summary>
    public Task<IReadOnlyList<LslStreamDescriptor>> ScanAsync(TimeSpan? duration = null,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(LSL.resolve_streams, duration, cancellationToken);

    /// <summary>Looks up an exact advertised stream name using liblsl discovery.</summary>
    public Task<IReadOnlyList<LslStreamDescriptor>> FindByNameAsync(string name, TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ResolveAsync(timeout => LSL.resolve_stream("name", name, 1, timeout), duration, cancellationToken);
    }

    /// <summary>Waits for the requested names in a single discovery window.</summary>
    public Task<IReadOnlyList<LslStreamDescriptor>> FindByNamesAsync(IEnumerable<string> names,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] requested = names.Distinct(StringComparer.Ordinal).ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(requested.Length, nameof(names));
        foreach (string name in requested)
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(names));

        string predicate = string.Join(" or ", requested.Select(name => $"name={XPathLiteral(name)}"));
        return ResolveAsync(timeout => LSL.resolve_stream($"({predicate})", requested.Length, timeout), duration,
            cancellationToken);
    }

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\''))
            return $"'{value}'";
        if (!value.Contains('"'))
            return $"\"{value}\"";

        return "concat(" + string.Join(", \"'\", ", value.Split('\'').Select(part => $"'{part}'")) + ")";
    }

    private static Task<IReadOnlyList<LslStreamDescriptor>> ResolveAsync(Func<double, StreamInfo[]> resolve,
        TimeSpan? duration, CancellationToken cancellationToken)
    {
        TimeSpan window = duration ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero, nameof(duration));

        return Task.Run<IReadOnlyList<LslStreamDescriptor>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamInfo[] streams = resolve(window.TotalSeconds);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Array.AsReadOnly(streams.Select(info => new LslStreamDescriptor(
                        info.name(), info.type(), info.hostname(), info.source_id(), info.uid(),
                        info.channel_count(), info.channel_format(), info.nominal_srate()))
                    .OrderBy(stream => stream.Name, StringComparer.Ordinal)
                    .ThenBy(stream => stream.Hostname, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(stream => stream.Uid, StringComparer.Ordinal)
                    .ToArray());
            }
            finally
            {
                foreach (var stream in streams)
                    stream.Dispose();
            }
        }, cancellationToken);
    }
}
