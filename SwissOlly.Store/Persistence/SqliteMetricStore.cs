using Microsoft.Data.Sqlite;
using SwissOlly.Core.Interfaces;
using SwissOlly.Core.Models;

namespace SwissOlly.Store.Persistence;

public sealed class SqliteMetricStore : IMetricStore
{
    private static readonly TimeSpan RawResolutionWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(15);
    private const long MinuteBucketMilliseconds = 60_000;
    private const long IncrementalAutoVacuum = 2;

    /// <summary>Largest live database a prune will rewrite to turn on auto_vacuum (see ChooseReclaimAsync).</summary>
    private const long MaxConvertingVacuumBytes = 256L * 1024 * 1024;

    /// <summary>Most raw samples one maintenance step rolls up: half an hour (about 37,000 rows at 2 s).</summary>
    private const long RawStepMilliseconds = 30 * MinuteBucketMilliseconds;

    /// <summary>Most expired rollups one maintenance step deletes: six hours (about 15,000 rows).</summary>
    private const long RollupStepMilliseconds = 6 * 60 * MinuteBucketMilliseconds;

    /// <summary>Pages one reclaim step gives back: 8 MB.</summary>
    private const int VacuumStepPages = 2048;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private string? _connectionString;
    private SwissOllyConfig? _config;
    private SqliteConnection? _writeConnection;
    private SqliteCommand? _insertCommand;
    private SqliteParameter? _panelKeyParameter;
    private SqliteParameter? _panelTitleParameter;
    private SqliteParameter? _seriesKeyParameter;
    private SqliteParameter? _seriesNameParameter;
    private SqliteParameter? _categoryParameter;
    private SqliteParameter? _unitParameter;
    private SqliteParameter? _timestampMsParameter;
    private SqliteParameter? _valueParameter;
    private long _lastMaintenanceTimestamp;
    private int _disposeStarted;

    public SqliteMetricStore()
        : this(TimeProvider.System)
    {
    }

