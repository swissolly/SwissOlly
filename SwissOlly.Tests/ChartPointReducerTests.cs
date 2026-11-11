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

    [Fact]
    public void A_Quick_Restart_Does_Not_Cut_The_Newest_Samples_Off()
    {
        // 15s samples; the app is closed and reopened inside one interval, so the old copy's last
        // sample and the new copy's first are 1.5s apart. Each 15s sample after that was drawn as
        // a lone dot until three regular intervals had passed.
        var beforeRestart = Run(Noon, TimeSpan.FromSeconds(15), 20).ToList();
        var reopened = beforeRestart[^1].Timestamp.AddSeconds(1.5);

        for (var samplesSince = 1; samplesSince <= 5; samplesSince++)
        {
            var points = beforeRestart.Concat(Run(reopened, TimeSpan.FromSeconds(15), samplesSince)).ToArray();

            Assert.Single(ChartPointReducer.SplitAtGaps(points, minimumGapMs: 10_000));
        }
    }

    [Fact]
    public void The_Newest_Sample_After_A_Real_Outage_Still_Starts_A_New_Run()
    {
        // One sample so far since the PC woke: nothing after it to compare with.
        var points = Run(Noon, TimeSpan.FromSeconds(15), 20)
            .Append(new MetricPoint(Noon.AddMinutes(30), 0))
            .ToArray();

        Assert.Equal(2, ChartPointReducer.SplitAtGaps(points, minimumGapMs: 10_000).Count);
    }

    [Fact]
    public void A_Line_Crossing_The_Window_Edge_Is_Carried_To_The_Edge()
    {
        // 15s samples that drift a little late, on a 1m window ending at the newest: the fifth
        // sample back lands 0.4s outside the window, which left the first quarter of it empty.
        var points = Run(Noon, TimeSpan.FromSeconds(15.1), 8).ToArray();
        var start = points[^1].Timestamp.AddMinutes(-1);

        var edge = ChartPointReducer.InterpolateAtEdge(points, 4, start, minimumGapMs: 10_000);

        Assert.NotNull(edge);
        Assert.Equal(start, edge.Timestamp);
        Assert.InRange(edge.Value, 3d, 4d);
        Assert.Equal(3d + (0.4 / 15.1), edge.Value, 6);
    }

    [Fact]
    public void No_Edge_Point_Across_An_Outage_Or_Without_A_Sample_Beyond()
    {
        var points = Run(Noon, TimeSpan.FromSeconds(15), 8)
            .Concat(Run(Noon.AddMinutes(10), TimeSpan.FromSeconds(15), 8))
            .ToArray();

        // Index 8 starts the run after the outage: the line was never drawn across it.
        Assert.Null(ChartPointReducer.InterpolateAtEdge(points, 8, Noon.AddMinutes(9), minimumGapMs: 10_000));
        // Nothing before the first sample or after the last.
        Assert.Null(ChartPointReducer.InterpolateAtEdge(points, 0, Noon.AddSeconds(-5), minimumGapMs: 10_000));
        Assert.Null(ChartPointReducer.InterpolateAtEdge(points, points.Length, Noon.AddHours(1), minimumGapMs: 10_000));
        // A sample exactly on the edge needs no extra point.
        Assert.Null(ChartPointReducer.InterpolateAtEdge(points, 1, points[1].Timestamp, minimumGapMs: 10_000));
    }
}
