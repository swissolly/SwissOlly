using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml.Media;

namespace SwissOlly.App;

// TEMPORARY perf probe (SWISSOLLY_PERF_PROBE=1). Remove before finishing.
internal static class PerfProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SWISSOLLY_PERF_PROBE") == "1";
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static readonly object s_gate = new();
    private static readonly string s_path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swissolly-perf", $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static long Now => s_clock.ElapsedTicks;

    public static double Ms(long startTicks) => (s_clock.ElapsedTicks - startTicks) * 1000d / Stopwatch.Frequency;

    public static void Log(string line)
    {
        if (!Enabled)
        {
            return;
        }

        lock (s_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(s_path)!);
            File.AppendAllText(s_path, line + Environment.NewLine);
        }
    }

    public static void Step(string name, long start)
    {
        if (Enabled)
        {
            Log($"    {name}: {Ms(start):0.000} ms");
        }
    }

    private static readonly Dictionary<string, double> s_totals = [];

    public static void Add(string name, long start)
    {
        if (Enabled)
        {
            s_totals[name] = s_totals.GetValueOrDefault(name) + Ms(start);
        }
    }

    public static void FlushTotals()
    {
        if (!Enabled)
        {
            return;
        }

        foreach (var (name, ms) in s_totals)
        {
            Log($"      {name} (sum): {ms:0.000} ms");
        }

        s_totals.Clear();
    }

    /// <summary>Logs when the next frames start and when the queue reaches idle, relative to start.</summary>
    public static void TrackAfter(string label, long start, Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        if (!Enabled)
        {
            return;
        }

        var frames = new List<double>();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            frames.Add(Ms(start));
            if (frames.Count >= 4)
            {
                CompositionTarget.Rendering -= handler;
                Log($"  {label} frames at: {string.Join(", ", frames.Select(f => f.ToString("0.0")))} ms");
            }
        };
        CompositionTarget.Rendering += handler;
        _ = queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Log($"  {label} idle(Low) at: {Ms(start):0.0} ms"));
    }
}
