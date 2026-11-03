namespace SwissOlly.Tests;

public sealed class MetricHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(60, 1)]
    [InlineData(1440, 1)]
    [InlineData(10080, 6)]
    [InlineData(43200, 22)]
    [InlineData(525600, 263)]
    public void Bucket_Keeps_A_Range_Near_Two_Thousand_Points(int rangeMinutes, int expectedBucketMinutes)
    {
        var bucket = MetricHistory.BucketFor(TimeSpan.FromMinutes(rangeMinutes));

        Assert.Equal(TimeSpan.FromMinutes(expectedBucketMinutes), bucket);
        Assert.True(rangeMinutes / bucket.TotalMinutes <= 2000);
    }

    [Fact]
    public void Concat_Joins_Reads_Without_Repeating_The_Seam()
    {
        var older = new[] { Panel("cpu-total", ("usage", new[] { 0, 1, 2, 3 })) };
        var newer = new[] { Panel("cpu-total", ("usage", new[] { 3, 4 })) };

        var merged = MetricHistory.Concat(older, newer);

        var points = Assert.Single(Assert.Single(merged).Series).Points;
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, points.Select(p => (int)(p.Timestamp - Noon).TotalMinutes));
    }

    [Fact]
    public void Concat_Keeps_Panels_And_Series_That_Only_One_Read_Has()
    {
        var older = new[] { Panel("cpu-total", ("usage", new[] { 0, 1 })), Panel("gpu-total", ("usage", new[] { 0 })) };
        var newer = new[] { Panel("cpu-total", ("usage", new[] { 2 }), ("other", new[] { 2 })), Panel("memory", ("used-gb", new[] { 2 })) };

        var merged = MetricHistory.Concat(older, newer);

        Assert.Equal(new[] { "cpu-total", "gpu-total", "memory" }, merged.Select(p => p.PanelKey).OrderBy(k => k));
        var cpu = merged.Single(p => p.PanelKey == "cpu-total");
        Assert.Equal(3, cpu.Series.Single(s => s.SeriesKey == "usage").Points.Count);
        Assert.Single(cpu.Series.Single(s => s.SeriesKey == "other").Points);
    }

    [Fact]
    public void Compact_Averages_Each_Minute_At_The_Minute_Start_And_Leaves_The_Rest()
    {
        // Every 10 seconds from 12:00:05 to 12:02:55, value = position.
        var points = Enumerable.Range(0, 18).Select(i => new MetricPoint(Noon.AddSeconds(5 + (i * 10)), i)).ToList();

        var removed = MetricHistory.Compact(points, DateTimeOffset.MinValue, Noon.AddMinutes(2), TimeSpan.FromMinutes(1));

        Assert.Equal(10, removed);
        Assert.Equal(Noon, points[0].Timestamp);
        Assert.Equal(2.5, points[0].Value, 9);            // average of 0..5
        Assert.Equal(Noon.AddMinutes(1), points[1].Timestamp);
        Assert.Equal(8.5, points[1].Value, 9);            // average of 6..11
        Assert.Equal(Noon.AddSeconds(125), points[2].Timestamp); // 12:02 onward untouched
        Assert.Equal(8, points.Count);
    }

    [Fact]
    public void Compact_Is_A_No_Op_On_Points_Already_Folded()
    {
        var points = Enumerable.Range(0, 30).Select(i => new MetricPoint(Noon.AddSeconds(i * 7), i % 5)).ToList();
        MetricHistory.Compact(points, DateTimeOffset.MinValue, Noon.AddMinutes(3), TimeSpan.FromMinutes(1));
        var folded = points.ToArray();

        var removed = MetricHistory.Compact(points, DateTimeOffset.MinValue, Noon.AddMinutes(3), TimeSpan.FromMinutes(1));

        Assert.Equal(0, removed);
        Assert.Equal(folded, points);
    }

    [Fact]
    public void Compact_Coarsens_Minute_Averages_Into_Wider_Buckets()
    {
        var minutes = Enumerable.Range(0, 20).Select(i => new MetricPoint(Noon.AddMinutes(i), i)).ToList();

        MetricHistory.Compact(minutes, DateTimeOffset.MinValue, Noon.AddMinutes(20), TimeSpan.FromMinutes(10));

        Assert.Equal(new[] { 4.5, 14.5 }, minutes.Select(p => p.Value));
        Assert.Equal(new[] { Noon, Noon.AddMinutes(10) }, minutes.Select(p => p.Timestamp));
    }

    private static MetricSeriesHistory Panel(string key, params (string Series, int[] Minutes)[] series) =>
        new(key, key, MetricCategory.Cpu, MetricUnit.Percent,
            series.Select(s => new MetricSeriesHistoryItem(
                s.Series,
                s.Series,
                s.Minutes.Select(m => new MetricPoint(Noon.AddMinutes(m), m)).ToArray())).ToArray());
}
