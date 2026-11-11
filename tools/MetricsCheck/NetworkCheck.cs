using System.Net.NetworkInformation;
using SwissOlly.Collector;

namespace MetricsCheck;

/// <summary>
/// Per-adapter network rates over a window of normal traffic: the collector's Mbps, integrated,
/// against the adapter's own byte counters (GetIfEntry2 octets, via NetworkInterface) and the
/// harness's own Network Interface PDH query.
/// </summary>
internal static class NetworkCheck
{
    public static async Task<int> RunAsync(Options options)
    {
        var seconds = (int)options.Number("seconds", 60);
        await using var collector = new WindowsMetricCollector();
        using var pdh = new PdhQuery();
        var receivedCounter = pdh.Add(@"\Network Interface(*)\Bytes Received/sec");
        var sentCounter = pdh.Add(@"\Network Interface(*)\Bytes Sent/sec");

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .GroupBy(n => PdhInstanceName(n.Description), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var totals = adapters.Keys.ToDictionary(k => k, _ => new Totals(), StringComparer.OrdinalIgnoreCase);
        var start = adapters.ToDictionary(a => a.Key, a => Octets(a.Value), StringComparer.OrdinalIgnoreCase);

        _ = await collector.CollectAsync(CancellationToken.None);
        pdh.Collect();
        var previous = Clock.Seconds;
        Console.WriteLine($"Adapters up: {string.Join(", ", adapters.Keys)}");
        Console.WriteLine("Rates in Mbit/s (1,000,000 bits, as Task Manager).");
        Console.WriteLine();

        // Optional known traffic: 60,000-byte pings to the default gateway for 30 s from t = 10 s,
        // one at a time, so it stays a modest load on the LAN and never leaves it.
        using var stopPing = new CancellationTokenSource();
        Task<(int Replies, int Bytes)>? pinger = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        for (var i = 0; i < seconds; i++)
        {
            await timer.WaitForNextTickAsync();
            if (options.Flag("ping-gateway") && i == 10)
            {
                pinger = PingGatewayAsync(adapters.Values, TimeSpan.FromSeconds(30), stopPing.Token);
            }

            var now = Clock.Seconds;
            var snapshot = await collector.CollectAsync(CancellationToken.None);
            pdh.Collect();
            var received = pdh.Array(receivedCounter);
            var sent = pdh.Array(sentCounter);
            var dt = now - previous;
            previous = now;

            var line = $"{now,5:0.0} |";
            foreach (var (instance, total) in totals)
            {
                var panel = $"net-{Snapshots.Sanitize(instance)}";
                var down = Snapshots.Value(snapshot, panel, "download");
                var up = Snapshots.Value(snapshot, panel, "upload");
                total.CollectorDown += double.IsNaN(down) ? 0 : down * 1e6 / 8 * dt;
                total.CollectorUp += double.IsNaN(up) ? 0 : up * 1e6 / 8 * dt;
                total.PdhDown += received.GetValueOrDefault(instance) * dt;
                total.PdhUp += sent.GetValueOrDefault(instance) * dt;
                total.Seen |= !double.IsNaN(down);
                line += $" {Short(instance)} down {down,7:0.000} up {up,7:0.000} (PDH {received.GetValueOrDefault(instance) * 8 / 1e6,7:0.000} / {sent.GetValueOrDefault(instance) * 8 / 1e6,7:0.000}) |";
            }

            Console.WriteLine(line);
        }

        Console.WriteLine();
        if (pinger is not null)
        {
            stopPing.Cancel();
            var (replies, payload) = await pinger;
            // Each 60,008-byte ICMP message travels as 41 IP fragments (20-byte header each) in
            // 14-byte Ethernet frames: about 2.3% on top of the payload, each way.
            Console.WriteLine($"Pings: {replies} replies, {replies * (long)payload / 1e6:0.000} MB payload each way, about {replies * (payload + 8 + (41 * 34)) / 1e6:0.000} MB on the wire each way");
        }

        foreach (var (instance, total) in totals)
        {
            var (startDown, startUp) = start[instance];
            var (endDown, endUp) = Octets(adapters[instance]);
            var octetsDown = (double)(endDown - startDown);
            var octetsUp = (double)(endUp - startUp);
            Console.WriteLine($"{instance}{(total.Seen ? string.Empty : "  (no SwissOlly panel)")}");
            Console.WriteLine($"  down: adapter counters {octetsDown / 1e6,9:0.000} MB, SO {total.CollectorDown / 1e6,9:0.000} MB ({Stats.Percent(total.CollectorDown, octetsDown):+0.00;-0.00}%), PDH {total.PdhDown / 1e6,9:0.000} MB ({Stats.Percent(total.PdhDown, octetsDown):+0.00;-0.00}%), avg {total.CollectorDown * 8 / 1e6 / seconds:0.000} Mbps");
            Console.WriteLine($"  up  : adapter counters {octetsUp / 1e6,9:0.000} MB, SO {total.CollectorUp / 1e6,9:0.000} MB ({Stats.Percent(total.CollectorUp, octetsUp):+0.00;-0.00}%), PDH {total.PdhUp / 1e6,9:0.000} MB ({Stats.Percent(total.PdhUp, octetsUp):+0.00;-0.00}%), avg {total.CollectorUp * 8 / 1e6 / seconds:0.000} Mbps");
        }

        return 0;
    }

    private static async Task<(int Replies, int Bytes)> PingGatewayAsync(IEnumerable<NetworkInterface> adapters, TimeSpan duration, CancellationToken cancellationToken)
    {
        const int payload = 60_000;
        var gateway = adapters
            .SelectMany(a => a.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (gateway is null)
        {
            Console.WriteLine("  -- no IPv4 gateway to ping");
            return (0, payload);
        }

        Console.WriteLine($"  -- pinging {gateway} with {payload} bytes for {duration.TotalSeconds:0} s");
        var buffer = new byte[payload];
        var replies = 0;
        using var ping = new Ping();
        var end = Clock.Seconds + duration.TotalSeconds;
        while (Clock.Seconds < end && !cancellationToken.IsCancellationRequested)
        {
            var reply = await ping.SendPingAsync(gateway, 1000, buffer);
            if (reply.Status == IPStatus.Success)
            {
                replies++;
            }
        }

        Console.WriteLine("  -- pings done");
        return (replies, payload);
    }

    /// <summary>PDH's instance name for an adapter: its description with ( ) # / \ escaped.</summary>
    internal static string PdhInstanceName(string description) =>
        description.Replace('(', '[').Replace(')', ']').Replace('#', '_').Replace('/', '_').Replace('\\', '_');

    private static (long Down, long Up) Octets(NetworkInterface adapter)
    {
        var stats = adapter.GetIPStatistics();
        return (stats.BytesReceived, stats.BytesSent);
    }

    private static string Short(string instance) => instance.Length <= 18 ? instance : instance[..18];

    private sealed class Totals
    {
        public double CollectorDown;
        public double CollectorUp;
        public double PdhDown;
        public double PdhUp;
        public bool Seen;
    }
}
