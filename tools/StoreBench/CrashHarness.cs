using System.Diagnostics;
using SwissOlly.Core.Models;
using SwissOlly.Store.Persistence;

namespace StoreBench;

/// <summary>
/// Abrupt termination during a write. A child process writes snapshots through SqliteMetricStore
/// the way the collector does (41 samples, one transaction each), with simulated time running
/// fast (20 s a write by default) so the 15-minute maintenance (rollup, prune, vacuum step,
/// checkpoint) comes round every 45 writes, plus an explicit prune (as at launch) every 100.
/// The parent kills it (TerminateProcess) at a random moment, then checks that the database
/// opens, passes integrity_check, kept every write the child reported as committed, holds no
/// half-written snapshot or half-done rollup, and that the store reopens and keeps writing.
/// </summary>
internal static class CrashHarness
{
    private const int Series = 41;

    public static async Task<int> RunAsync(string dir, Options options)
    {
        var kills = options.GetInt("kills", 25);
        var random = new Random(options.GetInt("seed", 1));
        // Simulated seconds per write: 20 brings the 15-minute maintenance round every 45 writes.
        var step = options.GetInt("step", 20);
        Directory.CreateDirectory(dir);
        var failures = 0;
        var duringMaintenance = 0;
        var shares = new List<double>();
        Console.WriteLine($"Killing a writer {kills} times in {dir}");
        for (var round = 1; round <= kills; round++)
        {
            var delayMs = random.Next(30, 1500);
            var (lastCommitted, inMaintenance, writes, share) = await RunChildAndKillAsync(dir, round, delayMs, step);
            if (share > 0)
            {
                shares.Add(share);
            }

            var walAtKill = Commands.FileSize(dir, "-wal");
            duringMaintenance += inMaintenance ? 1 : 0;

            // Alternate which side opens the database first after the kill, so both the store's
            // own open and a plain connection get to recover the hot WAL.
            var problems = new List<string>();
            if (round % 2 == 0)
            {
                problems.AddRange(await ReopenStoreAsync(dir));
                problems.AddRange(await CheckAsync(dir, lastCommitted, afterStoreWrite: true));
            }
            else
            {
                problems.AddRange(await CheckAsync(dir, lastCommitted, afterStoreWrite: false));
                problems.AddRange(await ReopenStoreAsync(dir));
            }

            failures += problems.Count > 0 ? 1 : 0;
            Console.WriteLine(
                $"  #{round,2}: killed after {delayMs,4} ms, {writes,5} writes{(inMaintenance ? ", inside maintenance" : string.Empty)}, " +
                $"wal {Commands.Mb(walAtKill)} -> {(problems.Count == 0 ? "ok" : string.Join("; ", problems))}");
        }

        var (pages, free, raw, rollups) = await Commands.CountsAsync(dir);
        Console.WriteLine($"{kills - failures} of {kills} rounds clean ({duringMaintenance} killed inside maintenance; the writer spent {(shares.Count == 0 ? 0 : shares.Average()):P0} of its time inside store writes). Final: {raw:N0} raw rows, {rollups:N0} rollups, {pages:N0} pages, {free:N0} free.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<(long LastCommittedMs, bool InMaintenance, int Writes, double WriteShare)> RunChildAndKillAsync(string dir, int round, int delayMs, int step)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(CrashHarness).Assembly.Location);
        }

        foreach (var argument in new[] { "crash-child", "--dir", dir, "--seed", round.ToString(System.Globalization.CultureInfo.InvariantCulture), "--step", step.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var ready = new TaskCompletionSource();
        long lastCommitted = 0;
        var inMaintenance = false;
        var writes = 0;
        var share = 0d;
        using var child = new Process { StartInfo = startInfo };
        child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line)
            {
                return;
            }

            switch (line[0])
            {
                case 'R':
                    ready.TrySetResult();
                    break;
                case 'M':
                    inMaintenance = true;
                    break;
                case 'C':
                    lastCommitted = long.Parse(line.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
                    inMaintenance = false;
                    writes++;
                    break;
                case 'W':
                    share = double.Parse(line.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        };
        child.Start();
        child.BeginOutputReadLine();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(delayMs);
        child.Kill();
        child.WaitForExit(); // also drains the output already written
        return (lastCommitted, inMaintenance, writes, share);
    }

    private static async Task<List<string>> CheckAsync(string dir, long lastCommittedMs, bool afterStoreWrite)
    {
        var problems = new List<string>();
        await using var connection = Commands.Open(dir, readOnly: false);
        await connection.OpenAsync();
        var integrity = await Commands.Scalar<string>(connection, "PRAGMA integrity_check;");
        if (integrity != "ok")
        {
            problems.Add($"integrity_check: {integrity}");
        }

        var journal = await Commands.Scalar<string>(connection, "PRAGMA journal_mode;");
        if (journal != "wal")
        {
            problems.Add($"journal_mode {journal}");
        }

        if (lastCommittedMs > 0)
        {
            var kept = await Commands.Scalar<long>(connection, $"SELECT COUNT(*) FROM metric_samples WHERE timestamp_ms = {lastCommittedMs};");
            if (kept != Series)
            {
                problems.Add($"last committed snapshot has {kept} of {Series} rows");
            }

            // The write in flight at the kill either committed whole or not at all (plus the
            // store's own test write when it reopened first).
            var beyond = await Commands.Scalar<long>(connection, $"SELECT COUNT(*) FROM metric_samples WHERE timestamp_ms > {lastCommittedMs};");
            if (beyond > (afterStoreWrite ? 2 : 1) * Series)
            {
                problems.Add($"{beyond} rows after the last committed snapshot");
            }
        }

        var tornSnapshots = await Commands.Scalar<long>(connection,
            $"SELECT COUNT(*) FROM (SELECT 1 FROM metric_samples GROUP BY timestamp_ms HAVING COUNT(*) <> {Series});");
        var tornRollups = await Commands.Scalar<long>(connection,
            $"SELECT COUNT(*) FROM (SELECT 1 FROM metric_rollups_1m GROUP BY timestamp_ms HAVING COUNT(*) <> {Series});");
        // Rollup and delete share a transaction: a minute is raw or rolled up, never both.
        var doubled = await Commands.Scalar<long>(connection,
            """
            SELECT COUNT(*) FROM metric_samples AS s
            WHERE EXISTS (SELECT 1 FROM metric_rollups_1m AS r
                          WHERE r.panel_key = s.panel_key AND r.series_key = s.series_key
                            AND r.timestamp_ms = s.timestamp_ms / 60000 * 60000);
            """);
        if (tornSnapshots + tornRollups + doubled > 0)
        {
            problems.Add($"torn snapshots {tornSnapshots}, torn rollup minutes {tornRollups}, minutes both raw and rolled up {doubled}");
        }

        return problems;
    }

    /// <summary>The post-kill checks on any database, e.g. one whose writer was stopped some other way.</summary>
    public static async Task<int> CheckOnlyAsync(string dir)
    {
        var began = Stopwatch.StartNew();
        var problems = await CheckAsync(dir, 0, afterStoreWrite: false);
        Console.WriteLine($"Checked in {began.Elapsed.TotalSeconds:0.0} s: {(problems.Count == 0 ? "integrity ok, no torn snapshot or rollup" : string.Join("; ", problems))}");
        return problems.Count == 0 ? 0 : 1;
    }

    private static async Task<List<string>> ReopenStoreAsync(string dir)
    {
        var problems = new List<string>();
        try
        {
            long newest;
            await using (var connection = Commands.Open(dir, readOnly: false))
            {
                await connection.OpenAsync();
                newest = await Commands.Scalar<long>(connection, "SELECT COALESCE(MAX(timestamp_ms), 0) FROM metric_samples;");
            }

            var at = DateTimeOffset.FromUnixTimeMilliseconds(newest).AddSeconds(1);
            await using var store = new SqliteMetricStore(new ManualClock(at));
            await store.InitializeAsync(Commands.Config(dir, "1d", 2), CancellationToken.None);
            var samples = new List<MetricSample>(Series);
            new SyntheticBoard(99).Sample(at, samples);
            await store.AppendSnapshotAsync(new MetricSnapshot(at, samples), CancellationToken.None);
            var history = await store.LoadHistoryAsync(at.AddMinutes(-1), at.AddSeconds(1), TimeSpan.Zero, CancellationToken.None);
            var points = history.Sum(panel => panel.Series.Count(series => series.Points.Count > 0 && series.Points[^1].Timestamp == at));
            if (points != Series)
            {
                problems.Add($"store reopened but read back {points} of {Series} series");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"store reopen failed: {ex.GetType().Name}: {ex.Message}");
        }

        return problems;
    }

    public static async Task<int> ChildAsync(string dir, Options options)
    {
        var start = Commands.MinuteFloor(DateTimeOffset.UtcNow).AddDays(-1);
        if (File.Exists(Path.Combine(dir, SwissOllyConfig.DatabaseFileName)))
        {
            await using var connection = Commands.Open(dir, readOnly: false);
            await connection.OpenAsync();
            var newest = await Commands.Scalar<long>(connection, "SELECT COALESCE(MAX(timestamp_ms), 0) FROM metric_samples;");
            if (newest > 0)
            {
                start = DateTimeOffset.FromUnixTimeMilliseconds(newest);
            }
        }

        var clock = new ManualClock(start);
        var store = new SqliteMetricStore(clock); // never disposed: this process gets killed
        await store.InitializeAsync(Commands.Config(dir, "1d", 2), CancellationToken.None);
        var board = new SyntheticBoard(options.GetInt("seed", 1));
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        await output.WriteLineAsync("R");
        var step = options.GetInt("step", 2);
        var lastMaintenance = start;
        var at = start;
        var running = Stopwatch.StartNew();
        var inWrites = TimeSpan.Zero;
        for (var write = 1; ; write++)
        {
            at = at.AddSeconds(step);
            clock.AdvanceTo(at);
            var samples = new List<MetricSample>(Series);
            board.Sample(at, samples);
            if (at - lastMaintenance >= TimeSpan.FromMinutes(15))
            {
                lastMaintenance = at;
                await output.WriteLineAsync("M");
            }
            else if (write % 100 == 0)
            {
                // The prune the app runs at launch and after a retention change, which also
                // gives free pages back (incremental vacuum, or a converting VACUUM).
                await output.WriteLineAsync("M");
                await store.PruneAsync(Commands.Config(dir, "1d", 2), CancellationToken.None);
            }

            var began = Stopwatch.GetTimestamp();
            await store.AppendSnapshotAsync(new MetricSnapshot(at, samples), CancellationToken.None);
            inWrites += Stopwatch.GetElapsedTime(began);
            await output.WriteLineAsync($"C {at.ToUnixTimeMilliseconds()}");
            if (write % 25 == 0)
            {
                // Share of the child's time spent inside a store write: how often a kill lands mid-write.
                await output.WriteLineAsync($"W {inWrites / running.Elapsed:0.00}");
            }
        }
    }
}
