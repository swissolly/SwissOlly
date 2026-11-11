using System.Diagnostics;
using System.Globalization;
using MetricsCheck;
using SwissOlly.Core.Models;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var options = Options.Parse(args.Skip(1));
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

return command switch
{
    "memory" => await MemoryCheck.RunAsync(options),
    "hold" => MemoryCheck.Hold(args),
    "disk" => await DiskCheck.RunAsync(options),
    "capacity" => await CapacityCheck.RunAsync(),
    "network" => await NetworkCheck.RunAsync(options),
    "gpu" => await GpuCheck.RunAsync(options),
    "interval" => await IntervalCheck.RunAsync(options),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        MetricsCheck: compares SwissOlly's collector against Windows' own numbers.

          memory   [--seconds 40] [--alloc-gb 0]   used/available vs GlobalMemoryStatusEx and PDH
          disk     [--size-gb 2] [--dir <folder>]  throughput vs a timed write, read and copy
          capacity                                 drive sizes vs DriveInfo and Explorer
          network  [--seconds 60]                  per-adapter rates vs PDH and the adapter's byte counters
          gpu      [--seconds 60]                  usage, VRAM and temperature vs PDH, the kernel and nvidia-smi
          interval [--interval 1] [--minutes 5] [--fake] [--stall-seconds 0]
                                                   scrape spacing of CollectorService
        """);
    return 1;
}

namespace MetricsCheck
{
    internal static class Clock
    {
        private static readonly Stopwatch Watch = Stopwatch.StartNew();

        public static double Seconds => Watch.Elapsed.TotalSeconds;
    }

    internal sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            string? pending = null;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pending is not null)
                    {
                        options._values[pending] = "true";
                    }

                    pending = arg[2..];
                }
                else if (pending is not null)
                {
                    options._values[pending] = arg;
                    pending = null;
                }
            }

            if (pending is not null)
            {
                options._values[pending] = "true";
            }

            return options;
        }

        public double Number(string name, double fallback) =>
            _values.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        public string? Text(string name) => _values.GetValueOrDefault(name);

        public bool Flag(string name) => _values.ContainsKey(name);
    }

    internal static class Snapshots
    {
        public static double Value(MetricSnapshot snapshot, string panelKey, string seriesKey)
        {
            foreach (var sample in snapshot.Samples)
            {
                if (sample.PanelKey == panelKey && sample.SeriesKey == seriesKey)
                {
                    return sample.Value;
                }
            }

            return double.NaN;
        }

        /// <summary>The collector's panel key for a counter instance (its Sanitize rule).</summary>
        public static string Sanitize(string value)
        {
            var chars = value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
            return new string(chars).Trim('-');
        }
    }

    internal static class Stats
    {
        public static double Mean(IReadOnlyCollection<double> values) => values.Count == 0 ? double.NaN : values.Average();

        public static double StdDev(IReadOnlyCollection<double> values)
        {
            if (values.Count < 2)
            {
                return 0;
            }

            var mean = values.Average();
            return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
        }

        public static double Percent(double value, double reference) =>
            reference == 0 ? (value == 0 ? 0 : double.PositiveInfinity) : (value - reference) / reference * 100d;
    }
}
