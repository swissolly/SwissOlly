using System.Diagnostics;
using System.Runtime.InteropServices;
using SwissOlly.Collector;

namespace MetricsCheck;

/// <summary>
/// Memory used and available, once a second, against GlobalMemoryStatusEx and the PDH Memory
/// counters Task Manager's Memory page is built from. Optionally a helper process holds a few
/// GiB in the middle of the run so the numbers move.
/// </summary>
internal static class MemoryCheck
{
    private const double GiB = 1024d * 1024d * 1024d;

    public static async Task<int> RunAsync(Options options)
    {
        var seconds = (int)options.Number("seconds", 40);
        var allocGb = options.Number("alloc-gb", 0);

        await using var collector = new WindowsMetricCollector();
        using var pdh = new PdhQuery();
        var availableCounter = pdh.Add(@"\Memory\Available Bytes");
        var modifiedCounter = pdh.Add(@"\Memory\Modified Page List Bytes");
        var committedCounter = pdh.Add(@"\Memory\Committed Bytes");
        var commitLimitCounter = pdh.Add(@"\Memory\Commit Limit");
        pdh.Collect();

        Console.WriteLine("Task Manager: Available = \\Memory\\Available Bytes (standby + free); In use = total - available - modified;");
        Console.WriteLine("the Memory % = (total - available) / total. All sizes GiB (Task Manager labels these GB).");
        Console.WriteLine();
        Console.WriteLine("    t | SO used  SO avail SO total |  OS avail  PDH avail  TM in use  modified | SO %   OS load | d used  d avail (MiB) | committed / limit");

        var usedDiffs = new List<double>();
        var availDiffs = new List<double>();
        var inUseDiffs = new List<double>();
        var percentDiffs = new List<double>();
        Process? holder = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            for (var i = 0; i < seconds; i++)
            {
                await timer.WaitForNextTickAsync();
                if (allocGb > 0 && i == 5)
                {
                    holder = StartHolder(pdh, committedCounter, commitLimitCounter, allocGb, Math.Max(5, seconds - 15));
                }

                var snapshot = await collector.CollectAsync(CancellationToken.None);
                var (totalPhys, availPhys, memoryLoad) = Native.MemoryStatus();
                pdh.Collect();

                var used = Snapshots.Value(snapshot, "memory", "used-gb");
                var available = Snapshots.Value(snapshot, "memory", "available-gb");
                var total = Snapshots.Value(snapshot, "memory", "total-gb");
                var pdhAvailable = (pdh.Single(availableCounter) ?? double.NaN) / GiB;
                var modified = (pdh.Single(modifiedCounter) ?? double.NaN) / GiB;
                var committed = (pdh.Single(committedCounter) ?? double.NaN) / GiB;
                var commitLimit = (pdh.Single(commitLimitCounter) ?? double.NaN) / GiB;
                var osTotal = totalPhys / GiB;
                var osAvailable = availPhys / GiB;
                var taskManagerInUse = osTotal - pdhAvailable - modified;
                // The app's Memory card: used / (used + available).
                var appPercent = used / (used + available) * 100d;

                var usedDiff = (used - (osTotal - pdhAvailable)) * 1024d;
                var availDiff = (available - pdhAvailable) * 1024d;
                usedDiffs.Add(usedDiff);
                availDiffs.Add(availDiff);
                inUseDiffs.Add((used - taskManagerInUse) * 1024d);
                percentDiffs.Add(appPercent - ((osTotal - pdhAvailable) / osTotal * 100d));

                Console.WriteLine(
                    $"{Clock.Seconds,5:0.0} | {used,7:0.000} {available,8:0.000} {total,8:0.000} | {osAvailable,8:0.000} {pdhAvailable,9:0.000} {taskManagerInUse,10:0.000} {modified,9:0.000} | {appPercent,5:0.0} {memoryLoad,5}%  | {Math.Round(usedDiff),6:+0;-0;0} {Math.Round(availDiff),7:+0;-0;0}        | {committed,6:0.00} / {commitLimit,6:0.00}");
            }
        }
        finally
        {
            if (holder is { HasExited: false })
            {
                holder.Kill();
            }
        }

        Console.WriteLine();
        Report("Used vs total - \\Memory\\Available Bytes", usedDiffs, "MiB");
        Report("Available vs \\Memory\\Available Bytes", availDiffs, "MiB");
        Report("Used vs Task Manager's In use (it leaves out the modified list)", inUseDiffs, "MiB");
        Report("Memory % (app card) vs (total - available) / total", percentDiffs, "points");
        return 0;
    }

    private static void Report(string label, List<double> diffs, string unit)
    {
        Console.WriteLine($"{label}: mean {Math.Round(Stats.Mean(diffs), 1):+0.0;-0.0;0.0} {unit}, max |d| {diffs.Max(Math.Abs):0.0} {unit} over {diffs.Count} samples");
    }

    private static Process? StartHolder(PdhQuery pdh, nint committed, nint commitLimit, double gigabytes, int holdSeconds)
    {
        // Never push the machine towards its commit limit or out of free memory: a game may be running.
        var headroom = ((pdh.Single(commitLimit) ?? 0) - (pdh.Single(committed) ?? 0)) / GiB;
        var available = Native.MemoryStatus().AvailPhys / GiB;
        if (headroom < gigabytes + 2 || available < gigabytes + 4)
        {
            Console.WriteLine($"  -- not allocating {gigabytes:0.#} GiB: commit headroom {headroom:0.0} GiB, available {available:0.0} GiB");
            return null;
        }

        Console.WriteLine($"  -- helper process allocating {gigabytes:0.#} GiB for {holdSeconds} s");
        return Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"hold {gigabytes} {holdSeconds}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>The helper: commits and touches the memory so it is resident, holds it, exits.</summary>
    public static int Hold(string[] args)
    {
        var gigabytes = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var seconds = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        const int chunk = 256 * 1024 * 1024;
        var chunks = (int)Math.Ceiling(gigabytes * GiB / chunk);
        var blocks = new List<nint>(chunks);
        try
        {
            for (var i = 0; i < chunks; i++)
            {
                var block = Marshal.AllocHGlobal(chunk);
                blocks.Add(block);
                for (var offset = 0; offset < chunk; offset += 4096)
                {
                    Marshal.WriteByte(block, offset, 1);
                }
            }

            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            foreach (var block in blocks)
            {
                Marshal.FreeHGlobal(block);
            }
        }

        return 0;
    }
}
