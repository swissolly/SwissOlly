using SwissOlly.Store.Persistence;

namespace SwissOlly.Tests;

/// <summary>
/// History is stored as UTC instants (Unix milliseconds) and rolled up into UTC minutes, so
/// daylight saving and time-zone changes never touch it. A clock set forward leaves a gap; a clock
/// set back keeps every sample, in time order; maintenance runs on the monotonic clock either way.
/// </summary>
public sealed class TimeHandlingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir;
    private readonly TestClock _clock = new(Start);
    private readonly SqliteMetricStore _store;

    public TimeHandlingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swissolly-time-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SqliteMetricStore(_clock);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // xUnit 2 calls IAsyncLifetime.DisposeAsync, not IAsyncDisposable: as IAsyncDisposable this never
    // ran, so no store was closed and every test left its temp folder behind.
    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private SwissOllyConfig Config(string retention = "2d") => new SwissOllyConfig
    {
        StorageDirectory = _tempDir,
        RetentionInputText = retention,
    }.Normalize();

    [Fact]
    public async Task Timestamps_Are_Stored_As_Instants_Whatever_Their_Offset()
    {
        await _store.InitializeAsync(Config(), CancellationToken.None);

        // Three scrapes two seconds apart, stamped in local time by a PC whose zone moved from
        // Tokyo to New York in between: the wall-clock reading falls 13 hours, the instants don't.
        var stamps = new[]
        {
            Start.ToOffset(TimeSpan.FromHours(9)),
            Start.AddSeconds(2).ToOffset(TimeSpan.FromHours(9)),
            Start.AddSeconds(4).ToOffset(TimeSpan.FromHours(-4)),
        };
        for (var i = 0; i < stamps.Length; i++)
        {
            await _store.AppendSnapshotAsync(Snapshot(stamps[i], i), CancellationToken.None);
        }

        var points = await LoadAsync(Start.AddMinutes(-1), Start.AddMinutes(1), TimeSpan.Zero);
        Assert.Equal(stamps, points.Select(point => point.Timestamp));
        Assert.Equal(new[] { 0d, 1d, 2d }, points.Select(point => point.Value));
        Assert.All(points, point => Assert.Equal(TimeSpan.Zero, point.Timestamp.Offset));
    }

    [Fact]
    public async Task Dst_Fall_Back_Keeps_Both_Passes_Through_The_Repeated_Hour()
    {
        await _store.InitializeAsync(Config(), CancellationToken.None);
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // 1 November 2026, 06:00 UTC: 02:00 EDT becomes 01:00 EST, so 01:00-02:00 local happens
        // twice. Three hours from 00:30 EDT, each sample worth its minute's index.
        var from = new DateTimeOffset(2026, 11, 1, 4, 30, 0, TimeSpan.Zero);
        var to = from.AddHours(3);
        var session = await WriteAsync(from, to, TimeSpan.FromSeconds(10), at => Math.Floor((at - from).TotalMinutes));

        // Seven hours on, all of it is past the raw window and rolled up.
        _clock.AdvanceTo(to.AddHours(7));
        await _store.PruneAsync(Config(), CancellationToken.None);
        var minutes = await LoadAsync(from, to, TimeSpan.FromMinutes(1));

        Assert.Equal(180, minutes.Count);
        for (var i = 0; i < minutes.Count; i++)
        {
            Assert.Equal(from.AddMinutes(i), minutes[i].Timestamp);
            Assert.Equal(i, minutes[i].Value, 9);
        }

        // Read on the local clock those 180 minutes show only 120 distinct times: keyed by local
        // time, the two passes through 01:00-02:00 would have been averaged together.
        Assert.Equal(120, minutes.Select(point => TimeZoneInfo.ConvertTime(point.Timestamp, eastern).DateTime).Distinct().Count());

        // A running session folds the same samples into the same minutes.
        MetricHistory.Compact(session, DateTimeOffset.MinValue, to, TimeSpan.FromMinutes(1));
        Assert.Equal(minutes, session);
    }

    [Fact]
    public async Task Dst_Spring_Forward_Leaves_No_Gap()
    {
        await _store.InitializeAsync(Config(), CancellationToken.None);
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // 8 March 2026, 07:00 UTC: 02:00 EST becomes 03:00 EDT; 02:00-03:00 local never happens.
        var from = new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(2);
        _clock.SetWall(from);
        await WriteAsync(from, to, TimeSpan.FromSeconds(10), at => 5);

        var raw = await LoadAsync(from, to, TimeSpan.Zero);
        Assert.Equal(720, raw.Count);
        Assert.Single(ChartPointReducer.SplitAtGaps(raw, 0));

        _clock.AdvanceTo(to.AddHours(7));
        await _store.PruneAsync(Config(), CancellationToken.None);
        var minutes = await LoadAsync(from, to, TimeSpan.FromMinutes(1));
        Assert.Equal(120, minutes.Count);
        Assert.Single(ChartPointReducer.SplitAtGaps(minutes, 0));

        // The local clock jumps from 01:59 to 03:00 across consecutive minutes.
        var local = minutes.Select(point => TimeZoneInfo.ConvertTime(point.Timestamp, eastern).DateTime).ToList();
        Assert.DoesNotContain(local, time => time.Hour == 2);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), local[60]);
    }

    [Fact]
    public async Task Time_Zone_Change_While_Running_Moves_Nothing()
    {
        await _store.InitializeAsync(Config(), CancellationToken.None);

        // Even stamped with local time, samples land in the same UTC minutes after the PC's
        // zone changes from Tokyo to New York mid-session.
        _clock.SetZone(TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"));
        var to = Start.AddMinutes(20);
        for (var at = Start; at < to; at = at.AddSeconds(2))
        {
            if (at == Start.AddMinutes(10))
            {
                _clock.SetZone(TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
            }

            _clock.AdvanceTo(at);
            var minute = Math.Floor((at - Start).TotalMinutes);
            await _store.AppendSnapshotAsync(Snapshot(_clock.GetLocalNow(), minute), CancellationToken.None);
        }

        _clock.AdvanceTo(to.AddHours(7));
        await _store.PruneAsync(Config(), CancellationToken.None);
        var minutes = await LoadAsync(Start, to, TimeSpan.FromMinutes(1));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => Start.AddMinutes(i)), minutes.Select(point => point.Timestamp));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (double)i), minutes.Select(point => point.Value));
    }

    [Fact]
    public async Task Clock_Set_Forward_Leaves_A_Gap_And_Writing_Carries_On()
    {
        await _store.InitializeAsync(Config(), CancellationToken.None);
        await WriteAsync(Start, Start.AddMinutes(10), TimeSpan.FromSeconds(2), _ => 1);

        var jumpedTo = Start.AddMinutes(10).AddHours(3);
        _clock.SetWall(jumpedTo);
        await WriteAsync(jumpedTo, jumpedTo.AddMinutes(10), TimeSpan.FromSeconds(2), _ => 2);

        var points = await LoadAsync(Start, jumpedTo.AddMinutes(10), TimeSpan.Zero);
        Assert.Equal(600, points.Count);
        var runs = ChartPointReducer.SplitAtGaps(points, 0);
        Assert.Equal(2, runs.Count);
        Assert.All(runs[0], point => Assert.Equal(1, point.Value));
        Assert.All(runs[1], point => Assert.Equal(2, point.Value));
    }

    [Fact]
    public async Task Clock_Set_Forward_Past_The_Raw_Window_Rolls_Up_What_Came_Before()
    {
        await _store.InitializeAsync(Config(), CancellationToken.None);
        var before = await WriteAsync(Start, Start.AddMinutes(10), TimeSpan.FromSeconds(2), at => (at - Start).TotalSeconds);

        // Seven hours forward; the maintenance due 15 elapsed minutes after opening runs five
        // minutes after the jump, on its own, and rolls up everything from before it.
        var jumpedTo = Start.AddMinutes(10).AddHours(7);
        _clock.SetWall(jumpedTo);
        await WriteAsync(jumpedTo, jumpedTo.AddMinutes(6), TimeSpan.FromSeconds(2), _ => 50);

        var rolledUp = await LoadAsync(Start, Start.AddMinutes(10), TimeSpan.Zero);
        MetricHistory.Compact(before, DateTimeOffset.MinValue, Start.AddMinutes(10), TimeSpan.FromMinutes(1));
        Assert.Equal(before, rolledUp);
        Assert.Equal(10, rolledUp.Count);

        var after = await LoadAsync(jumpedTo, jumpedTo.AddMinutes(6), TimeSpan.Zero);
        Assert.Equal(180, after.Count);
    }

    [Fact]
    public async Task Clock_Set_Forward_Past_Retention_Prunes_By_The_New_Time_And_Keeps_Writing()
    {
        var config = Config("24h");
        await _store.InitializeAsync(config, CancellationToken.None);
        await WriteAsync(Start, Start.AddMinutes(5), TimeSpan.FromSeconds(2), _ => 1);

        // Retention follows the wall clock: set two days ahead, what came before has expired.
        _clock.SetWall(Start.AddDays(2));
        await _store.PruneAsync(config, CancellationToken.None);
        await WriteAsync(Start.AddDays(2), Start.AddDays(2).AddMinutes(1), TimeSpan.FromSeconds(2), _ => 2);

        Assert.Empty(await LoadAsync(Start, Start.AddMinutes(5), TimeSpan.Zero));
        Assert.Equal(30, (await LoadAsync(Start.AddDays(2), Start.AddDays(2).AddMinutes(1), TimeSpan.Zero)).Count);
    }

    [Fact]
    public async Task Clock_Set_Back_Keeps_Every_Sample_In_Time_Order()
    {
        await _store.InitializeAsync(Config(), CancellationToken.None);
        await WriteAsync(Start, Start.AddMinutes(10), TimeSpan.FromSeconds(2), _ => 1);

        // Set back five minutes: the next ten minutes overlap the last five already written.
        _clock.SetWall(Start.AddMinutes(5).AddSeconds(1));
        await WriteAsync(Start.AddMinutes(5).AddSeconds(1), Start.AddMinutes(15).AddSeconds(1), TimeSpan.FromSeconds(2), _ => 2);

        var points = await LoadAsync(Start, Start.AddMinutes(20), TimeSpan.Zero);
        Assert.Equal(600, points.Count);
        Assert.Equal(points.OrderBy(point => point.Timestamp).Select(point => point.Timestamp), points.Select(point => point.Timestamp));
        var overlap = points.Where(point => point.Timestamp >= Start.AddMinutes(5) && point.Timestamp < Start.AddMinutes(10)).ToList();
        Assert.Equal(150, overlap.Count(point => point.Value == 1));
        Assert.Equal(150, overlap.Count(point => point.Value == 2));
    }

    [Fact]
    public async Task Clock_Set_Back_Does_Not_Hold_Up_Maintenance()
    {
        var config = Config("24h");
        await _store.InitializeAsync(config, CancellationToken.None);

        // A sample past retention, waiting for the next maintenance to prune it.
        await _store.AppendSnapshotAsync(Snapshot(Start.AddHours(-30), 5), CancellationToken.None);

        // The clock is set back an hour, then fifteen minutes pass. Maintenance used to wait for
        // the wall clock to reach its next slot, an hour and a quarter away, while raw samples
        // piled up; it now runs fifteen elapsed minutes after the last one.
        _clock.SetWall(Start.AddHours(-1));
        await WriteAsync(Start.AddHours(-1), Start.AddHours(-1).AddMinutes(15).AddSeconds(2), TimeSpan.FromSeconds(2), _ => 1);

        Assert.Empty(await LoadAsync(Start.AddHours(-31), Start.AddHours(-29), TimeSpan.Zero));
    }

    [Fact]
    public async Task Clock_Set_Back_Past_The_Raw_Window_Averages_A_Minute_Rolled_Up_Twice()
    {
        var config = Config();
        await _store.InitializeAsync(config, CancellationToken.None);
        await WriteAsync(Start, Start.AddMinutes(1), TimeSpan.FromSeconds(2), _ => 10);
        _clock.AdvanceTo(Start.AddHours(7));
        await _store.PruneAsync(config, CancellationToken.None);
        Assert.Equal(10, Assert.Single(await LoadAsync(Start, Start.AddMinutes(1), TimeSpan.Zero)).Value);

        // Set back seven hours, the same wall-clock minute comes round again and is rolled up a
        // second time. It used to replace the first minute's average outright.
        _clock.SetWall(Start);
        await WriteAsync(Start, Start.AddMinutes(1), TimeSpan.FromSeconds(2), _ => 30);
        _clock.AdvanceTo(Start.AddHours(7));
        await _store.PruneAsync(config, CancellationToken.None);

        Assert.Equal(20, Assert.Single(await LoadAsync(Start, Start.AddMinutes(1), TimeSpan.Zero)).Value, 9);
    }

    /// <summary>
    /// A backlog (the clock jumped days ahead) is worked through a bounded step per write, an
    /// half hour of raw samples at a time, so no single write holds the store for long.
    /// </summary>
    [Fact]
    public async Task A_Long_Backlog_Is_Worked_Through_A_Step_Per_Write()
    {
        await _store.InitializeAsync(Config("24h"), CancellationToken.None);
        var samples = Enumerable.Range(0, 3 * 1440)
            .Select(minute => new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, minute, Start.AddMinutes(minute)))
            .ToList();
        await _store.AppendSnapshotAsync(new MetricSnapshot(Start.AddDays(3), samples), CancellationToken.None);

        // Three days of raw samples, then the clock jumps to half a day past them: the oldest
        // two and a half days have expired, the last half day is due to be rolled up.
        var jumpedTo = Start.AddDays(3.5);
        _clock.SetWall(jumpedTo);
        await WriteAsync(jumpedTo, jumpedTo.AddMinutes(15).AddSeconds(2), TimeSpan.FromSeconds(2), _ => 1);
        Assert.Equal((72 * 60) - 30, (await LoadAsync(Start, Start.AddDays(3), TimeSpan.Zero)).Count);

        await WriteAsync(jumpedTo.AddMinutes(15).AddSeconds(2), jumpedTo.AddMinutes(21), TimeSpan.FromSeconds(2), _ => 1);
        // Done: what is left is the last day's minutes (the cutoff moved on with the writes), each
        // rolled up once, all raw samples gone.
        var kept = await LoadAsync(Start, Start.AddDays(3), TimeSpan.Zero);
        var first = (int)kept[0].Value;
        Assert.InRange(first, 3600, 3630);
        Assert.Equal(Enumerable.Range(first, 4320 - first).Select(minute => (double)minute), kept.Select(point => point.Value));
        Assert.All(kept, point => Assert.Equal(Start.AddMinutes(point.Value), point.Timestamp));
    }

    [Fact]
    public void Live_Samples_From_A_Clock_Set_Back_Go_In_Time_Order()
    {
        var points = new List<MetricPoint>();
        foreach (var seconds in new[] { 0, 10, 20 })
        {
            MetricHistory.InsertInOrder(points, new MetricPoint(Start.AddSeconds(seconds), 1));
        }

        // The clock goes back 15 s: the next samples fall among those already held.
        foreach (var seconds in new[] { 5, 10, 15, 25 })
        {
            MetricHistory.InsertInOrder(points, new MetricPoint(Start.AddSeconds(seconds), 2));
        }

        Assert.Equal(new[] { 0, 5, 10, 10, 15, 20, 25 }, points.Select(point => (int)(point.Timestamp - Start).TotalSeconds));
        Assert.Equal(new[] { 1d, 2d, 1d, 2d, 2d, 1d, 2d }, points.Select(point => point.Value));

        // Folding the overlap averages both passes, as the store does.
        MetricHistory.Compact(points, DateTimeOffset.MinValue, Start.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.Equal(11d / 7, Assert.Single(points).Value, 9);
    }

    [Fact]
    public void Pinned_Process_Samples_Carry_On_After_A_Clock_Set_Back()
    {
        var points = new List<MetricPoint>();
        foreach (var seconds in new[] { 0, 10, 20 })
        {
            Assert.True(MetricHistory.InsertInOrderIfNew(points, new MetricPoint(Start.AddSeconds(seconds), 1)));
        }

        // A refresh on the newest sample's timestamp adds nothing.
        Assert.False(MetricHistory.InsertInOrderIfNew(points, new MetricPoint(Start.AddSeconds(20), 9)));

        // The clock goes back 15 s. Samples used to be dropped until it passed 20 s again.
        Assert.True(MetricHistory.InsertInOrderIfNew(points, new MetricPoint(Start.AddSeconds(5), 2)));
        Assert.False(MetricHistory.InsertInOrderIfNew(points, new MetricPoint(Start.AddSeconds(5), 9)));
        Assert.True(MetricHistory.InsertInOrderIfNew(points, new MetricPoint(Start.AddSeconds(15), 2)));
        Assert.True(MetricHistory.InsertInOrderIfNew(points, new MetricPoint(Start.AddSeconds(25), 2)));

        Assert.Equal(new[] { 0, 5, 10, 15, 20, 25 }, points.Select(point => (int)(point.Timestamp - Start).TotalSeconds));
        Assert.Equal(new[] { 1d, 2d, 1d, 2d, 1d, 2d }, points.Select(point => point.Value));
    }

    /// <summary>One snapshot per scrape from <paramref name="from"/>, the clock following along.</summary>
    private async Task<List<MetricPoint>> WriteAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan every, Func<DateTimeOffset, double> value)
    {
        var written = new List<MetricPoint>();
        for (var at = from; at < to; at += every)
        {
            _clock.AdvanceTo(at);
            var point = new MetricPoint(at, value(at));
            written.Add(point);
            await _store.AppendSnapshotAsync(Snapshot(at, point.Value), CancellationToken.None);
        }

        return written;
    }

    private async Task<IReadOnlyList<MetricPoint>> LoadAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var history = await _store.LoadHistoryAsync(from, to, bucket, CancellationToken.None);
        return history.Count == 0 ? [] : Assert.Single(Assert.Single(history).Series).Points;
    }

    private static MetricSnapshot Snapshot(DateTimeOffset at, double value) => new(at,
    [
        new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, value, at),
    ]);

    /// <summary>
    /// A wall clock that can be set (as a user or time sync does) apart from the monotonic clock,
    /// which only ever moves forward with elapsed time.
    /// </summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _wall = start;
        private long _elapsedTicks;
        private TimeZoneInfo _zone = TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _elapsedTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override TimeZoneInfo LocalTimeZone => _zone;

        /// <summary>Time passes until the wall clock reads <paramref name="wall"/>.</summary>
        public void AdvanceTo(DateTimeOffset wall)
        {
            if (wall > _wall)
            {
                _elapsedTicks += (wall - _wall).Ticks;
                _wall = wall;
            }
        }

        /// <summary>The clock is set: the wall time jumps, no time passes.</summary>
        public void SetWall(DateTimeOffset wall) => _wall = wall;

        public void SetZone(TimeZoneInfo zone) => _zone = zone;
    }
}
