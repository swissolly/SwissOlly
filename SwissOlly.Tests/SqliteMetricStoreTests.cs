namespace SwissOlly.Tests;

public sealed class SqliteMetricStoreTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly SwissOlly.Store.Persistence.SqliteMetricStore _store;

    public SqliteMetricStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swissolly-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SwissOlly.Store.Persistence.SqliteMetricStore();
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    private SwissOllyConfig CreateConfig() => new SwissOllyConfig
    {
        StorageDirectory = _tempDir,
        MaxRetentionHours = 24,
    }.Normalize();

    [Fact]
    public async Task InitializeAsync_Creates_Database_File()
    {
        var config = CreateConfig();
        await _store.InitializeAsync(config, CancellationToken.None);
        Assert.True(File.Exists(config.GetDatabasePath()));
    }

    [Fact]
    public async Task AppendSnapshot_And_LoadHistory_Roundtrip()
    {
        var config = CreateConfig();
        await _store.InitializeAsync(config, CancellationToken.None);

        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new MetricSnapshot(timestamp,
        [
            new MetricSample("cpu-total", "CPU Total", "usage", "Usage",
                MetricCategory.Cpu, MetricUnit.Percent, 45.5, timestamp),
        ]);

        await _store.AppendSnapshotAsync(snapshot, CancellationToken.None);

        var history = await _store.LoadHistoryAsync(timestamp.AddMinutes(-5), CancellationToken.None);
        Assert.NotEmpty(history);
        var cpuPanel = history.First(h => h.PanelKey == "cpu-total");
        Assert.Single(cpuPanel.Series);
        Assert.Single(cpuPanel.Series[0].Points);
        Assert.Equal(45.5, cpuPanel.Series[0].Points[0].Value, 1);
    }

    [Fact]
    public async Task Bucketed_History_Averages_In_Sql_And_Honours_The_Range()
    {
        await _store.InitializeAsync(CreateConfig(), CancellationToken.None);
        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddHours(1);
        // Ten-second samples for ten minutes: values 0..59.
        for (var i = 0; i < 60; i++)
        {
            var at = start.AddSeconds(i * 10);
            await _store.AppendSnapshotAsync(new MetricSnapshot(at,
            [
                new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, i, at),
            ]), CancellationToken.None);
        }

        var fiveMinute = await _store.LoadHistoryAsync(start, start.AddMinutes(10), TimeSpan.FromMinutes(5), CancellationToken.None);
        var points = Assert.Single(Assert.Single(fiveMinute).Series).Points;
        Assert.Equal(2, points.Count);
        Assert.Equal(14.5, points[0].Value, 3);   // average of 0..29
        Assert.Equal(44.5, points[1].Value, 3);   // average of 30..59

        var raw = await _store.LoadHistoryAsync(start.AddMinutes(1), start.AddMinutes(2), TimeSpan.Zero, CancellationToken.None);
        var rawPoints = Assert.Single(Assert.Single(raw).Series).Points;
        Assert.Equal(6, rawPoints.Count);          // [1:00, 2:00) at ten-second spacing
        Assert.Equal(6d, rawPoints[0].Value);
    }

    /// <summary>
    /// The restart guarantee: what the app reloads at launch (minute averages before the last hour,
    /// raw samples after it) is point for point what a running session holds once it has folded
    /// its own older samples into minutes. So a chart looks the same before and after a restart.
    /// </summary>
    [Fact]
    public async Task Reloaded_History_Matches_What_A_Running_Session_Shows()
    {
        await _store.InitializeAsync(CreateConfig(), CancellationToken.None);
        var start = MinuteFloor(DateTimeOffset.UtcNow.AddHours(-4)).AddSeconds(7);
        var end = start.AddHours(2.5);
        var session = new List<MetricPoint>();
        var random = new Random(12);
        for (var at = start; at < end; at = at.AddSeconds(5))
        {
            // Spiky, like CPU load, with a five-minute outage in the middle.
            if (at > start.AddMinutes(70) && at < start.AddMinutes(75))
            {
                continue;
            }

            var value = random.NextDouble() < 0.05 ? 90 + (random.NextDouble() * 10) : random.NextDouble() * 20;
            session.Add(new MetricPoint(at, value));
            await _store.AppendSnapshotAsync(new MetricSnapshot(at,
            [
                new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, value, at),
            ]), CancellationToken.None);
        }

        // The session folds everything older than an hour, cut at a whole minute.
        var rawFrom = MinuteFloor(end.AddHours(-1));
        MetricHistory.Compact(session, DateTimeOffset.MinValue, rawFrom, TimeSpan.FromMinutes(1));

        // A restart at the same moment reads the same two tiers.
        var older = await _store.LoadHistoryAsync(MinuteFloor(start), rawFrom, TimeSpan.FromMinutes(1), CancellationToken.None);
        var recent = await _store.LoadHistoryAsync(rawFrom, end.AddMinutes(1), TimeSpan.Zero, CancellationToken.None);
        var reloaded = Assert.Single(Assert.Single(MetricHistory.Concat(older, recent)).Series).Points;

        Assert.Equal(session.Count, reloaded.Count);
        for (var i = 0; i < session.Count; i++)
        {
            Assert.Equal(session[i].Timestamp, reloaded[i].Timestamp);
            Assert.Equal(session[i].Value, reloaded[i].Value, 9);
        }

        // The outage stays an outage: no minute is invented inside it.
        Assert.DoesNotContain(reloaded, p => p.Timestamp > start.AddMinutes(71) && p.Timestamp < start.AddMinutes(74));
    }

    private static DateTimeOffset MinuteFloor(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds() / 60_000 * 60_000);

    [Fact]
    public async Task Store_Recovers_When_Its_Folder_Becomes_Reachable_Again()
    {
        // A file sitting where the storage folder should be stands in for an unplugged drive.
        var blocked = Path.Combine(_tempDir, "blocked");
        await File.WriteAllTextAsync(blocked, "not a folder");
        var config = new SwissOllyConfig { StorageDirectory = blocked, MaxRetentionHours = 24 }.Normalize();
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new MetricSnapshot(timestamp,
        [
            new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, 12, timestamp),
        ]);

        await Assert.ThrowsAnyAsync<IOException>(() => _store.InitializeAsync(config, CancellationToken.None));
        await Assert.ThrowsAnyAsync<IOException>(() => _store.AppendSnapshotAsync(snapshot, CancellationToken.None));

        File.Delete(blocked);
        await _store.AppendSnapshotAsync(snapshot, CancellationToken.None);

        var history = await _store.LoadHistoryAsync(timestamp.AddMinutes(-1), CancellationToken.None);
        Assert.Single(history);
        Assert.True(File.Exists(config.GetDatabasePath()));
    }

    [Fact]
    public async Task AppendSnapshot_Empty_Samples_Does_Not_Throw()
    {
        var config = CreateConfig();
        await _store.InitializeAsync(config, CancellationToken.None);

        var snapshot = new MetricSnapshot(DateTimeOffset.UtcNow, []);
        await _store.AppendSnapshotAsync(snapshot, CancellationToken.None);
    }

    [Fact]
    public async Task LoadHistory_Empty_Database_Returns_Empty()
    {
        var config = CreateConfig();
        await _store.InitializeAsync(config, CancellationToken.None);

        var history = await _store.LoadHistoryAsync(DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);
        Assert.Empty(history);
    }

    [Fact]
    public async Task History_Persists_After_Store_Restart()
    {
        var config = CreateConfig();
        var timestamp = DateTimeOffset.UtcNow;
        await _store.InitializeAsync(config, CancellationToken.None);
        await _store.AppendSnapshotAsync(new MetricSnapshot(timestamp,
        [
            new MetricSample("cpu-total", "CPU Total", "usage", "Usage",
                MetricCategory.Cpu, MetricUnit.Percent, 62.5, timestamp),
        ]), CancellationToken.None);
        await _store.DisposeAsync();

        var reopenedStore = new SwissOlly.Store.Persistence.SqliteMetricStore();
        try
        {
            await reopenedStore.InitializeAsync(config, CancellationToken.None);
            var history = await reopenedStore.LoadHistoryAsync(timestamp.AddMinutes(-5), CancellationToken.None);

            var point = Assert.Single(Assert.Single(Assert.Single(history).Series).Points);
            Assert.Equal(62.5, point.Value, 1);
        }
        finally
        {
            await reopenedStore.DisposeAsync();
        }
    }

    [Fact]
    public async Task PruneAsync_Honors_Exact_Configured_Retention_After_Restart()
    {
        var originalConfig = new SwissOllyConfig
        {
            StorageDirectory = _tempDir,
            MaxRetentionHours = 24,
            RetentionInputText = "24h",
        }.Normalize();
        var shortRetentionConfig = new SwissOllyConfig
        {
            StorageDirectory = _tempDir,
            MaxRetentionHours = 24,
            RetentionInputText = "1h",
        }.Normalize();
        var now = DateTimeOffset.UtcNow;
        var expiredAt = now.AddMinutes(-70);
        var retainedAt = now.AddMinutes(-20);
        await _store.InitializeAsync(originalConfig, CancellationToken.None);
        await _store.AppendSnapshotAsync(new MetricSnapshot(expiredAt,
        [
            new MetricSample("test", "Test", "value", "Value",
                MetricCategory.System, MetricUnit.Count, 1, expiredAt),
        ]), CancellationToken.None);
        await _store.AppendSnapshotAsync(new MetricSnapshot(retainedAt,
        [
            new MetricSample("test", "Test", "value", "Value",
                MetricCategory.System, MetricUnit.Count, 2, retainedAt),
        ]), CancellationToken.None);

        await _store.DisposeAsync();

        var reopenedStore = new SwissOlly.Store.Persistence.SqliteMetricStore();
        try
        {
            await reopenedStore.InitializeAsync(shortRetentionConfig, CancellationToken.None);
            // This is the startup maintenance step: catch up using the retention value
            // saved in settings while the app was closed.
            await reopenedStore.PruneAsync(shortRetentionConfig, CancellationToken.None);
            var history = await reopenedStore.LoadHistoryAsync(now.AddHours(-2), CancellationToken.None);

            var points = Assert.Single(Assert.Single(history).Series).Points;
            var point = Assert.Single(points);
            Assert.Equal(2, point.Value);
            Assert.True(point.Timestamp >= now.AddHours(-1));
        }
        finally
        {
            await reopenedStore.DisposeAsync();
        }
    }

    [Fact]
    public async Task Wide_History_Includes_Rollups_And_Recent_Raw_Samples()
    {
        var config = new SwissOllyConfig
        {
            StorageDirectory = _tempDir,
            MaxRetentionHours = 48,
            RetentionInputText = "2d",
        }.Normalize();
        var now = DateTimeOffset.UtcNow;
        var olderAt = now.AddHours(-8);
        var recentAt = now.AddMinutes(-30);
        await _store.InitializeAsync(config, CancellationToken.None);
        await _store.AppendSnapshotAsync(new MetricSnapshot(olderAt,
        [
            new MetricSample("test", "Test", "value", "Value",
                MetricCategory.System, MetricUnit.Count, 8, olderAt),
        ]), CancellationToken.None);
        await _store.AppendSnapshotAsync(new MetricSnapshot(recentAt,
        [
            new MetricSample("test", "Test", "value", "Value",
                MetricCategory.System, MetricUnit.Count, 30, recentAt),
        ]), CancellationToken.None);
        await _store.PruneAsync(config, CancellationToken.None);

        var history = await _store.LoadHistoryAsync(now.AddDays(-2), CancellationToken.None);

        var points = Assert.Single(Assert.Single(history).Series).Points;
        Assert.Contains(points, point => point.Value == 8);
        Assert.Contains(points, point => point.Value == 30);
    }

    [Fact]
    public async Task PruneAsync_Removes_Old_Data()
    {
        var config = CreateConfig();
        await _store.InitializeAsync(config, CancellationToken.None);

        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-30);
        var snapshot = new MetricSnapshot(oldTimestamp,
        [
            new MetricSample("test", "Test", "value", "Value",
                MetricCategory.System, MetricUnit.Count, 1.0, oldTimestamp),
        ]);
        await _store.AppendSnapshotAsync(snapshot, CancellationToken.None);

        var pruneConfig = new SwissOllyConfig
        {
            StorageDirectory = _tempDir,
            MaxRetentionHours = 1,
        }.Normalize();
        await _store.PruneAsync(pruneConfig, CancellationToken.None);

        var history = await _store.LoadHistoryAsync(oldTimestamp.AddMinutes(-1), CancellationToken.None);
        Assert.Empty(history);
    }
}
