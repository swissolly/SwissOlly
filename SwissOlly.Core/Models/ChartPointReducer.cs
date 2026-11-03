namespace SwissOlly.Core.Models;

/// <summary>
/// Pure geometry helpers for the telemetry chart, kept here so they can be unit-tested.
/// </summary>
public static class ChartPointReducer
{
    /// <summary>
    /// Indices of the points to draw after collapsing points that land within
    /// <paramref name="minSpacing"/> pixels of each other on X. Each cluster is measured from its
    /// <em>first</em> point and represented by its last (most recent) one.
    /// </summary>
    /// <remarks>
    /// Measuring from the previous point instead chained: on a 1h+ window live samples land
    /// ~0.5px apart, every one was "close to the last", and a whole live stretch collapsed into a
    /// single point - drawn as a lone dot under the latest-value marker, with no line or fill.
    /// </remarks>
    public static int[] CollapseByX(IReadOnlyList<double> xs, double minSpacing)
    {
        if (xs.Count == 0)
        {
            return [];
        }

        var kept = new List<int>(xs.Count) { 0 };
        var anchorX = xs[0];
        for (var index = 1; index < xs.Count; index++)
        {
            if (Math.Abs(xs[index] - anchorX) < minSpacing)
            {
                kept[^1] = index;
                continue;
            }

            anchorX = xs[index];
            kept.Add(index);
        }

        return [.. kept];
    }

    /// <summary>
    /// Splits a series into runs at outages (the app was closed, the PC slept), so the chart
    /// leaves a gap there instead of drawing a straight line across it. Gaps narrower than
    /// <paramref name="minimumGapMs"/> stay joined: at the current zoom they would not show.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<MetricPoint>> SplitAtGaps(
        IReadOnlyList<MetricPoint> points,
        double minimumGapMs)
    {
        if (points.Count <= 1)
        {
            return [points];
        }

        var starts = FindRunStarts(points, 0, points.Count, minimumGapMs);
        if (starts.Count == 0)
        {
            return [points];
        }

        var runs = new List<IReadOnlyList<MetricPoint>>(starts.Count + 1);
        var runStart = 0;
        foreach (var start in starts)
        {
            runs.Add(Slice(points, runStart, start));
            runStart = start;
        }

        runs.Add(Slice(points, runStart, points.Count));
        return runs;
    }

    /// <summary>
    /// Indices in [<paramref name="from"/>, <paramref name="to"/>) where a new run begins after
    /// an outage, in order; <paramref name="from"/> itself is never listed. Works in place on the
    /// stored points, so it costs nothing to call on every redraw.
    /// </summary>
    public static List<int> FindRunStarts(IReadOnlyList<MetricPoint> points, int from, int to, double minimumGapMs)
    {
        var starts = new List<int>();
        for (var i = Math.Max(from, 0) + 1; i < Math.Min(to, points.Count); i++)
        {
            if (IsGap(points, i, minimumGapMs))
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    /// <summary>
    /// True when the interval ending at <paramref name="index"/> is a real outage: over 10s, over
    /// <paramref name="minimumGapMs"/>, and more than 3x the local sample spacing. The spacing is
    /// the coarser of the two sides' tightest nearby intervals, so a series that switches from
    /// minute averages (history) to live samples does not break at the switch, while an outage
    /// next to another outage still finds the normal spacing beside it.
    /// </summary>
    /// <remarks>
    /// One median for the whole series made every history point a gap (scattered dots). Judging
    /// gaps after downsampling against the largest neighbouring interval let two outages a few
    /// samples apart hide each other, which drew a bridging line across both.
    /// </remarks>
    private static bool IsGap(IReadOnlyList<MetricPoint> points, int index, double minimumGapMs)
    {
        const double FloorMs = 10_000d;
        var intervalMs = (points[index].Timestamp - points[index - 1].Timestamp).TotalMilliseconds;
        if (intervalMs <= FloorMs || intervalMs <= minimumGapMs)
        {
            return false;
        }

        var before = NeighbourIntervalMinMs(points, index, -1);
        var after = NeighbourIntervalMinMs(points, index, +1);
        var typicalMs = double.IsPositiveInfinity(before) ? after
            : double.IsPositiveInfinity(after) ? before
            : Math.Max(before, after);
        return intervalMs > typicalMs * 3d;
    }

    /// <summary>
    /// Smallest positive interval among up to three beside <paramref name="index"/> in one
    /// direction, or infinity when there are none. A run's spacing is uniform (the scrape interval,
    /// or a minute for history), so the smallest nearby interval is that spacing.
    /// </summary>
    private static double NeighbourIntervalMinMs(IReadOnlyList<MetricPoint> points, int index, int direction)
    {
        var smallest = double.PositiveInfinity;
        var seen = 0;
        for (var k = index + direction; seen < 3 && k >= 1 && k < points.Count; k += direction, seen++)
        {
            var intervalMs = (points[k].Timestamp - points[k - 1].Timestamp).TotalMilliseconds;
            if (intervalMs > 0d)
            {
                smallest = Math.Min(smallest, intervalMs);
            }
        }

        return smallest;
    }

    private static MetricPoint[] Slice(IReadOnlyList<MetricPoint> points, int start, int end)
    {
        var slice = new MetricPoint[end - start];
        for (var i = 0; i < slice.Length; i++)
        {
            slice[i] = points[start + i];
        }

        return slice;
    }
}
