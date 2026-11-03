using SwissOlly.Core.Models;

namespace SwissOlly.Tests;

public class ChartPointReducerTests
{
    [Fact]
    public void DenseCreepingRun_IsNotCollapsedIntoOnePoint()
    {
        // Live samples on a 1h window: 0.5px apart, 40 of them spanning 19.5px.
        var xs = Enumerable.Range(0, 40).Select(i => 100 + (i * 0.5)).ToArray();

        var kept = ChartPointReducer.CollapseByX(xs, 0.8);

        // One point per ~0.8px cluster, so the stretch still draws as a line.
        Assert.True(kept.Length >= 20, $"kept {kept.Length}");
        Assert.Equal(xs.Length - 1, kept[^1]);
    }

    [Fact]
    public void PointsWithinOneCluster_KeepTheMostRecent()
    {
        var kept = ChartPointReducer.CollapseByX([10.0, 10.3, 10.6, 20.0], 0.8);

        Assert.Equal(new[] { 2, 3 }, kept);
    }

    [Fact]
    public void SpacedPoints_AreAllKept()
    {
        var kept = ChartPointReducer.CollapseByX([0.0, 2.0, 4.0, 6.0], 0.8);

        Assert.Equal(new[] { 0, 1, 2, 3 }, kept);
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        Assert.Empty(ChartPointReducer.CollapseByX([], 0.8));
    }

    private static readonly DateTimeOffset Noon = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static IEnumerable<MetricPoint> Run(DateTimeOffset from, TimeSpan spacing, int count) =>
        Enumerable.Range(0, count).Select(i => new MetricPoint(from + (spacing * i), i));

    [Fact]
    public void Two_Outages_A_Few_Samples_Apart_Both_Break_The_Line()
    {
        // The shape that drew a bridging line in the real history: minute averages, an 86-minute
        // outage, a 25-second launch at 2s samples, a 126-minute outage, then live samples again.
        var points = Run(Noon, TimeSpan.FromMinutes(1), 60)
            .Concat(Run(Noon.AddMinutes(59 + 86), TimeSpan.FromSeconds(2), 13))
            .Concat(Run(Noon.AddMinutes(59 + 86 + 126), TimeSpan.FromSeconds(2), 200))
            .ToArray();

        var runs = ChartPointReducer.SplitAtGaps(points, minimumGapMs: 8 * 60_000);

        Assert.Equal(3, runs.Count);
        Assert.Equal(new[] { 60, 13, 200 }, runs.Select(r => r.Count));
    }

    [Fact]
    public void History_Turning_Into_Live_Samples_Is_Not_A_Gap()
    {
        // Minute averages up to the six-hour mark, then 2s samples starting 37s later.
        var points = Run(Noon, TimeSpan.FromMinutes(1), 30)
            .Concat(Run(Noon.AddMinutes(29).AddSeconds(37), TimeSpan.FromSeconds(2), 50))
            .ToArray();

        Assert.Single(ChartPointReducer.SplitAtGaps(points, minimumGapMs: 0));
    }

    [Fact]
    public void A_Slow_Scrape_Interval_Is_Not_Mistaken_For_Gaps()
    {
        var points = Run(Noon, TimeSpan.FromSeconds(60), 40).ToArray();

        Assert.Single(ChartPointReducer.SplitAtGaps(points, minimumGapMs: 5_000));
    }

    [Fact]
    public void Gaps_Narrower_Than_The_Minimum_Stay_Joined()
    {
        // A 3-minute hole is real, but on a 12h chart it is under two buckets wide.
        var points = Run(Noon, TimeSpan.FromSeconds(2), 30)
            .Concat(Run(Noon.AddMinutes(4), TimeSpan.FromSeconds(2), 30))
            .ToArray();

        Assert.Single(ChartPointReducer.SplitAtGaps(points, minimumGapMs: 8 * 60_000));
        Assert.Equal(2, ChartPointReducer.SplitAtGaps(points, minimumGapMs: 0).Count);
    }
}