    /// <param name="time">
    /// The wall clock behind retention and rollup cutoffs, and the monotonic clock that schedules
    /// maintenance. Tests and tools pass their own to simulate time passing or the clock changing.
    /// </param>
    public SqliteMetricStore(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task InitializeAsync(SwissOllyConfig config, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await InitializeCoreAsync(config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The database under its current name, renaming a pre-rebrand one first (custom storage
    /// folders are not covered by the settings-folder move). If that rename could not happen,
    /// keep opening the old file rather than start an empty database beside it.
    /// </summary>
    private static string ResolveDatabasePath(SwissOllyConfig config)
    {
        RebrandMigration.MigrateDatabaseFiles(config.StorageDirectory);
        var path = config.GetDatabasePath();
        var preRebrandPath = Path.Combine(config.StorageDirectory, SwissOllyConfig.PreRebrandDatabaseFileName);
        return !File.Exists(path) && File.Exists(preRebrandPath) ? preRebrandPath : path;
    }

    private async Task InitializeCoreAsync(SwissOllyConfig config, CancellationToken cancellationToken)
    {
        config = config.Normalize();
        var nextConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDatabasePath(config),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        if (string.Equals(_connectionString, nextConnectionString, StringComparison.Ordinal) &&
            _writeConnection is not null &&
            _insertCommand is not null)
        {
            _config = config;
            _lastMaintenanceTimestamp = _time.GetTimestamp();
            return;
        }

        if (_connectionString is not null)
        {
            ReleasePooledConnections(_connectionString);
        }

        _connectionString = nextConnectionString;
        _config = config;
        await OpenWriteConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenWriteConnectionAsync(CancellationToken cancellationToken)
    {
        await DropWriteConnectionAsync().ConfigureAwait(false);

        // Recreated on every open: a storage folder on a drive that was unplugged and plugged
        // back in needs it again before SQLite can create the database file.
        Directory.CreateDirectory(_config!.StorageDirectory);
        _writeConnection = new SqliteConnection(_connectionString);
        await _writeConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = _writeConnection.CreateCommand();
        // auto_vacuum first: switching journal_mode to WAL writes the header of a new database,
        // after which its vacuum mode can no longer change, so every database created with the
        // old order had none and the incremental_vacuum in maintenance never freed anything.
        // journal_size_limit: the WAL file otherwise stays as big as the largest transaction
        // since launch (a big prune or vacuum); normal running keeps it under 10 MB.
        command.CommandText =
            """
            PRAGMA auto_vacuum = INCREMENTAL;
            PRAGMA journal_mode = WAL;
            PRAGMA journal_size_limit = 16777216;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;

            CREATE TABLE IF NOT EXISTS metric_samples (
                panel_key TEXT NOT NULL,
                panel_title TEXT NOT NULL,
                series_key TEXT NOT NULL,
                series_name TEXT NOT NULL,
                category INTEGER NOT NULL,
                unit INTEGER NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                value REAL NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_metric_samples_timestamp
                ON metric_samples(timestamp_ms);

            CREATE INDEX IF NOT EXISTS idx_metric_samples_panel_series_time
                ON metric_samples(panel_key, series_key, timestamp_ms);

            CREATE TABLE IF NOT EXISTS metric_rollups_1m (
                panel_key TEXT NOT NULL,
                panel_title TEXT NOT NULL,
                series_key TEXT NOT NULL,
                series_name TEXT NOT NULL,
                category INTEGER NOT NULL,
                unit INTEGER NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                value REAL NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_metric_rollups_identity
                ON metric_rollups_1m(panel_key, series_key, timestamp_ms);

            CREATE INDEX IF NOT EXISTS idx_metric_rollups_timestamp
                ON metric_rollups_1m(timestamp_ms);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _insertCommand = _writeConnection.CreateCommand();
        _insertCommand.CommandText =
            """
            INSERT INTO metric_samples (
                panel_key,
                panel_title,
                series_key,
                series_name,
                category,
                unit,
                timestamp_ms,
                value
            )
            VALUES (
                $panelKey,
                $panelTitle,
                $seriesKey,
                $seriesName,
                $category,
                $unit,
                $timestampMs,
                $value
            );
            """;

        _panelKeyParameter = _insertCommand.CreateParameter();
        _panelKeyParameter.ParameterName = "$panelKey";
        _insertCommand.Parameters.Add(_panelKeyParameter);

        _panelTitleParameter = _insertCommand.CreateParameter();
        _panelTitleParameter.ParameterName = "$panelTitle";
        _insertCommand.Parameters.Add(_panelTitleParameter);

        _seriesKeyParameter = _insertCommand.CreateParameter();
        _seriesKeyParameter.ParameterName = "$seriesKey";
        _insertCommand.Parameters.Add(_seriesKeyParameter);

        _seriesNameParameter = _insertCommand.CreateParameter();
        _seriesNameParameter.ParameterName = "$seriesName";
        _insertCommand.Parameters.Add(_seriesNameParameter);

        _categoryParameter = _insertCommand.CreateParameter();
        _categoryParameter.ParameterName = "$category";
        _insertCommand.Parameters.Add(_categoryParameter);

        _unitParameter = _insertCommand.CreateParameter();
        _unitParameter.ParameterName = "$unit";
        _insertCommand.Parameters.Add(_unitParameter);

        _timestampMsParameter = _insertCommand.CreateParameter();
        _timestampMsParameter.ParameterName = "$timestampMs";
        _insertCommand.Parameters.Add(_timestampMsParameter);

        _valueParameter = _insertCommand.CreateParameter();
        _valueParameter.ParameterName = "$value";
        _insertCommand.Parameters.Add(_valueParameter);

        _lastMaintenanceTimestamp = _time.GetTimestamp();
    }

    public async Task AppendSnapshotAsync(MetricSnapshot snapshot, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (snapshot.Samples.Count == 0)
        {
            return;
        }

        _ = EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_writeConnection is null || _insertCommand is null)
            {
                // A failed write dropped the connection; try again now that the disk may have room
                // or the drive may be back.
                await OpenWriteConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            var connection = EnsureWriteConnectionInitialized();
            var command = EnsureInsertCommandInitialized();
            try
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                command.Transaction = (SqliteTransaction)transaction;
                try
                {
                    foreach (var sample in snapshot.Samples)
                    {
                        _panelKeyParameter!.Value = sample.PanelKey;
                        _panelTitleParameter!.Value = sample.PanelTitle;
                        _seriesKeyParameter!.Value = sample.SeriesKey;
                        _seriesNameParameter!.Value = sample.SeriesName;
                        _categoryParameter!.Value = (int)sample.Category;
                        _unitParameter!.Value = (int)sample.Unit;
                        _timestampMsParameter!.Value = sample.Timestamp.ToUnixTimeMilliseconds();
                        _valueParameter!.Value = sample.Value;

                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    command.Transaction = null;
                }
                await MaybeMaintainAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                // Disk full, drive removed, file locked: the connection may be unusable now, so
                // drop it and reopen on the next write instead of failing forever.
                await DropWriteConnectionAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropWriteConnectionAsync()
    {
        if (_insertCommand is not null)
        {
            await _insertCommand.DisposeAsync().ConfigureAwait(false);
            _insertCommand = null;
        }

        if (_writeConnection is not null)
        {
            try
            {
                await _writeConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Closing a connection whose file vanished can fail too; it is gone either way.
            }

            _writeConnection = null;
        }
    }

    public async Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(
        DateTimeOffset fromUtc,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var connectionString = EnsureInitialized();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        var fromMs = fromUtc.ToUnixTimeMilliseconds();
        var now = _time.GetUtcNow();
        var rawBoundaryMs = now.Subtract(RawResolutionWindow).ToUnixTimeMilliseconds();
        var loadedWindow = now - fromUtc;

        // For wide ranges (>24h), skip the raw-samples window entirely — the chart
        // downsamples to ~180 points regardless, so second-resolution raw samples
        // for the last 6h would get collapsed to a single pixel anyway. Avoiding
        // the UNION saves hundreds of thousands of rows on a 30d load.
        var wideRange = loadedWindow >= TimeSpan.FromHours(24);

        command.CommandText = wideRange
            ? """
              SELECT
                  panel_key,
                  panel_title,
                  series_key,
                  series_name,
                  category,
                  unit,
                  timestamp_ms,
                  value
              FROM (
                  SELECT
                      panel_key,
                      panel_title,
                      series_key,
                      series_name,
                      category,
                      unit,
                      timestamp_ms,
                      value
                  FROM metric_rollups_1m
                  WHERE timestamp_ms >= $fromTimestamp

                  UNION ALL

                  SELECT
                      samples.panel_key,
                      samples.panel_title,
                      samples.series_key,
                      samples.series_name,
                      samples.category,
                      samples.unit,
                      (samples.timestamp_ms / $bucketMs) * $bucketMs AS timestamp_ms,
                      AVG(samples.value) AS value
                  FROM metric_samples AS samples
                  WHERE samples.timestamp_ms >= $fromTimestamp
                    AND NOT EXISTS (
                        SELECT 1
                        FROM metric_rollups_1m AS rollups
                        WHERE rollups.panel_key = samples.panel_key
                          AND rollups.series_key = samples.series_key
                          AND rollups.timestamp_ms = (samples.timestamp_ms / $bucketMs) * $bucketMs
                    )
                  GROUP BY
                      samples.panel_key,
                      samples.panel_title,
                      samples.series_key,
                      samples.series_name,
                      samples.category,
                      samples.unit,
                      (samples.timestamp_ms / $bucketMs)
              )
              ORDER BY panel_key, series_key, timestamp_ms;
              """
            : """
              SELECT
                  panel_key,
                  panel_title,
                  series_key,
                  series_name,
                  category,
                  unit,
                  timestamp_ms,
                  value
              FROM (
                  SELECT
                      panel_key,
                      panel_title,
                      series_key,
                      series_name,
                      category,
                      unit,
                      timestamp_ms,
                      value
                  FROM metric_rollups_1m
                  WHERE timestamp_ms >= $fromTimestamp
                    AND timestamp_ms < $rawBoundary

                  UNION ALL

                  SELECT
                      panel_key,
                      panel_title,
                      series_key,
                      series_name,
                      category,
                      unit,
                      timestamp_ms,
                      value
                  FROM metric_samples
                  WHERE timestamp_ms >= $fromTimestamp
              )
              ORDER BY panel_key, series_key, timestamp_ms;
              """;
        command.Parameters.AddWithValue("$fromTimestamp", fromMs);
        if (wideRange)
        {
            command.Parameters.AddWithValue("$bucketMs", MinuteBucketMilliseconds);
        }
        else
        {
            command.Parameters.AddWithValue("$rawBoundary", rawBoundaryMs);
        }

        return await ReadHistoryAsync(command, 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucket,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var connectionString = EnsureInitialized();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Minute rollups and raw samples never cover the same time (compaction moves samples
        // into rollups and deletes them in one transaction), so the two tables simply stack.
        // Rows come in time order straight off the two timestamp indexes and are averaged into
        // buckets here as they go by. A GROUP BY in SQL sorted every row first, spilling to a
        // temp file: on a year of history, 30 days took 25 s that way and 8 s this way, and the
        // startup read took about half as long.
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM (
                SELECT panel_key, panel_title, series_key, series_name, category, unit, timestamp_ms, value
                FROM metric_rollups_1m
                WHERE timestamp_ms >= $fromTimestamp AND timestamp_ms < $toTimestamp
                UNION ALL
                SELECT panel_key, panel_title, series_key, series_name, category, unit, timestamp_ms, value
                FROM metric_samples
                WHERE timestamp_ms >= $fromTimestamp AND timestamp_ms < $toTimestamp)
            ORDER BY timestamp_ms;
            """;
        command.Parameters.AddWithValue("$fromTimestamp", fromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$toTimestamp", toUtc.ToUnixTimeMilliseconds());

        var bucketMs = (long)Math.Max(0d, bucket.TotalMilliseconds);
        return await ReadHistoryAsync(command, bucketMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Groups rows into panels and series, averaging each series into
    /// <paramref name="bucketMs"/>-wide buckets (timestamped at the bucket's start) or keeping
    /// every row when it is zero. Each series' rows must arrive in time order.
    /// </summary>
    private static async Task<IReadOnlyList<MetricSeriesHistory>> ReadHistoryAsync(
        SqliteCommand command,
        long bucketMs,
        CancellationToken cancellationToken)
    {
        var panelMap = new Dictionary<string, PanelAccumulator>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var panelKey = reader.GetString(0);
            if (!panelMap.TryGetValue(panelKey, out var panel))
            {
                panel = new PanelAccumulator(
                    panelKey,
                    reader.GetString(1),
                    (MetricCategory)reader.GetInt32(4),
                    (MetricUnit)reader.GetInt32(5));
                panelMap.Add(panelKey, panel);
            }

            panel.GetSeries(reader.GetString(2), reader).Add(reader.GetInt64(6), reader.GetDouble(7), bucketMs);
        }

        // Panels by title, then key; series by key: the order the reads always returned.
        return panelMap.Values
            .OrderBy(panel => panel.PanelTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(panel => panel.PanelKey, StringComparer.Ordinal)
            .Select(panel => panel.ToHistory())
            .ToArray();
    }

    /// <summary>
    /// Brings the database in line with <paramref name="config"/>'s retention now (at launch and
    /// after a retention change), then gives the space back if at least half the file is free.
    /// Works in bounded steps and lets the collector's writes in between them. In one transaction,
    /// dropping 30 days from a year of history held the store for 36 s and catching up after a
    /// night off for over 5 s, both past the collector's 5 s write timeout, so those samples were
    /// lost; in steps, no write waited more than about a second.
    /// </summary>
    public async Task PruneAsync(SwissOllyConfig config, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _ = EnsureInitialized();
        config = config.Normalize();
        var now = _time.GetUtcNow();
        var more = true;
        while (more)
        {
            more = await OnWriteConnectionAsync(
                async connection =>
                {
                    _config = config;
                    var due = await CompactAndPruneStepAsync(connection, config, now, cancellationToken).ConfigureAwait(false);
                    _lastMaintenanceTimestamp = _time.GetTimestamp();
                    return due;
                },
                cancellationToken).ConfigureAwait(false);
        }

        switch (await OnWriteConnectionAsync(connection => ChooseReclaimAsync(connection, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            case Reclaim.Incremental:
                while (await OnWriteConnectionAsync(
                    async connection =>
                    {
                        await ExecuteAsync(connection, $"PRAGMA incremental_vacuum({VacuumStepPages}); PRAGMA wal_checkpoint(PASSIVE);", cancellationToken).ConfigureAwait(false);
                        return await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false) > 0;
                    },
                    cancellationToken).ConfigureAwait(false))
                {
                }

                break;
            case Reclaim.Convert:
                await OnWriteConnectionAsync(
                    async connection =>
                    {
                        try
                        {
                            await ExecuteAsync(connection, "PRAGMA auto_vacuum = INCREMENTAL; VACUUM; PRAGMA wal_checkpoint(PASSIVE);", cancellationToken).ConfigureAwait(false);
                        }
                        catch (SqliteException)
                        {
                            // Best effort (a VACUUM needs room for a copy): the free pages still get reused.
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the write connection, holding the store meanwhile. Once
    /// the store is being disposed it runs nothing and returns the default, ending a prune.
    /// </summary>
    private async Task<T?> OnWriteConnectionAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return default;
            }

            if (_writeConnection is null || _insertCommand is null)
            {
                await OpenWriteConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            return await work(EnsureWriteConnectionInitialized()).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            // Keep the semaphore alive so operations that were already queued can
            // acquire it, observe disposal, and release it without racing Dispose.
            _gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            throw new ObjectDisposedException(nameof(SqliteMetricStore));
        }
    }

    private string EnsureInitialized() =>
        _connectionString ?? throw new InvalidOperationException("The SQLite store has not been initialized.");

    private SqliteConnection EnsureWriteConnectionInitialized() =>
        _writeConnection ?? throw new InvalidOperationException("The SQLite write connection has not been initialized.");

    private SqliteCommand EnsureInsertCommandInitialized() =>
        _insertCommand ?? throw new InvalidOperationException("The SQLite insert command has not been initialized.");

    /// <summary>
    /// Rolls up and prunes every <see cref="MaintenanceInterval"/> of elapsed time. Scheduled on
    /// the monotonic clock: on the wall clock, setting the time back an hour (or a day) stopped
    /// rollups and pruning for that long while raw samples piled up.
    /// </summary>
    private async Task MaybeMaintainAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_config is null || _time.GetElapsedTime(_lastMaintenanceTimestamp) < MaintenanceInterval)
        {
            return;
        }

        // With more due than one step does (the clock jumped ahead, say), the next writes carry
        // on, a step each, rather than this one holding the store until it is all done.
        if (!await CompactAndPruneStepAsync(connection, _config, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            _lastMaintenanceTimestamp = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// One step of rolling up and pruning, in one transaction, from the oldest rows: at most an
    /// hour of raw samples and a day of expired rollups. Returns true while more is due.
    /// </summary>
    private static async Task<bool> CompactAndPruneStepAsync(
        SqliteConnection connection,
        SwissOllyConfig config,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retentionWindow = config.GetRetentionWindow();
        var retentionCutoffMs = now.Subtract(retentionWindow).ToUnixTimeMilliseconds();
        var rawCutoffMs = now.Subtract(RawResolutionWindow).ToUnixTimeMilliseconds();
        rawCutoffMs = rawCutoffMs / MinuteBucketMilliseconds * MinuteBucketMilliseconds;

        // Raw samples are rolled up to the raw cutoff, or, with retention shorter than the raw
        // window, dropped at the retention cutoff. Steps end on whole minutes, so a minute is
        // always rolled up whole.
        var rollUp = retentionWindow > RawResolutionWindow;
        var rawDueBeforeMs = rollUp ? rawCutoffMs : retentionCutoffMs;
        var rawStepEndMs = await StepEndAsync(connection, "metric_samples", rawDueBeforeMs, RawStepMilliseconds, cancellationToken).ConfigureAwait(false);
        var rollupStepEndMs = await StepEndAsync(connection, "metric_rollups_1m", retentionCutoffMs, RollupStepMilliseconds, cancellationToken).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (rollUp)
        {
            await using var compactCommand = connection.CreateCommand();
            compactCommand.Transaction = transaction;
            // A minute already rolled up only gets more raw samples after the clock was set back
            // past the raw window; average the two instead of letting the newer one overwrite it.
            compactCommand.CommandText =
                """
                INSERT INTO metric_rollups_1m (
                    panel_key,
                    panel_title,
                    series_key,
                    series_name,
                    category,
                    unit,
                    timestamp_ms,
                    value
                )
                SELECT
                    panel_key,
                    panel_title,
                    series_key,
                    series_name,
                    category,
                    unit,
                    (timestamp_ms / $bucketMs) * $bucketMs AS timestamp_ms,
                    AVG(value) AS value
                FROM metric_samples
                WHERE timestamp_ms >= $retentionCutoff
                  AND timestamp_ms < $rawCutoff
                GROUP BY
                    panel_key,
                    panel_title,
                    series_key,
                    series_name,
                    category,
                    unit,
                    (timestamp_ms / $bucketMs)
                ON CONFLICT (panel_key, series_key, timestamp_ms)
                    DO UPDATE SET value = (metric_rollups_1m.value + excluded.value) / 2;
                """;
            compactCommand.Parameters.AddWithValue("$retentionCutoff", retentionCutoffMs);
            compactCommand.Parameters.AddWithValue("$rawCutoff", rawStepEndMs);
            compactCommand.Parameters.AddWithValue("$bucketMs", MinuteBucketMilliseconds);
            await compactCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var deleteRawCommand = connection.CreateCommand();
        deleteRawCommand.Transaction = transaction;
        deleteRawCommand.CommandText = "DELETE FROM metric_samples WHERE timestamp_ms < $cutoff;";
        deleteRawCommand.Parameters.AddWithValue("$cutoff", rawStepEndMs);
        await deleteRawCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var deleteRollupCommand = connection.CreateCommand();
        deleteRollupCommand.Transaction = transaction;
        deleteRollupCommand.CommandText = "DELETE FROM metric_rollups_1m WHERE timestamp_ms < $cutoff;";
        deleteRollupCommand.Parameters.AddWithValue("$cutoff", rollupStepEndMs);
        await deleteRollupCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var more = rawStepEndMs < rawDueBeforeMs || rollupStepEndMs < retentionCutoffMs;
        if (!more)
        {
            // Once the last step is done (the WAL checkpoints itself in between). Vacuum before
            // the checkpoint, so the file shrinks now rather than at the next one.
            await ExecuteAsync(connection, "PRAGMA optimize; PRAGMA incremental_vacuum(512); PRAGMA wal_checkpoint(PASSIVE);", cancellationToken).ConfigureAwait(false);
        }

        return more;
    }

    /// <summary>
    /// Where a step through <paramref name="table"/> ends: <paramref name="stepMs"/> past the
    /// minute of its oldest row, or <paramref name="dueBeforeMs"/> if that comes first.
    /// </summary>
    private static async Task<long> StepEndAsync(
        SqliteConnection connection,
        string table,
        long dueBeforeMs,
        long stepMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MIN(timestamp_ms) FROM {table};";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long oldestMs
            ? Math.Min(dueBeforeMs, (oldestMs / MinuteBucketMilliseconds * MinuteBucketMilliseconds) + stepMs)
            : dueBeforeMs;
    }

    private enum Reclaim
    {
        None,
        Incremental,
        Convert,
    }

    /// <summary>
    /// Whether to give the space of deleted history back to the disk: only when at least half
    /// the file is free (retention was lowered, or the app was closed for longer than the
    /// retention). The 15-minute maintenance returns at most 2 MB a time, so lowering retention
    /// from a year to a day would take weeks to show. Smaller amounts, such as the raw samples a
    /// launch after a night off rolls up, stay in the file for the next hours of samples to reuse.
    /// </summary>
    private static async Task<Reclaim> ChooseReclaimAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var freePages = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);
        var pages = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
        if (freePages == 0 || freePages * 2 < pages)
        {
            return Reclaim.None;
        }

        if (await ScalarAsync(connection, "PRAGMA auto_vacuum;", cancellationToken).ConfigureAwait(false) == IncrementalAutoVacuum)
        {
            return Reclaim.Incremental;
        }

        // Created before auto_vacuum was set first, so it can't shrink in place. Convert it once
        // with a VACUUM, when what is left is small enough to rewrite in a second or two.
        var pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false);
        return (pages - freePages) * pageSize <= MaxConvertingVacuumBytes ? Reclaim.Convert : Reclaim.None;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (_insertCommand is not null)
        {
            await _insertCommand.DisposeAsync().ConfigureAwait(false);
            _insertCommand = null;
        }

        if (_writeConnection is not null)
        {
            // Explicitly checkpoint the WAL before closing so the database
            // file is self-contained and safe for the next launch
            try
            {
                await using var checkpoint = _writeConnection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort — connection may already be in a bad state
            }

            await _writeConnection.DisposeAsync().ConfigureAwait(false);
            _writeConnection = null;
        }

        if (_connectionString is not null)
        {
            ReleasePooledConnections(_connectionString);
        }
    }

    // Reads open pooled connections, and the pool keeps idle ones (and so the database file) open
    // after they close. Release them when the store moves to another folder or is disposed, so
    // the old file isn't held until the app exits.
    private static void ReleasePooledConnections(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private sealed class PanelAccumulator
    {
        private readonly Dictionary<string, SeriesAccumulator> _series = new(StringComparer.OrdinalIgnoreCase);

        public PanelAccumulator(string panelKey, string panelTitle, MetricCategory category, MetricUnit unit)
        {
            PanelKey = panelKey;
            PanelTitle = panelTitle;
            Category = category;
            Unit = unit;
        }

        public string PanelKey { get; }

        public string PanelTitle { get; }

        public MetricCategory Category { get; }

        public MetricUnit Unit { get; }

        /// <summary>The series for this row, its name read from the row only the first time.</summary>
        public SeriesAccumulator GetSeries(string seriesKey, SqliteDataReader row)
        {
            if (!_series.TryGetValue(seriesKey, out var series))
            {
                series = new SeriesAccumulator(seriesKey, row.GetString(3));
                _series.Add(seriesKey, series);
            }

            return series;
        }

        public MetricSeriesHistory ToHistory() =>
            new(
                PanelKey,
                PanelTitle,
                Category,
                Unit,
                _series.Values
                    .OrderBy(series => series.SeriesKey, StringComparer.Ordinal)
                    .Select(series => new MetricSeriesHistoryItem(series.SeriesKey, series.SeriesName, series.Finish()))
                    .ToArray());
    }

    private sealed class SeriesAccumulator
    {
        private readonly List<MetricPoint> _points = [];
        private long _bucketStartMs;
        private double _bucketSum;
        private int _bucketCount;

        public SeriesAccumulator(string seriesKey, string seriesName)
        {
            SeriesKey = seriesKey;
            SeriesName = seriesName;
        }

        public string SeriesKey { get; }

        public string SeriesName { get; }

        /// <summary>
        /// Adds a row, or folds it into the running average of its bucket when
        /// <paramref name="bucketMs"/> is set. Integer division, as SQLite's, so buckets start
        /// where the rollups' minutes and the old GROUP BY's buckets did.
        /// </summary>
        public void Add(long timestampMs, double value, long bucketMs)
        {
            if (bucketMs <= 0)
            {
                _points.Add(new MetricPoint(DateTimeOffset.FromUnixTimeMilliseconds(timestampMs), value));
                return;
            }

            var bucketStartMs = timestampMs / bucketMs * bucketMs;
            if (_bucketCount > 0 && bucketStartMs != _bucketStartMs)
            {
                FlushBucket();
            }

            _bucketStartMs = bucketStartMs;
            _bucketSum += value;
            _bucketCount++;
        }

        public MetricPoint[] Finish()
        {
            FlushBucket();
            return [.. _points];
        }

        private void FlushBucket()
        {
            if (_bucketCount == 0)
            {
                return;
            }

            _points.Add(new MetricPoint(DateTimeOffset.FromUnixTimeMilliseconds(_bucketStartMs), _bucketSum / _bucketCount));
            _bucketSum = 0;
            _bucketCount = 0;
        }
    }
}
