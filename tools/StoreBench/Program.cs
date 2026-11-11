using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StoreBench;
using SwissOlly.Core.Models;
using SwissOlly.Store.Persistence;

// Seeds, measures and crash-tests the history database through SwissOlly.Store's own code paths
// (SqliteMetricStore: the same schema, inserts, rollups and pruning as the app). Always point it
// at a scratch folder, never at %LocalAppData%\SwissOlly.
//
//   seed    --dir D [--days 365] [--interval 2] [--batch 30]   a year at 2 s, maintenance every 15 simulated
//           [--resume [--hours H]]                             min; --resume carries on a stopped seed
//   bench   --dir D [--skip-reads] [--catchup] [--plan]        file size, startup and range loads, prune,
//                                                              writes, memory (all reads: minutes on a year)
//   explore --dir D [--window wide|older|recent] [--days N] [--strategy store|sql-groupby|sql-sorted|
//           time-ordered|scan-only]                            one read, one process: time and peak memory
//   growth  --dir D [--interval 2] [--hours 48] [--retention 365d]   bytes per day, one snapshot per write
//   shrink  --dir D [--retention 24h] [--in-place] [--no-cycles]     lower retention: prune, file size, writes
//   crash   --dir D [--kills 25] [--seed 1] [--step 20]         kill a writer mid-write, check and reopen
//   check   --dir D                                            the post-kill checks on any database
//   plan    --dir D                                            EXPLAIN QUERY PLAN for the store's statements
//   snapshot --dir D --to T, mark-end --dir D                  copy a live database; fix seed.json's end
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var options = Options.Parse(args);
var dir = options.Get("dir") ?? throw new ArgumentException("--dir is required");
dir = Path.GetFullPath(dir);
if (dir.Contains(Path.Combine("AppData", "Local", "SwissOlly"), StringComparison.OrdinalIgnoreCase))
{
    throw new ArgumentException("Refusing to run against the app's own data folder.");
}

return options.Command switch
{
    "seed" => await Commands.SeedAsync(dir, options),
    "bench" => await Commands.BenchAsync(dir, options),
    "growth" => await Commands.GrowthAsync(dir, options),
    "shrink" => await Commands.ShrinkAsync(dir, options),
    "plan" => await Commands.PlanAsync(dir),
    "explore" => await Explore.RunAsync(dir, options),
    "snapshot" => await Commands.SnapshotAsync(dir, options),
    "check" => await CrashHarness.CheckOnlyAsync(dir),
    "mark-end" => await Commands.MarkEndAsync(dir),
    "crash" => await CrashHarness.RunAsync(dir, options),
    "crash-child" => await CrashHarness.ChildAsync(dir, options),
    _ => throw new ArgumentException($"Unknown command '{options.Command}'."),
};

internal sealed record SeedInfo(DateTimeOffset Start, DateTimeOffset End, double Days, int IntervalSeconds, int Series, string Retention);

