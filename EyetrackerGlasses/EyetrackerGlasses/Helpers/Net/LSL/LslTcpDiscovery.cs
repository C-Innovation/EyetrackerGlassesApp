using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace EyetrackerGlasses.Helpers.Net.LSL;

/// <summary>Reads outlet metadata directly from the configured recorder when UDP discovery misses it.</summary>
internal sealed class LslTcpDiscovery(string host, CancellationToken sessionToken = default)
{
    private const int FirstDataPort = 16572;
    private const int DataPortCount = 32;
    private const int MaxReplyBytes = 16 * 1024;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly byte[] Request = "LSL:shortinfo\r\ntrue()\r\n"u8.ToArray();
    private readonly object gate = new();
    private Task<ILookup<string, string>>? snapshot;
    private long scanStarted;

    internal Task<ILookup<string, string>> GetSnapshotAsync()
    {
        lock (gate)
        {
            if (snapshot is null || snapshot.IsCompleted &&
                System.Diagnostics.Stopwatch.GetElapsedTime(scanStarted) >= RefreshInterval)
            {
                scanStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                snapshot = DiscoverAsync();
            }
            return snapshot;
        }
    }

    private async Task<ILookup<string, string>> DiscoverAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        deadline.CancelAfter(DiscoveryTimeout);
        var results = Array.Empty<(string Name, string Xml)?>();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, deadline.Token)
                .ConfigureAwait(false);
            results = await Task.WhenAll(addresses.SelectMany(address =>
                Enumerable.Range(FirstDataPort, DataPortCount)
                    .Select(port => ReadOutletAsync(address, port, deadline.Token)))).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine($"LSL TCP discovery for {host}: {error.Message}");
        }

        return results.Where(result => result.HasValue).Select(result => result.GetValueOrDefault())
            .Distinct().ToLookup(result => result.Name, result => result.Xml, StringComparer.Ordinal);
    }

    private static async Task<(string Name, string Xml)?> ReadOutletAsync(IPAddress address, int port,
        CancellationToken token)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Request, token).ConfigureAwait(false);

            var buffer = new byte[MaxReplyBytes];
            int length = 0;
            int end = -1;
            while (length < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
                if (read == 0)
                    break;
                length += read;
                end = buffer.AsSpan(0, length).IndexOf("</info>"u8);
                if (end >= 0)
                    break;
            }
            if (end < 0)
                return null;

            using var text = new StringReader(Encoding.UTF8.GetString(buffer, 0, end + "</info>".Length));
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            XElement info = XElement.Load(reader);
            string? name = (string?)info.Element("name");
            if (info.Name != "info" || string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace((string?)info.Element("uid")) ||
                !ushort.TryParse((string?)info.Element("v4data_port"), out ushort dataPort) || dataPort != port ||
                !ushort.TryParse((string?)info.Element("v4service_port"), out ushort servicePort) || servicePort == 0)
                return null;

            // UDP discovery normally fills in the reachable address from the packet source.
            info.SetElementValue("v4address", address.ToString());
            info.SetElementValue("v6address", string.Empty);
            return (name, info.ToString(SaveOptions.DisableFormatting));
        }
        catch (Exception error) when (error is SocketException or IOException or XmlException or OperationCanceledException)
        {
            return null;
        }
    }
}
