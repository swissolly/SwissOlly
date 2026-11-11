using SwissOlly.Collector;

namespace MetricsCheck;

/// <summary>
/// Drive capacity: the collector's Drive panels against DriveInfo, and what Explorer prints for
/// the same bytes. Explorer's "GB" is 1024-based, i.e. GiB.
/// </summary>
internal static class CapacityCheck
{
    private const double GiB = 1024d * 1024d * 1024d;

    public static async Task<int> RunAsync()
    {
        await using var collector = new WindowsMetricCollector();
        var snapshot = await collector.CollectAsync(CancellationToken.None);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            var label = drive.Name.TrimEnd('\\');
            var panel = $"volume-{Snapshots.Sanitize(label)}";
            var soTotal = Snapshots.Value(snapshot, panel, "total-gb");
            var soUsed = Snapshots.Value(snapshot, panel, "used-gb");
            var soPercent = Snapshots.Value(snapshot, panel, "used-percent");
            var total = drive.TotalSize;
            var free = drive.TotalFreeSpace;
            var used = total - free;

            Console.WriteLine($"Drive {label} ({drive.DriveFormat}, \"{drive.VolumeLabel}\")");
            Console.WriteLine($"  DriveInfo   : total {total:N0} B, free {free:N0} B (available to this user {drive.AvailableFreeSpace:N0} B)");
            Console.WriteLine($"  Explorer    : {Native.ExplorerSize(free)} free of {Native.ExplorerSize(total)}, used {Native.ExplorerSize(used)}");
            Console.WriteLine($"  1024-based  : total {total / GiB:0.000} GiB, used {used / GiB:0.000} GiB, {used * 100d / total:0.00}% used");
            Console.WriteLine($"  1000-based  : total {total / 1e9:0.000} GB (the size on the box)");
            Console.WriteLine($"  SwissOlly   : total {soTotal:0.000}, used {soUsed:0.000}, {soPercent:0.00}% used (GiB; the panel shows \"{AppCapacity(soUsed)}\" of \"{AppCapacity(soTotal)}\")");
            Console.WriteLine($"  difference  : total {Math.Round((soTotal - (total / GiB)) * 1024, 1):+0.0;-0.0;0.0} MiB, used {Math.Round((soUsed - (used / GiB)) * 1024, 1):+0.0;-0.0;0.0} MiB, percent {Math.Round(soPercent - (used * 100d / total), 3):+0.000;-0.000;0.000} points");
            Console.WriteLine();
        }

        return 0;
    }

    // The app's FormatCapacity (TelemetryPanelCard, TelemetryChart).
    private static string AppCapacity(double gigabytes) =>
        gigabytes >= 1024d ? $"{gigabytes / 1024d:0.0} TiB" : $"{gigabytes:0.0} GiB";
}
