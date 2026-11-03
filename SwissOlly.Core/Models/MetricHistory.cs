namespace SwissOlly.Core.Models;

/// <summary>Helpers for history reads that span more than one resolution.</summary>
public static class MetricHistory
{
    /// <summary>
    /// A bucket for averaging <paramref name="range"/> down to at most about
    /// <paramref name="maxPoints"/> points per series: whole minutes, never under one minute (the
    /// resolution history is stored at).
    /// </summary>
    public static TimeSpan BucketFor(TimeSpan range, int maxPoints = 2000)
    {
        var minutes = Math.Max(1d, Math.Ceiling(range.TotalMinutes / Math.Max(1, maxPoints)));
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// Replaces the points in [<paramref name="from"/>, <paramref name="to"/>) with one average per
    /// <paramref name="bucket"/>, timestamped at the bucket's start. That is how the store rolls
    /// samples into minutes and averages a wide read, so a stretch folded here in memory looks
    /// exactly as it will after a restart reloads it. Points must be in time order. Returns how
    /// many points were removed.
    /// </summary>
    public static int Compact(List<MetricPoint> points, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var bucketMs = (long)bucket.TotalMilliseconds;
        if (bucketMs <= 0 || points.Count < 2 || to <= from)
        {
            return 0;
        }

        var start = LowerBound(points, from);
        var end = LowerBound(points, to);
        if (end - start < 1)
        {
            return 0;
        }

        var compacted = new List<MetricPoint>();
        var unchanged = true;
        var index = start;
        while (index < end)
        {
            var bucketStart = points[index].Timestamp.ToUnixTimeMilliseconds() / bucketMs * bucketMs;
            var sum = 0d;
            var count = 0;
            while (index < end && points[index].Timestamp.ToUnixTimeMilliseconds() / bucketMs * bucketMs == bucketStart)
            {
                sum += points[index].Value;
                count++;
                index++;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(bucketStart);
            unchanged &= count == 1 && points[index - 1].Timestamp == timestamp;
            compacted.Add(new MetricPoint(timestamp, sum / count));
        }

        // Already folded (each bucket one point at its start): leave the list alone.
        if (unchanged)
        {
            return 0;
        }

        points.RemoveRange(start, end - start);
        points.InsertRange(start, compacted);
        return (end - start) - compacted.Count;
    }

    private static int LowerBound(List<MetricPoint> points, DateTimeOffset timestamp)
    {
        var lo = 0;
        var hi = points.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (points[mid].Timestamp < timestamp)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// Joins two reads of adjacent periods, older first, into one history per panel and series.
    /// Points the newer read already covers are dropped from the older one, so the seam never
    /// repeats or reorders a timestamp.
    /// </summary>
    public static IReadOnlyList<MetricSeriesHistory> Concat(
        IReadOnlyList<MetricSeriesHistory> older,
        IReadOnlyList<MetricSeriesHistory> newer)
    {
        if (older.Count == 0)
        {
            return newer;
        }

        if (newer.Count == 0)
        {
            return older;
        }

        var newerByPanel = newer.ToDictionary(panel => panel.PanelKey, StringComparer.OrdinalIgnoreCase);
        var merged = new List<MetricSeriesHistory>(Math.Max(older.Count, newer.Count));
        foreach (var olderPanel in older)
        {
            if (!newerByPanel.Remove(olderPanel.PanelKey, out var newerPanel))
            {
                merged.Add(olderPanel);
                continue;
            }

            var newerSeries = newerPanel.Series.ToDictionary(series => series.SeriesKey, StringComparer.OrdinalIgnoreCase);
            var series = new List<MetricSeriesHistoryItem>();
            foreach (var olderItem in olderPanel.Series)
            {
                if (!newerSeries.Remove(olderItem.SeriesKey, out var newerItem))
                {
                    series.Add(olderItem);
                    continue;
                }

                var firstNewer = newerItem.Points.Count > 0 ? newerItem.Points[0].Timestamp : DateTimeOffset.MaxValue;
                var points = new List<MetricPoint>(olderItem.Points.Count + newerItem.Points.Count);
                foreach (var point in olderItem.Points)
                {
                    if (point.Timestamp < firstNewer)
                    {
                        points.Add(point);
                    }
                }

                points.AddRange(newerItem.Points);
                series.Add(new MetricSeriesHistoryItem(newerItem.SeriesKey, newerItem.SeriesName, points));
            }

            series.AddRange(newerSeries.Values);
            merged.Add(new MetricSeriesHistory(newerPanel.PanelKey, newerPanel.PanelTitle, newerPanel.Category, newerPanel.Unit, series));
        }

        merged.AddRange(newerByPanel.Values);
        return merged;
    }
}