internal static class Commands
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(15);

    public static SwissOllyConfig Config(string dir, string retention, int intervalSeconds) => new SwissOllyConfig
    {
        StorageDirectory = dir,
        RetentionInputText = retention,
        ScrapeIntervalSeconds = intervalSeconds,
    }.Normalize();

    public static DateTimeOffset MinuteFloor(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds() / 60_000 * 60_000);

    public static async Task<int> SeedAsync(string dir, Options options)
    {
        var days = options.GetDouble("days", 365);
        var interval = options.GetInt("interval", 2);
        var batch = options.GetInt("batch", 30);
        var retention = options.Get("retention") ?? $"{(int)Math.Ceiling(days) + 1}d";
        Directory.CreateDirectory(dir);
        var end = MinuteFloor(DateTimeOffset.UtcNow);
        var start = end - TimeSpan.FromDays(days);
        var resumeAfter = start;
        if (File.Exists(Path.Combine(dir, SwissOllyConfig.DatabaseFileName)))
        {
            if (!options.Has("resume"))
            {
                throw new InvalidOperationException("The folder already holds a database; seed into an empty one, or pass --resume.");
            }

            // Carry on a seed that was stopped: same start, same end, from the newest sample.
            await using var connection = Open(dir, readOnly: false);
            await connection.OpenAsync();
            start = DateTimeOffset.FromUnixTimeMilliseconds(await Scalar<long>(connection, "SELECT MIN(timestamp_ms) FROM metric_rollups_1m;"));
            end = start + TimeSpan.FromDays(days);
            resumeAfter = DateTimeOffset.FromUnixTimeMilliseconds(await Scalar<long>(connection, "SELECT COALESCE((SELECT MAX(timestamp_ms) FROM metric_samples), (SELECT MAX(timestamp_ms) + 58000 FROM metric_rollups_1m));"));
            if (options.Get("hours") is { } hours)
            {
                // A fixed stretch past the newest sample, whatever the oldest is now.
                end = resumeAfter + TimeSpan.FromHours(double.Parse(hours, CultureInfo.InvariantCulture));
            }

            Console.WriteLine($"Resuming after {resumeAfter:u} (day {(resumeAfter - start).TotalDays:0.0})");
        }

        var clock = new ManualClock(resumeAfter);
        var config = Config(dir, retention, interval);
        var board = new SyntheticBoard();
        Console.WriteLine($"Seeding {days} days at {interval} s, {board.SeriesCount} series, retention {config.GetRetentionWindow().TotalDays:0.#} d, {batch} scrapes per write");

        var appendMs = new Stats();
        var maintenanceMs = new Stats();
        var samples = new List<MetricSample>(board.SeriesCount * batch);
        var step = TimeSpan.FromSeconds(interval);
        var lastMaintenance = resumeAfter;
        var nextReport = start.AddDays(Math.Min(30, days));
        while (nextReport <= resumeAfter)
        {
            nextReport = nextReport.AddDays(30);
        }

        long rows = 0;
        var total = Stopwatch.StartNew();
        await using (var store = new SqliteMetricStore(clock))
        {
            await store.InitializeAsync(config, CancellationToken.None);
            var inBatch = 0;
            for (var at = resumeAfter + step; at <= end; at += step)
            {
                board.Sample(at, samples);
                if (++inBatch < batch && at + step <= end)
                {
                    continue;
                }

                clock.AdvanceTo(at);
                var maintains = at - lastMaintenance >= MaintenanceInterval;
                var began = Stopwatch.GetTimestamp();
                await store.AppendSnapshotAsync(new MetricSnapshot(at, samples), CancellationToken.None);
                var elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                if (maintains)
                {
                    lastMaintenance = at;
                    maintenanceMs.Add(elapsed);
                }
                else
                {
                    appendMs.Add(elapsed);
                }

                rows += samples.Count;
                samples.Clear();
                inBatch = 0;
                if (at >= nextReport)
                {
                    nextReport = nextReport.AddDays(30);
                    Console.WriteLine($"  day {(at - start).TotalDays,5:0}: {total.Elapsed:hh\\:mm\\:ss}, {rows / total.Elapsed.TotalSeconds / 1000,6:0}k rows/s, db {Mb(FileSize(dir, ""))} + wal {Mb(FileSize(dir, "-wal"))}");
                }
            }
        }

        var info = new SeedInfo(start, end, days, interval, board.SeriesCount, retention);
        await File.WriteAllTextAsync(Path.Combine(dir, "seed.json"), JsonSerializer.Serialize(info));
        Console.WriteLine($"Done in {total.Elapsed:hh\\:mm\\:ss}: {rows:N0} samples written");
        Console.WriteLine($"  write of {batch} scrapes: {appendMs}");
        Console.WriteLine($"  write + maintenance (rollup, prune, vacuum step): {maintenanceMs}");
        Console.WriteLine($"  files after close: db {Mb(FileSize(dir, ""))}, wal {Mb(FileSize(dir, "-wal"))}");
        await PrintDatabaseAsync(dir);
        return 0;
    }

    public static async Task<int> BenchAsync(string dir, Options options)
    {
        var info = JsonSerializer.Deserialize<SeedInfo>(await File.ReadAllTextAsync(Path.Combine(dir, "seed.json")))!;
        var end = info.End;
        var config = Config(dir, info.Retention, info.IntervalSeconds);
        Console.WriteLine($"Database: {info.Days} days at {info.IntervalSeconds} s, {info.Series} series, ending {end:u}");
        await PrintDatabaseAsync(dir);

        var clock = new ManualClock(end);
        await using var store = new SqliteMetricStore(clock);
        var opened = Stopwatch.StartNew();
        await store.InitializeAsync(config, CancellationToken.None);
        Console.WriteLine($"Open (InitializeAsync): {opened.Elapsed.TotalMilliseconds:0} ms");

        if (!options.Has("skip-reads"))
        {
            // ShellWindow.TryLoadHistoryAsync: 24 h (or the retention, if shorter), minute averages
            // up to the last whole minute an hour back, raw samples after that.
            var baseWindow = TimeSpan.FromHours(Math.Min(24, config.GetRetentionWindow().TotalHours));
            var baseStart = end - baseWindow;
            var rawStart = MinuteFloor(end - TimeSpan.FromHours(1));
            GC.Collect();
            var managedBefore = GC.GetTotalMemory(true);
            var privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
            IReadOnlyList<MetricSeriesHistory> startup = [];
            var startupMs = new Stats();
            var olderMs = new Stats();
            for (var run = 0; run < 3; run++)
            {
                startup = [];
                var began = Stopwatch.GetTimestamp();
                var older = await store.LoadHistoryAsync(baseStart, rawStart, TimeSpan.FromMinutes(1), CancellationToken.None);
                olderMs.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                var recent = await store.LoadHistoryAsync(rawStart, end.AddMinutes(1), TimeSpan.Zero, CancellationToken.None);
                startup = MetricHistory.Concat(older, recent);
                startupMs.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            }

            Console.WriteLine($"  of which the minute-averaged 23 h: {olderMs}");

            var startupPoints = startup.Sum(panel => panel.Series.Sum(series => series.Points.Count));
            var managedAfter = GC.GetTotalMemory(true);
            var privateAfter = Process.GetCurrentProcess().PrivateMemorySize64;
            Console.WriteLine($"Startup history (24 h: 23 h of minutes + 1 h raw): {startupMs}; {startupPoints:N0} points in {startup.Count} panels");
            Console.WriteLine($"  held in memory: {Mb(managedAfter - managedBefore)} managed; process private bytes {Mb(privateBefore)} -> {Mb(privateAfter)}, peak {Mb(Process.GetCurrentProcess().PeakPagedMemorySize64)}");
            GC.KeepAlive(startup);
            startup = [];

            // 1h and 24h are drawn from what the startup load put in memory. Wider ranges read the part
            // before the 24 h base (ShellWindow.LoadWideHistoryAsync), averaged to ~2000 points a series.
            Console.WriteLine("Range loads (UI's wide-history read: [now - range, now - 24h), ~2000 buckets a series):");
            foreach (var (label, minutes) in new[] { ("2d", 2880), ("5d", 7200), ("7d", 10080), ("30d", 43200), ("90d", 129600), ("1y", 525600) })
            {
                if (TimeSpan.FromMinutes(minutes) > end - info.Start + TimeSpan.FromMinutes(1))
                {
                    continue;
                }

                var from = end.AddMinutes(-minutes);
                var bucket = MetricHistory.BucketFor(baseStart - from);
                var stats = new Stats();
                var points = 0;
                long managed = 0;
                for (var run = 0; run < 3; run++)
                {
                    var before = GC.GetTotalMemory(true);
                    var began = Stopwatch.GetTimestamp();
                    var history = await store.LoadHistoryAsync(from, baseStart, bucket, CancellationToken.None);
                    stats.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                    points = history.Sum(panel => panel.Series.Sum(series => series.Points.Count));
                    managed = GC.GetTotalMemory(true) - before;
                    GC.KeepAlive(history);
                }

                using var process = Process.GetCurrentProcess();
                Console.WriteLine($"  {label,-4} bucket {bucket.TotalMinutes,4:0} min: {stats}; {points:N0} points ({points / info.Series} per series), {Mb(managed)} managed, process peak private {Mb(process.PeakPagedMemorySize64)}");
            }

            Console.WriteLine($"Process private bytes after range loads: {Mb(Process.GetCurrentProcess().PrivateMemorySize64)}");
        }

        var prune = Stopwatch.StartNew();
        await store.PruneAsync(config, CancellationToken.None);
        Console.WriteLine($"Prune at steady state (nothing due): {prune.Elapsed.TotalMilliseconds:0} ms");

        // 15 minutes of collection and the maintenance it triggers, as in the running app.
        var board = new SyntheticBoard(11);
        var samples = new List<MetricSample>(board.SeriesCount);
        var write = new Stats();
        var withMaintenance = 0d;
        var at = end;
        for (var i = 0; i < 451; i++)
        {
            at = at.AddSeconds(info.IntervalSeconds);
            clock.AdvanceTo(at);
            samples.Clear();
            board.Sample(at, samples);
            var began = Stopwatch.GetTimestamp();
            await store.AppendSnapshotAsync(new MetricSnapshot(at, [.. samples]), CancellationToken.None);
            var elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            if (i == 449) // 450 scrapes of 2 s since the prune: the 15-minute maintenance
            {
                withMaintenance = elapsed;
            }
            else
            {
                write.Add(elapsed);
            }
        }

        Console.WriteLine($"Collector write (41 samples, one transaction): {write}");
        Console.WriteLine($"Write that ran the 15-minute maintenance: {withMaintenance:0} ms");

        if (options.Has("catchup"))
        {
            // The app was closed overnight: the startup prune rolls up the whole raw window, while
            // the collector writes, as it does after a launch.
            clock.AdvanceTo(at.AddHours(8));
            var (pruneMs, writes) = await PruneWhileWritingAsync(store, config, clock.GetUtcNow(), board);
            Console.WriteLine($"Startup prune after 8 h closed (rolls up 6 h of raw samples): {pruneMs:0} ms; writes meanwhile: {writes}");
        }

        if (options.Has("plan"))
        {
            await PrintPlanAsync(dir, end);
        }

        return 0;
    }

    /// <summary>
    /// Runs a prune while a collector-like loop writes a snapshot every 2 s (sped up to every
    /// 250 ms), and reports how long each write waited: the collector gives up after 5 s.
    /// </summary>
    private static async Task<(double PruneMs, Stats Writes)> PruneWhileWritingAsync(SqliteMetricStore store, SwissOllyConfig config, DateTimeOffset from, SyntheticBoard board)
    {
        var writes = new Stats();
        var began = Stopwatch.StartNew();
        var prune = Task.Run(() => store.PruneAsync(config, CancellationToken.None));
        var at = from;
        while (!prune.IsCompleted)
        {
            at = at.AddSeconds(2);
            var samples = new List<MetricSample>(board.SeriesCount);
            board.Sample(at, samples);
            var write = Stopwatch.GetTimestamp();
            await store.AppendSnapshotAsync(new MetricSnapshot(at, samples), CancellationToken.None);
            writes.Add(Stopwatch.GetElapsedTime(write).TotalMilliseconds);
            await Task.WhenAny(prune, Task.Delay(250));
        }

        await prune;
        return (began.Elapsed.TotalMilliseconds, writes);
    }

    public static async Task<int> GrowthAsync(string dir, Options options)
    {
        var interval = options.GetInt("interval", 2);
        var hours = options.GetInt("hours", 48);
        var retention = options.Get("retention") ?? "365d";
        Directory.CreateDirectory(dir);
        var start = MinuteFloor(DateTimeOffset.UtcNow) - TimeSpan.FromHours(hours);
        var clock = new ManualClock(start);
        var config = Config(dir, retention, interval);
        var board = new SyntheticBoard();
        var samples = new List<MetricSample>(board.SeriesCount);
        var step = TimeSpan.FromSeconds(interval);
        long walPeak = 0;
        var write = new Stats();
        Console.WriteLine($"Growth at {interval} s, {board.SeriesCount} series, retention {retention}, one write per scrape (as the collector)");
        Console.WriteLine("  hour      db       wal   pages  free  raw rows   rollups");
        await using (var store = new SqliteMetricStore(clock))
        {
            await store.InitializeAsync(config, CancellationToken.None);
            var nextHour = start.AddHours(1);
            for (var at = start + step; at <= start.AddHours(hours); at += step)
            {
                clock.AdvanceTo(at);
                samples.Clear();
                board.Sample(at, samples);
                var began = Stopwatch.GetTimestamp();
                await store.AppendSnapshotAsync(new MetricSnapshot(at, [.. samples]), CancellationToken.None);
                write.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                walPeak = Math.Max(walPeak, FileSize(dir, "-wal"));
                if (at >= nextHour)
                {
                    var hour = (int)Math.Round((at - start).TotalHours);
                    nextHour = nextHour.AddHours(1);
                    if (hour is 1 or 3 or 6 or 7 or 12 or 18 or 24 or 30 or 36 or 42 or 48 or 72 or 96 || hour == hours)
                    {
                        var (pages, free, raw, rollups) = await CountsAsync(dir);
                        Console.WriteLine($"  {hour,4} {Mb(FileSize(dir, "")),9} {Mb(FileSize(dir, "-wal")),9} {pages,7} {free,5} {raw,9:N0} {rollups,9:N0}");
                    }
                }
            }
        }

        Console.WriteLine($"  write per scrape: {write}");
        Console.WriteLine($"  WAL peak {Mb(walPeak)}; after close: db {Mb(FileSize(dir, ""))}, wal {Mb(FileSize(dir, "-wal"))}");
        return 0;
    }

    public static async Task<int> ShrinkAsync(string dir, Options options)
    {
        var info = JsonSerializer.Deserialize<SeedInfo>(await File.ReadAllTextAsync(Path.Combine(dir, "seed.json")))!;
        var copy = dir;
        if (!options.Has("in-place"))
        {
            copy = dir.TrimEnd(Path.DirectorySeparatorChar) + "-shrink";
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(dir, SwissOllyConfig.DatabaseFileName), Path.Combine(copy, SwissOllyConfig.DatabaseFileName), overwrite: true);
        }

        Console.WriteLine($"The {info.Days}-day database: {Mb(FileSize(copy, ""))}");

        var clock = new ManualClock(info.End);
        var retention = options.Get("retention") ?? "24h";
        var config = Config(copy, retention, info.IntervalSeconds);
        await using (var store = new SqliteMetricStore(clock))
        {
            await store.InitializeAsync(config, CancellationToken.None);
            var (pruneMs, writes) = await PruneWhileWritingAsync(store, config, info.End, new SyntheticBoard(3));
            Console.WriteLine($"Retention lowered to {retention}, prune: {pruneMs:0} ms; db {Mb(FileSize(copy, ""))}, wal {Mb(FileSize(copy, "-wal"))}; writes meanwhile: {writes}");
            var (pages, free, _, _) = await CountsAsync(copy);
            Console.WriteLine($"  pages {pages:N0}, free {free:N0}");

            var board = new SyntheticBoard(5);
            var samples = new List<MetricSample>(board.SeriesCount);
            var at = info.End;
            for (var cycle = 1; cycle <= (options.Has("no-cycles") ? 0 : 4); cycle++)
            {
                for (var i = 0; i < 450; i++)
                {
                    at = at.AddSeconds(info.IntervalSeconds);
                    clock.AdvanceTo(at);
                    samples.Clear();
                    board.Sample(at, samples);
                    await store.AppendSnapshotAsync(new MetricSnapshot(at, [.. samples]), CancellationToken.None);
                }

                (pages, free, _, _) = await CountsAsync(copy);
                Console.WriteLine($"  after maintenance {cycle}: db {Mb(FileSize(copy, ""))}, pages {pages:N0}, free {free:N0}");
            }
        }

        Console.WriteLine($"After close: db {Mb(FileSize(copy, ""))}, wal {Mb(FileSize(copy, "-wal"))}");
        if (options.Has("vacuum"))
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(copy, SwissOllyConfig.DatabaseFileName)}");
            await connection.OpenAsync();
            var vacuum = Stopwatch.StartNew();
            await Execute(connection, "PRAGMA incremental_vacuum;");
            Console.WriteLine($"Full incremental_vacuum: {vacuum.Elapsed.TotalMilliseconds:0} ms; db {Mb(FileSize(copy, ""))}, wal {Mb(FileSize(copy, "-wal"))}");
            await Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Console.WriteLine($"  after checkpoint: db {Mb(FileSize(copy, ""))}, wal {Mb(FileSize(copy, "-wal"))}");
        }

        return 0;
    }

    /// <summary>A consistent page-for-page copy of a database still being written (backup API).</summary>
    public static async Task<int> SnapshotAsync(string dir, Options options)
    {
        var target = Path.GetFullPath(options.Get("to") ?? throw new ArgumentException("--to is required"));
        Directory.CreateDirectory(target);
        long oldest, newest;
        await using (var source = Open(dir, readOnly: true))
        {
            await source.OpenAsync();
            await using var destination = Open(target, readOnly: false);
            destination.ConnectionString = new SqliteConnectionStringBuilder(destination.ConnectionString) { Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
            await destination.OpenAsync();
            var began = Stopwatch.StartNew();
            source.BackupDatabase(destination);
            Console.WriteLine($"Copied in {began.Elapsed.TotalSeconds:0.0} s");
            oldest = await Scalar<long>(destination, "SELECT MIN(timestamp_ms) FROM metric_rollups_1m;");
            newest = await Scalar<long>(destination, "SELECT MAX(timestamp_ms) FROM metric_samples;");
        }

        var interval = options.GetInt("interval", 2);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(oldest);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(newest);
        var info = new SeedInfo(start, end, Math.Round((end - start).TotalDays, 1), interval, 41, "366d");
        await File.WriteAllTextAsync(Path.Combine(target, "seed.json"), JsonSerializer.Serialize(info));
        await PrintDatabaseAsync(target);
        return 0;
    }

    /// <summary>Points seed.json's end at the newest sample (after a seed was stopped part way).</summary>
    public static async Task<int> MarkEndAsync(string dir)
    {
        var path = Path.Combine(dir, "seed.json");
        var info = JsonSerializer.Deserialize<SeedInfo>(await File.ReadAllTextAsync(path))!;
        await using var connection = Open(dir, readOnly: false);
        await connection.OpenAsync();
        var newest = DateTimeOffset.FromUnixTimeMilliseconds(await Scalar<long>(connection, "SELECT MAX(timestamp_ms) FROM metric_samples;"));
        var oldestRaw = DateTimeOffset.FromUnixTimeMilliseconds(await Scalar<long>(connection, "SELECT MIN(timestamp_ms) FROM metric_samples;"));
        var oldest = DateTimeOffset.FromUnixTimeMilliseconds(await Scalar<long>(connection, "SELECT MIN(timestamp_ms) FROM metric_rollups_1m;"));
        var end = MinuteFloor(newest.AddMinutes(1));
        info = info with { Start = oldest, End = end, Days = Math.Round((end - oldest).TotalDays, 1) };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(info));
        Console.WriteLine($"Rollups from {oldest:u}, raw {oldestRaw:u} to {newest:u}; seed.json end set to {end:u}");
        return 0;
    }

    public static async Task<int> PlanAsync(string dir)
    {
        long newest;
        await using (var connection = Open(dir, readOnly: true))
        {
            await connection.OpenAsync();
            newest = await Scalar<long>(connection, "SELECT MAX(timestamp_ms) FROM metric_samples;");
        }

        await PrintPlanAsync(dir, DateTimeOffset.FromUnixTimeMilliseconds(newest));
        return 0;
    }

    public static async Task PrintDatabaseAsync(string dir)
    {
        await using var connection = Open(dir, readOnly: true);
        await connection.OpenAsync();
        var pageSize = await Scalar<long>(connection, "PRAGMA page_size;");
        var pages = await Scalar<long>(connection, "PRAGMA page_count;");
        var free = await Scalar<long>(connection, "PRAGMA freelist_count;");
        var autoVacuum = await Scalar<long>(connection, "PRAGMA auto_vacuum;");
        var journal = await Scalar<string>(connection, "PRAGMA journal_mode;");
        var raw = await Scalar<long>(connection, "SELECT COUNT(*) FROM metric_samples;");
        var rollups = await Scalar<long>(connection, "SELECT COUNT(*) FROM metric_rollups_1m;");
        Console.WriteLine($"  file {Mb(FileSize(dir, ""))} (+ wal {Mb(FileSize(dir, "-wal"))}): {pages:N0} pages of {pageSize} B, {free:N0} free; auto_vacuum={autoVacuum} (2 = incremental), journal={journal}");
        Console.WriteLine($"  rows: {raw:N0} raw, {rollups:N0} minute rollups ({(double)pages * pageSize / Math.Max(1, raw + rollups):0} bytes a row, indexes included)");
        await using (var options = connection.CreateCommand())
        {
            options.CommandText = "SELECT group_concat(compile_options, ' ') FROM pragma_compile_options WHERE compile_options LIKE 'TEMP_STORE%' OR compile_options LIKE 'DEFAULT_CACHE%' OR compile_options LIKE 'ENABLE_DBSTAT%';";
            Console.WriteLine($"  SQLite {await Scalar<string>(connection, "SELECT sqlite_version();")}, {await options.ExecuteScalarAsync()}, temp_store={await Scalar<long>(connection, "PRAGMA temp_store;")}, cache_size={await Scalar<long>(connection, "PRAGMA cache_size;")}");
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name ORDER BY 2 DESC;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Console.WriteLine($"    {reader.GetString(0),-40} {Mb(reader.GetInt64(1)),10}");
            }
        }
        catch (SqliteException)
        {
            Console.WriteLine("    (dbstat not available in this SQLite build)");
        }
    }

    /// <summary>EXPLAIN QUERY PLAN for the store's reads and maintenance statements.</summary>
    public static async Task PrintPlanAsync(string dir, DateTimeOffset end)
    {
        const string Stored =
            """
            SELECT panel_key, panel_title, series_key, series_name, category, unit, timestamp_ms, value
            FROM metric_rollups_1m WHERE timestamp_ms >= $from AND timestamp_ms < $to
            UNION ALL
            SELECT panel_key, panel_title, series_key, series_name, category, unit, timestamp_ms, value
            FROM metric_samples WHERE timestamp_ms >= $from AND timestamp_ms < $to
            """;
        var statements = new (string Name, string Sql)[]
        {
            ("bucketed read (startup older part, wide ranges)",
                $"""
                SELECT panel_key, MAX(panel_title), series_key, MAX(series_name), MAX(category), MAX(unit),
                       (timestamp_ms / $bucketMs) * $bucketMs AS bucket_ms, AVG(value)
                FROM ({Stored}) GROUP BY panel_key, series_key, bucket_ms ORDER BY panel_key, series_key, bucket_ms;
                """),
            ("raw read (startup last hour)", $"SELECT * FROM ({Stored}) ORDER BY panel_key, series_key, timestamp_ms;"),
            ("rollup",
                """
                SELECT panel_key, panel_title, series_key, series_name, category, unit, (timestamp_ms / $bucketMs) * $bucketMs, AVG(value)
                FROM metric_samples WHERE timestamp_ms >= $from AND timestamp_ms < $to
                GROUP BY panel_key, panel_title, series_key, series_name, category, unit, (timestamp_ms / $bucketMs);
                """),
            ("raw delete", "DELETE FROM metric_samples WHERE timestamp_ms < $to;"),
            ("rollup delete", "DELETE FROM metric_rollups_1m WHERE timestamp_ms < $to;"),
        };

        await using var connection = Open(dir, readOnly: true);
        await connection.OpenAsync();
        foreach (var (name, sql) in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            command.Parameters.AddWithValue("$bucketMs", 15_780_000L);
            command.Parameters.AddWithValue("$from", end.AddDays(-365).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", end.AddDays(-1).ToUnixTimeMilliseconds());
            await using var reader = await command.ExecuteReaderAsync();
            Console.WriteLine($"Plan, {name}:");
            while (await reader.ReadAsync())
            {
                Console.WriteLine($"  {reader.GetString(3)}");
            }
        }
    }

    public static async Task<(long Pages, long Free, long Raw, long Rollups)> CountsAsync(string dir)
    {
        await using var connection = Open(dir, readOnly: true);
        await connection.OpenAsync();
        return (
            await Scalar<long>(connection, "PRAGMA page_count;"),
            await Scalar<long>(connection, "PRAGMA freelist_count;"),
            await Scalar<long>(connection, "SELECT COUNT(*) FROM metric_samples;"),
            await Scalar<long>(connection, "SELECT COUNT(*) FROM metric_rollups_1m;"));
    }

    public static SqliteConnection Open(string dir, bool readOnly) => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(dir, SwissOllyConfig.DatabaseFileName),
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString());

    public static async Task<T> Scalar<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    public static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static long FileSize(string dir, string suffix)
    {
        var file = new FileInfo(Path.Combine(dir, SwissOllyConfig.DatabaseFileName + suffix));
        return file.Exists ? file.Length : 0;
    }

    public static string Mb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.00} GB"
        : $"{bytes / (1024d * 1024):0.0} MB";
}

/// <summary>Min, median, max and mean of a set of timings in milliseconds.</summary>
internal sealed class Stats
{
    private readonly List<double> _values = [];

    public void Add(double value) => _values.Add(value);

    public override string ToString()
    {
        if (_values.Count == 0)
        {
            return "n/a";
        }

        var sorted = _values.Order().ToArray();
        return $"min {sorted[0]:0.0} / median {sorted[sorted.Length / 2]:0.0} / max {sorted[^1]:0.0} ms (n={sorted.Length}, mean {sorted.Average():0.00})";
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = string.Empty;

    public static Options Parse(string[] args)
    {
        var options = new Options { Command = args.Length > 0 ? args[0] : string.Empty };
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options._values[args[i][2..]] = hasValue ? args[++i] : null;
        }

        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public int GetInt(string name, int fallback) => Get(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string name, double fallback) => Get(name) is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;
}
