using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SwissOlly.Core.Models;
using SwissOlly.Store.Persistence;

namespace StoreBench;

/// <summary>
/// Ways to read history, compared on a seeded database. One strategy per process, so the peak
/// memory reported is that strategy's own.
///   --window wide (default; [end - days, end - 24h), ~2000 buckets), older (the startup read's
///   minute-averaged 23 h) or recent (the startup read's raw last hour)
///   --strategy store (SqliteMetricStore itself), sql-groupby, sql-sorted (the current SQL shapes
///   on a plain connection), time-ordered (scan in time order, group in C#)
/// </summary>
internal static class Explore
{
    private const string Stored =
        """
        SELECT panel_key, panel_title, series_key, series_name, category, unit, timestamp_ms, value
        FROM metric_rollups_1m WHERE timestamp_ms >= $from AND timestamp_ms < $to
        UNION ALL
        SELECT panel_key, panel_title, series_key, series_name, category, unit, timestamp_ms, value
        FROM metric_samples WHERE timestamp_ms >= $from AND timestamp_ms < $to
        """;

    public static async Task<int> RunAsync(string dir, Options options)
    {
        var info = JsonSerializer.Deserialize<SeedInfo>(await File.ReadAllTextAsync(Path.Combine(dir, "seed.json")))!;
        var days = options.GetDouble("days", 365);
        var strategy = options.Get("strategy") ?? "store";
        var window = options.Get("window") ?? "wide";
        var rawStart = Commands.MinuteFloor(info.End.AddHours(-1));
        var (from, to, bucket) = window switch
        {
            "older" => (info.End.AddHours(-24), rawStart, TimeSpan.FromMinutes(1)),
            "recent" => (rawStart, info.End.AddMinutes(1), TimeSpan.Zero),
            _ => (info.End.AddDays(-days), info.End.AddHours(-24), MetricHistory.BucketFor(info.End.AddHours(-24) - info.End.AddDays(-days))),
        };
        var bucketMs = (long)bucket.TotalMilliseconds;
        var stopwatch = Stopwatch.StartNew();
        int points;
        if (strategy == "store")
        {
            await using var store = new SqliteMetricStore(new ManualClock(info.End));
            await store.InitializeAsync(Commands.Config(dir, info.Retention, info.IntervalSeconds), CancellationToken.None);
            stopwatch.Restart();
            var history = await store.LoadHistoryAsync(from, to, bucket, CancellationToken.None);
            points = history.Sum(panel => panel.Series.Sum(series => series.Points.Count));
        }
        else
        {
            await using var connection = Commands.Open(dir, readOnly: true);
            await connection.OpenAsync();
            var sql = strategy switch
            {
                "sql-groupby" =>
                    $"""
                    SELECT panel_key, MAX(panel_title), series_key, MAX(series_name), MAX(category), MAX(unit),
                           (timestamp_ms / $bucketMs) * $bucketMs AS bucket_ms, AVG(value)
                    FROM ({Stored}) GROUP BY panel_key, series_key, bucket_ms ORDER BY panel_key, series_key, bucket_ms;
                    """,
                "sql-sorted" => $"SELECT * FROM ({Stored}) ORDER BY panel_key, series_key, timestamp_ms;",
                "time-ordered" => $"SELECT * FROM ({Stored}) ORDER BY timestamp_ms;",
                "scan-only" => "SELECT count(*), sum(length(panel_key)), sum(value), 0 FROM metric_rollups_1m WHERE timestamp_ms >= $from AND timestamp_ms < $to;",
                _ => throw new ArgumentException($"Unknown strategy '{strategy}'."),
            };

            await PrintPlanAsync(connection, sql, from, to, Math.Max(1, bucketMs));
            stopwatch.Restart();
            if (strategy == "scan-only")
            {
                await using var scan = Command(connection, sql, from, to, Math.Max(1, bucketMs));
                points = Convert.ToInt32(await scan.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                points = await ReadAsync(connection, sql, from, to, Math.Max(1, bucketMs), strategy == "time-ordered" ? bucketMs : 0);
            }
        }

        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"{window,-6} {strategy,-13} {(window == "wide" ? $"{days,4:0} d" : "    "),6} bucket {bucket.TotalMinutes,4:0} min: {stopwatch.Elapsed.TotalMilliseconds,7:0} ms, {points,9:N0} points, peak private {Commands.Mb(process.PeakPagedMemorySize64)}");
        return 0;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, DateTimeOffset from, DateTimeOffset to, long bucketMs)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$bucketMs", bucketMs);
        return command;
    }

    private static async Task PrintPlanAsync(SqliteConnection connection, string sql, DateTimeOffset from, DateTimeOffset to, long bucketMs)
    {
        await using var command = Command(connection, "EXPLAIN QUERY PLAN " + sql, from, to, bucketMs);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Console.WriteLine($"    plan: {reader.GetString(3)}");
        }
    }

    /// <summary>
    /// Reads rows the way the store does (strings per row into per-series lists); with
    /// <paramref name="foldMs"/> set, rows arrive in time order and are averaged per bucket here.
    /// </summary>
    private static async Task<int> ReadAsync(SqliteConnection connection, string sql, DateTimeOffset from, DateTimeOffset to, long bucketMs, long foldMs)
    {
        var series = new Dictionary<(string, string), Fold>();
        await using var command = Command(connection, sql, from, to, bucketMs);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = (reader.GetString(0), reader.GetString(2));
            if (!series.TryGetValue(key, out var fold))
            {
                fold = new Fold(reader.GetString(1), reader.GetString(3));
                series.Add(key, fold);
            }
            else
            {
                _ = reader.GetString(3);
            }

            fold.Add(reader.GetInt64(6), reader.GetDouble(7), foldMs);
        }

        return series.Values.Sum(fold => fold.Finish());
    }

    private sealed class Fold(string title, string name)
    {
        private long _bucket = long.MinValue;
        private double _sum;
        private int _count;

        public string Title { get; } = title;

        public string Name { get; } = name;

        public List<MetricPoint> Points { get; } = [];

        public void Add(long timestampMs, double value, long foldMs)
        {
            if (foldMs <= 0)
            {
                Points.Add(new MetricPoint(DateTimeOffset.FromUnixTimeMilliseconds(timestampMs), value));
                return;
            }

            var bucket = timestampMs / foldMs * foldMs;
            if (bucket != _bucket)
            {
                Flush();
                _bucket = bucket;
            }

            _sum += value;
            _count++;
        }

        public int Finish()
        {
            Flush();
            return Points.Count;
        }

        private void Flush()
        {
            if (_count > 0)
            {
                Points.Add(new MetricPoint(DateTimeOffset.FromUnixTimeMilliseconds(_bucket), _sum / _count));
                _sum = 0;
                _count = 0;
            }
        }
    }
}
