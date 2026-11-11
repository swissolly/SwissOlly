using System.Diagnostics;
using SwissOlly.Collector;
using SwissOlly.Core.Interfaces;
using SwissOlly.Core.Models;

namespace MetricsCheck;

/// <summary>
/// Scrape spacing of the app's real scheduler: CollectorService with the real collector (or a
/// fake one with --fake) and a store that writes nothing. Reports spacing, jitter, drift against
/// the ideal schedule, and what happens after a sample stalls (--stall-seconds, fake only).
/// </summary>
internal static class IntervalCheck
{
    public static async Task<int> RunAsync(Options options)
    {
        var interval = (int)options.Number("interval", 1);
        var duration = TimeSpan.FromMinutes(options.Number("minutes", 5));
        var stallSeconds = options.Number("stall-seconds", 0);
        var fake = options.Flag("fake") || stallSeconds > 0;

        IMetricCollector inner = fake ? new FakeCollector(stallAtCall: 6, TimeSpan.FromSeconds(stallSeconds)) : new WindowsMetricCollector();
        var timed = new TimedCollector(inner);
        var events = new List<(double T, DateTimeOffset Stamp)>();
        var failures = new List<(double T, string Message)>();
        await using var service = new CollectorService(timed, new NullStore());
        service.SnapshotCollected += (_, snapshot) =>
        {
            lock (events)
            {
                events.Add((Clock.Seconds, snapshot.Timestamp));
            }
        };
        service.CollectionFailed += (_, error) =>
        {
            lock (failures)
            {
                failures.Add((Clock.Seconds, error.Message));
            }
        };

        Console.WriteLine($"CollectorService at {interval} s for {duration.TotalMinutes:0.#} min, {(fake ? "fake" : "real")} collector{(stallSeconds > 0 ? $", call 6 stalls {stallSeconds:0.#} s" : string.Empty)}.");
        var started = Clock.Seconds;
        await service.StartAsync(new SwissOllyConfig { ScrapeIntervalSeconds = interval }, CancellationToken.None);
        await Task.Delay(duration);
        await service.StopAsync();

        List<(double T, DateTimeOffset Stamp)> samples;
        lock (events)
        {
            samples = [.. events];
        }

        if (samples.Count < 3)
        {
            Console.WriteLine("Fewer than three samples.");
            return 1;
        }

        // Sample 0 is the immediate one at start; the timer's ticks follow. Each tick is placed on
        // the schedule by rounding (a stalled tick that was skipped leaves a hole, not a shift),
        // and a least-squares line through (slot, snapshot time) gives the real period: its
        // difference from the interval is drift, the residuals are jitter.
        var first = samples[0].T;
        var ticks = samples.Skip(1).ToList();
        var origin = ticks[0].Stamp;
        var slots = ticks.Select(s => Math.Round((s.Stamp - origin).TotalSeconds / interval)).ToList();
        var times = ticks.Select(s => (s.Stamp - origin).TotalSeconds).ToList();
        var (intercept, period) = Fit(slots, times);
        var residuals = slots.Select((k, i) => (times[i] - (intercept + (period * k))) * 1000).ToList();
        var stampSpacing = new List<double>();
        var eventSpacing = new List<double>();
        for (var i = 1; i < ticks.Count; i++)
        {
            stampSpacing.Add((ticks[i].Stamp - ticks[i - 1].Stamp).TotalMilliseconds);
            eventSpacing.Add((ticks[i].T - ticks[i - 1].T) * 1000);
        }

        var expected = (int)Math.Floor((Clock.Seconds - started) / interval) + 1;
        var missed = (int)slots[^1] + 1 - ticks.Count;
        Console.WriteLine($"Samples: {samples.Count} (about {expected} expected), first {(first - started) * 1000:0} ms after start, first tick {(ticks[0].T - first) * 1000:0} ms after that; {missed} tick(s) missed");
        if (samples.Count <= 40)
        {
            Console.WriteLine("  snapshot times (s from first tick): " + string.Join(", ", times.Select(t => t.ToString("0.000"))));
        }

        Console.WriteLine($"Period (least squares over {ticks.Count} ticks): {period * 1000:0.000} ms, drift {(period - interval) / interval * 3600 * 1000:+0.0;-0.0;0.0} ms per hour");
        Describe("Jitter, snapshot time vs the fitted schedule (ms)", residuals);
        Describe("Spacing, snapshot timestamps (ms)", stampSpacing);
        Describe("Spacing, SnapshotCollected events (ms)", eventSpacing);
        Console.WriteLine($"Collect duration: mean {timed.Durations.Average():0.0} ms, max {timed.Durations.Max():0.0} ms over {timed.Durations.Count} calls");
        foreach (var (t, message) in failures)
        {
            Console.WriteLine($"Failure at {t - first:0.000} s: {message}");
        }

        return 0;
    }

    private static (double Intercept, double Slope) Fit(List<double> x, List<double> y)
    {
        var meanX = x.Average();
        var meanY = y.Average();
        var sxx = x.Sum(v => (v - meanX) * (v - meanX));
        var sxy = x.Select((v, i) => (v - meanX) * (y[i] - meanY)).Sum();
        var slope = sxx == 0 ? 0 : sxy / sxx;
        return (meanY - (slope * meanX), slope);
    }

    private static void Describe(string label, List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var p99 = sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(sorted.Count * 0.99) - 1)];
        Console.WriteLine($"{label}: mean {Stats.Mean(values):0.0}, sd {Stats.StdDev(values):0.0}, min {sorted[0]:0.0}, p99 {p99:0.0}, max {sorted[^1]:0.0}");
    }

    private sealed class TimedCollector(IMetricCollector inner) : IMetricCollector
    {
        public List<double> Durations { get; } = [];

        public async Task<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            var start = Stopwatch.GetTimestamp();
            var snapshot = await inner.CollectAsync(cancellationToken);
            lock (Durations)
            {
                Durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            return snapshot;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FakeCollector(int stallAtCall, TimeSpan stall) : IMetricCollector
    {
        private int _calls;

        public Task<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            // Stamped on entry and synchronous, like the real collector.
            var now = DateTimeOffset.UtcNow;
            if (Interlocked.Increment(ref _calls) == stallAtCall && stall > TimeSpan.Zero)
            {
                Thread.Sleep(stall);
            }

            return Task.FromResult(new MetricSnapshot(now, [new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, 1, now)]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullStore : IMetricStore
    {
        public Task InitializeAsync(SwissOllyConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendSnapshotAsync(MetricSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSeriesHistory>>([]);

        public Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan bucket, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSeriesHistory>>([]);

        public Task PruneAsync(SwissOllyConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
