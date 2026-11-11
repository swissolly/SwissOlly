using SwissOlly.Core.Models;

namespace StoreBench;

/// <summary>
/// The board the collector records on the development PC (16 logical processors, fixed drives
/// B: and C:, two network adapters, one GPU with a temperature sensor): 41 series, with the
/// collector's own panel and series keys and titles, so rows are as wide as real ones. Values
/// move like real ones (load with spikes, slow memory drift, bursty disk and network) so the
/// stored numbers are not unrealistically compressible.
/// </summary>
internal sealed class SyntheticBoard
{
    private readonly Random _random;
    private readonly List<Series> _series = [];
    private double _cpuLoad = 8;
    private double _memoryUsed = 15;
    private double _driveB = 61.2;
    private double _driveC = 74.8;

    public SyntheticBoard(int seed = 7)
    {
        _random = new Random(seed);

        Add("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, _ => _cpuLoad);
        for (var core = 0; core < 16; core++)
        {
            Add("cpu-cores", "CPU Cores", $"core-{core}", $"Core {core}", MetricCategory.Cpu, MetricUnit.Percent,
                r => Math.Clamp(_cpuLoad + ((r.NextDouble() - 0.5) * 18), 0, 100));
        }

        Add("cpu-frequency", "CPU Clock", "clock", "Clock", MetricCategory.Cpu, MetricUnit.Megahertz,
            r => 3600 + (_cpuLoad * 9) + (r.NextDouble() * 400));
        Add("memory", "Memory", "used-gb", "Used", MetricCategory.Memory, MetricUnit.Gigabytes, _ => _memoryUsed);
        Add("memory", "Memory", "available-gb", "Available", MetricCategory.Memory, MetricUnit.Gigabytes, _ => 31.86 - _memoryUsed);
        Add("memory", "Memory", "total-gb", "Total", MetricCategory.Memory, MetricUnit.Gigabytes, _ => 31.86);
        foreach (var drive in new[] { "B:", "C:" })
        {
            var key = $"disk-{drive.TrimEnd(':').ToLowerInvariant()}";
            Add(key, $"Disk {drive}\\", "read", "Read", MetricCategory.Disk, MetricUnit.MegabytesPerSecond, Bursty(0.08, 120));
            Add(key, $"Disk {drive}\\", "write", "Write", MetricCategory.Disk, MetricUnit.MegabytesPerSecond, Bursty(0.15, 60));
        }

        Add("host-activity", "Host Activity", "processes", "Processes", MetricCategory.System, MetricUnit.Count, r => 290 + r.Next(0, 40));
        Add("host-activity", "Host Activity", "threads", "Threads", MetricCategory.System, MetricUnit.Count, r => 4100 + r.Next(0, 600));
        Add("host-activity", "Host Activity", "handles", "Handles", MetricCategory.System, MetricUnit.Count, r => 148_000 + r.Next(0, 9000));
        foreach (var (drive, totalGb) in new[] { ("B", 1863.0), ("C", 929.9) })
        {
            var key = $"volume-{drive.ToLowerInvariant()}";
            Add(key, $"Drive {drive}:", "used-percent", "Used", MetricCategory.Disk, MetricUnit.Percent, _ => drive == "B" ? _driveB : _driveC);
            Add(key, $"Drive {drive}:", "used-gb", "Used GiB", MetricCategory.Disk, MetricUnit.Gigabytes, _ => totalGb * (drive == "B" ? _driveB : _driveC) / 100);
            Add(key, $"Drive {drive}:", "total-gb", "Total GiB", MetricCategory.Disk, MetricUnit.Gigabytes, _ => totalGb);
        }

        foreach (var adapter in new[] { "Intel[R] I211 Gigabit Network Connection _2", "Intel[R] Wireless-AC 9260 160MHz _2" })
        {
            var key = $"net-{Sanitize(adapter)}";
            var title = $"Network {adapter.Replace('_', ' ').Replace('#', ' ').Trim()}";
            Add(key, title, "download", "Down", MetricCategory.Network, MetricUnit.MegabitsPerSecond, Bursty(0.1, 300));
            Add(key, title, "upload", "Up", MetricCategory.Network, MetricUnit.MegabitsPerSecond, Bursty(0.05, 40));
        }

        Add("gpu-total", "GPU Usage", "usage", "Usage", MetricCategory.Gpu, MetricUnit.Percent, r => Math.Clamp((_cpuLoad * 0.6) + (r.NextDouble() * 6), 0, 100));
        Add("gpu-memory", "GPU Memory", "dedicated-gb", "Dedicated", MetricCategory.Gpu, MetricUnit.Gigabytes, r => 1.9 + (r.NextDouble() * 0.4));
        Add("gpu-temperature", "GPU Temperature", "temperature", "Temperature", MetricCategory.Gpu, MetricUnit.Celsius, r => 38 + (_cpuLoad * 0.3) + r.Next(0, 3));
    }

    public int SeriesCount => _series.Count;

    /// <summary>One scrape: a value for every series at <paramref name="at"/>.</summary>
    public void Sample(DateTimeOffset at, List<MetricSample> into)
    {
        // Mostly idle with sustained busy stretches and short spikes, like a desktop.
        var target = _random.NextDouble() < 0.002 ? 70 + (_random.NextDouble() * 30) : 6 + (_random.NextDouble() * 8);
        _cpuLoad = Math.Clamp(_cpuLoad + ((target - _cpuLoad) * 0.08), 0, 100);
        _memoryUsed = Math.Clamp(_memoryUsed + ((_random.NextDouble() - 0.5) * 0.02), 9, 28);
        _driveB = Math.Clamp(_driveB + (_random.NextDouble() * 0.000002), 0, 100);
        _driveC = Math.Clamp(_driveC + ((_random.NextDouble() - 0.45) * 0.00001), 0, 100);

        foreach (var series in _series)
        {
            into.Add(new MetricSample(series.PanelKey, series.PanelTitle, series.SeriesKey, series.SeriesName,
                series.Category, series.Unit, series.Next(_random), at));
        }
    }

    private Func<Random, double> Bursty(double chance, double peak) =>
        r => r.NextDouble() < chance ? r.NextDouble() * peak : r.NextDouble() * 0.2;

    private void Add(string panelKey, string panelTitle, string seriesKey, string seriesName,
        MetricCategory category, MetricUnit unit, Func<Random, double> next) =>
        _series.Add(new Series(panelKey, panelTitle, seriesKey, seriesName, category, unit, next));

    private static string Sanitize(string value) =>
        new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');

    private sealed record Series(
        string PanelKey,
        string PanelTitle,
        string SeriesKey,
        string SeriesName,
        MetricCategory Category,
        MetricUnit Unit,
        Func<Random, double> Next);
}
