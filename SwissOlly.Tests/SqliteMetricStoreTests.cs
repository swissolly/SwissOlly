namespace SwissOlly.Tests;

public sealed class SqliteMetricStoreTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly SwissOlly.Store.Persistence.SqliteMetricStore _store;

    public SqliteMetricStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swissolly-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new SwissOlly.Store.Persistence.SqliteMetricStore();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // xUnit 2 calls IAsyncLifetime.DisposeAsync, not IAsyncDisposable: as IAsyncDisposable this never
    // ran, so no store was closed and every test left its temp folder behind.
    public async Task DisposeAsync()
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
    public async Task Bucketed_History_Averages_And_Honours_The_Range()
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
    /// Reads go through the rows in time order, but return panels by title and series by key as
    /// before (the board hands out line colours in that order), with buckets spanning rollups and
    /// raw samples averaged over every row in them.
    /// </summary>
    [Fact]
    public async Task History_Keeps_Its_Order_And_Averages_Across_Rollups_And_Raw_Samples()
    {
        var config = new SwissOllyConfig { StorageDirectory = _tempDir, RetentionInputText = "2d" }.Normalize();
        await _store.InitializeAsync(config, CancellationToken.None);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(-8).ToUnixTimeMilliseconds() / 300_000 * 300_000);
        for (var i = 0; i < 4; i++)
        {
            var at = start.AddSeconds(i * 15);
            await _store.AppendSnapshotAsync(new MetricSnapshot(at,
            [
                new MetricSample("memory", "Memory", "used-gb", "Used", MetricCategory.Memory, MetricUnit.Gigabytes, 10 + i, at),
                new MetricSample("cpu-cores", "CPU Cores", "core-2", "Core 2", MetricCategory.Cpu, MetricUnit.Percent, 2, at),
                new MetricSample("cpu-cores", "CPU Cores", "core-10", "Core 10", MetricCategory.Cpu, MetricUnit.Percent, 10, at),
                new MetricSample("cpu-cores", "CPU Cores", "core-1", "Core 1", MetricCategory.Cpu, MetricUnit.Percent, 1, at),
            ]), CancellationToken.None);
        }

        // Rolls the first minute up (it is past the raw window), then one raw sample lands in the
        // same five-minute bucket.
        await _store.PruneAsync(config, CancellationToken.None);
        var late = start.AddMinutes(2);
        await _store.AppendSnapshotAsync(new MetricSnapshot(late,
        [
            new MetricSample("memory", "Memory", "used-gb", "Used", MetricCategory.Memory, MetricUnit.Gigabytes, 20, late),
        ]), CancellationToken.None);

        foreach (var bucket in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(5) })
        {
            var history = await _store.LoadHistoryAsync(start, start.AddMinutes(5), bucket, CancellationToken.None);
            Assert.Equal(new[] { "CPU Cores", "Memory" }, history.Select(panel => panel.PanelTitle));
            Assert.Equal(new[] { "core-1", "core-10", "core-2" }, history[0].Series.Select(series => series.SeriesKey));
            Assert.Equal("Core 10", history[0].Series[1].SeriesName);
        }

        // The minute average (11.5) and the raw 20, as one bucket over both rows.
        var memory = (await _store.LoadHistoryAsync(start, start.AddMinutes(5), TimeSpan.FromMinutes(5), CancellationToken.None))[1];
        var point = Assert.Single(Assert.Single(memory.Series).Points);
        Assert.Equal(start, point.Timestamp);
        Assert.Equal((11.5 + 20) / 2, point.Value, 9);
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

    [Fact]
    public async Task New_Database_Uses_Incremental_Auto_Vacuum_And_Wal()
    {
        var config = CreateConfig();
        await _store.InitializeAsync(config, CancellationToken.None);

        Assert.Equal(2L, await PragmaAsync(config, "auto_vacuum"));   // 2 = incremental
        Assert.Equal("wal", await PragmaAsync(config, "journal_mode"));
    }

    [Fact]
    public async Task Lowering_Retention_Gives_The_Space_Back()
    {
        var config = new SwissOllyConfig { StorageDirectory = _tempDir, RetentionInputText = "7d" }.Normalize();
        await _store.InitializeAsync(config, CancellationToken.None);
        await AppendDayOfMinutesAsync(DateTimeOffset.UtcNow.AddDays(-3));
        var before = (long)(await PragmaAsync(config, "page_count"))!;

        var lowered = new SwissOllyConfig { StorageDirectory = _tempDir, RetentionInputText = "1d" }.Normalize();
        await _store.PruneAsync(lowered, CancellationToken.None);

        Assert.Equal(0L, await PragmaAsync(config, "freelist_count"));
        var after = (long)(await PragmaAsync(config, "page_count"))!;
        Assert.True(after < before / 4, $"{before} -> {after} pages");
    }

    /// <summary>
    /// Databases made before auto_vacuum was set ahead of WAL mode have none and can't shrink in
    /// place. When retention drops and most of the file is free, the prune converts them.
    /// </summary>
    [Fact]
    public async Task Database_Without_Auto_Vacuum_Is_Converted_When_Mostly_Free()
    {
        var config = new SwissOllyConfig { StorageDirectory = _tempDir, RetentionInputText = "7d" }.Normalize();
        await using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={config.GetDatabasePath()};Pooling=False"))
        {
            await old.OpenAsync();
            await using var command = old.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA auto_vacuum = INCREMENTAL; CREATE TABLE created_by_an_old_build (x);";
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync(config, CancellationToken.None);
        Assert.Equal(0L, await PragmaAsync(config, "auto_vacuum"));
        await AppendDayOfMinutesAsync(DateTimeOffset.UtcNow.AddDays(-3));
        var keptAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _store.AppendSnapshotAsync(new MetricSnapshot(keptAt,
        [
            new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, 42, keptAt),
        ]), CancellationToken.None);
        var before = (long)(await PragmaAsync(config, "page_count"))!;

        var lowered = new SwissOllyConfig { StorageDirectory = _tempDir, RetentionInputText = "1d" }.Normalize();
        await _store.PruneAsync(lowered, CancellationToken.None);

        Assert.Equal(2L, await PragmaAsync(config, "auto_vacuum"));
        var after = (long)(await PragmaAsync(config, "page_count"))!;
        Assert.True(after < before / 4, $"{before} -> {after} pages");
        var kept = await _store.LoadHistoryAsync(keptAt.AddMinutes(-1), keptAt.AddMinutes(1), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(42, Assert.Single(Assert.Single(Assert.Single(kept).Series).Points).Value);
    }

    /// <summary>
    /// A process killed mid-write leaves its files as they are at that moment: committed writes
    /// in the WAL, plus pages of the unfinished transaction. Copying them while a transaction is
    /// open gives the next launch exactly that. (tools/StoreBench crash kills a real writer.)
    /// </summary>
    [Fact]
    public async Task Files_Left_By_A_Writer_Killed_Mid_Transaction_Open_Clean_And_Take_Writes()
    {
        var config = CreateConfig();
        await _store.InitializeAsync(config, CancellationToken.None);
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        for (var i = 0; i < 50; i++)
        {
            var at = start.AddSeconds(i * 2);
            await _store.AppendSnapshotAsync(new MetricSnapshot(at,
            [
                new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, i, at),
            ]), CancellationToken.None);
        }

        var crashed = Path.Combine(_tempDir, "after-kill");
        Directory.CreateDirectory(crashed);
        await using (var writer = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={config.GetDatabasePath()};Pooling=False"))
        {
            await writer.OpenAsync();
            await using var transaction = writer.BeginTransaction();
            await using var insert = writer.CreateCommand();
            // Enough rows to spill uncommitted pages into the WAL (the page cache is 2 MB).
            insert.CommandText =
                """
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 40000)
                INSERT INTO metric_samples SELECT 'cpu-total', 'CPU Total', 'usage', 'Usage', 0, 0, $at + i, -1 FROM n;
                """;
            insert.Parameters.AddWithValue("$at", start.AddMinutes(5).ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync();

            // The -shm index is locked while open and is rebuilt from the WAL on the next open.
            foreach (var suffix in new[] { string.Empty, "-wal" })
            {
                await using var source = new FileStream(config.GetDatabasePath() + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var target = File.Create(Path.Combine(crashed, SwissOllyConfig.DatabaseFileName + suffix));
                await source.CopyToAsync(target);
            }

            Assert.True(new FileInfo(Path.Combine(crashed, SwissOllyConfig.DatabaseFileName + "-wal")).Length > 2 * 1024 * 1024);
        }

        var relaunched = new SwissOlly.Store.Persistence.SqliteMetricStore();
        try
        {
            var crashedConfig = new SwissOllyConfig { StorageDirectory = crashed, MaxRetentionHours = 24 }.Normalize();
            await relaunched.InitializeAsync(crashedConfig, CancellationToken.None);
            Assert.Equal("ok", await PragmaAsync(crashedConfig, "integrity_check"));

            var nextAt = start.AddMinutes(10);
            await relaunched.AppendSnapshotAsync(new MetricSnapshot(nextAt,
            [
                new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, 99, nextAt),
            ]), CancellationToken.None);
            var history = await relaunched.LoadHistoryAsync(start.AddMinutes(-1), nextAt.AddMinutes(1), TimeSpan.Zero, CancellationToken.None);
            var points = Assert.Single(Assert.Single(history).Series).Points;
            Assert.Equal(Enumerable.Range(0, 50).Select(i => (double)i).Append(99), points.Select(point => point.Value));
        }
        finally
        {
            await relaunched.DisposeAsync();
        }
    }

    /// <summary>A day of minute samples for 20 series, written as one snapshot (one transaction).</summary>
    private async Task AppendDayOfMinutesAsync(DateTimeOffset start)
    {
        var samples = new List<MetricSample>(20 * 1440);
        for (var minute = 0; minute < 1440; minute++)
        {
            var at = start.AddMinutes(minute);
            for (var core = 0; core < 20; core++)
            {
                samples.Add(new MetricSample("cpu-cores", "CPU Cores", $"core-{core}", $"Core {core}", MetricCategory.Cpu, MetricUnit.Percent, (minute * 0.37) + core, at));
            }
        }

        await _store.AppendSnapshotAsync(new MetricSnapshot(start.AddDays(1), samples), CancellationToken.None);
    }

    private static async Task<object?> PragmaAsync(SwissOllyConfig config, string pragma)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={config.GetDatabasePath()};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return await command.ExecuteScalarAsync();
    }
}
