using Microsoft.Data.Sqlite;
using Vaktr.Core.Interfaces;
using Vaktr.Core.Models;

namespace Vaktr.Store.Persistence;

public sealed class SqliteMetricStore : IMetricStore
{
    private static readonly TimeSpan RawResolutionWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(15);
    private const long MinuteBucketMilliseconds = 60_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private VaktrConfig? _config;
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
    private DateTimeOffset _nextMaintenanceUtc = DateTimeOffset.MinValue;
    private int _disposeStarted;

    public async Task InitializeAsync(VaktrConfig config, CancellationToken cancellationToken)
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

    private async Task InitializeCoreAsync(VaktrConfig config, CancellationToken cancellationToken)
    {
        config = config.Normalize();
        Directory.CreateDirectory(config.StorageDirectory);
        var nextConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = config.GetDatabasePath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        if (string.Equals(_connectionString, nextConnectionString, StringComparison.Ordinal) &&
            _writeConnection is not null &&
            _insertCommand is not null)
        {
            _config = config;
            _nextMaintenanceUtc = DateTimeOffset.UtcNow.Add(MaintenanceInterval);
            return;
        }

        _connectionString = nextConnectionString;
        _config = config;

        if (_insertCommand is not null)
        {
            await _insertCommand.DisposeAsync().ConfigureAwait(false);
            _insertCommand = null;
        }

        if (_writeConnection is not null)
        {
            await _writeConnection.DisposeAsync().ConfigureAwait(false);
        }

        _writeConnection = new SqliteConnection(_connectionString);
        await _writeConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = _writeConnection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA auto_vacuum = INCREMENTAL;

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

        _nextMaintenanceUtc = DateTimeOffset.UtcNow.Add(MaintenanceInterval);
    }

    public async Task AppendSnapshotAsync(MetricSnapshot snapshot, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (snapshot.Samples.Count == 0)
        {
            return;
        }

        var connectionString = EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var connection = EnsureWriteConnectionInitialized();
            var command = EnsureInsertCommandInitialized();
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
            await MaybeMaintainAsync(connection, snapshot.Timestamp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
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

        var command = connection.CreateCommand();
        var fromMs = fromUtc.ToUnixTimeMilliseconds();
        var rawBoundaryMs = DateTimeOffset.UtcNow.Subtract(RawResolutionWindow).ToUnixTimeMilliseconds();
        var loadedWindow = DateTimeOffset.UtcNow - fromUtc;

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

            var point = new MetricPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                reader.GetDouble(7));

            panel.AddPoint(reader.GetString(2), reader.GetString(3), point);
        }

        return panelMap.Values
            .Select(panel => panel.ToHistory())
            .OrderBy(panel => panel.PanelTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task PruneAsync(VaktrConfig config, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _ = EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _config = config.Normalize();
            var connection = EnsureWriteConnectionInitialized();
            await CompactAndPruneAsync(connection, _config, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            _nextMaintenanceUtc = DateTimeOffset.UtcNow.Add(MaintenanceInterval);
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

    private async Task MaybeMaintainAsync(SqliteConnection connection, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        if (_config is null || timestamp < _nextMaintenanceUtc)
        {
            return;
        }

        await CompactAndPruneAsync(connection, _config, timestamp, cancellationToken).ConfigureAwait(false);
        _nextMaintenanceUtc = timestamp.Add(MaintenanceInterval);
    }

    private static async Task CompactAndPruneAsync(
        SqliteConnection connection,
        VaktrConfig config,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retentionWindow = config.GetRetentionWindow();
        var retentionCutoffMs = now.Subtract(retentionWindow).ToUnixTimeMilliseconds();
        var rawCutoffMs = now.Subtract(RawResolutionWindow).ToUnixTimeMilliseconds();
        rawCutoffMs = rawCutoffMs / MinuteBucketMilliseconds * MinuteBucketMilliseconds;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Only compact raw samples into rollups if retention extends beyond the raw window.
        // If retention is shorter than the raw window, we skip compaction and just delete everything older.
        if (retentionWindow > RawResolutionWindow)
        {
            var compactCommand = connection.CreateCommand();
            compactCommand.Transaction = transaction;
            compactCommand.CommandText =
                """
                INSERT OR REPLACE INTO metric_rollups_1m (
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
                    (timestamp_ms / $bucketMs);
                """;
            compactCommand.Parameters.AddWithValue("$retentionCutoff", retentionCutoffMs);
            compactCommand.Parameters.AddWithValue("$rawCutoff", rawCutoffMs);
            compactCommand.Parameters.AddWithValue("$bucketMs", MinuteBucketMilliseconds);
            await compactCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Delete raw samples: if retention < raw window, honor the retention cutoff directly
        var deleteRawCommand = connection.CreateCommand();
        deleteRawCommand.Transaction = transaction;
        deleteRawCommand.CommandText = "DELETE FROM metric_samples WHERE timestamp_ms < $cutoff;";
        deleteRawCommand.Parameters.AddWithValue("$cutoff", retentionWindow > RawResolutionWindow ? rawCutoffMs : retentionCutoffMs);
        await deleteRawCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Delete rollup data older than retention
        var deleteRollupCommand = connection.CreateCommand();
        deleteRollupCommand.Transaction = transaction;
        deleteRollupCommand.CommandText = "DELETE FROM metric_rollups_1m WHERE timestamp_ms < $cutoff;";
        deleteRollupCommand.Parameters.AddWithValue("$cutoff", retentionCutoffMs);
        await deleteRollupCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var maintenanceCommand = connection.CreateCommand();
        maintenanceCommand.CommandText =
            """
            PRAGMA optimize;
            PRAGMA wal_checkpoint(PASSIVE);
            PRAGMA incremental_vacuum(512);
            """;
        await maintenanceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                var checkpoint = _writeConnection.CreateCommand();
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

        public void AddPoint(string seriesKey, string seriesName, MetricPoint point)
        {
            if (!_series.TryGetValue(seriesKey, out var series))
            {
                series = new SeriesAccumulator(seriesKey, seriesName);
                _series.Add(seriesKey, series);
            }

            series.Points.Add(point);
        }

        public MetricSeriesHistory ToHistory() =>
            new(
                PanelKey,
                PanelTitle,
                Category,
                Unit,
                _series.Values
                    .Select(series => new MetricSeriesHistoryItem(series.SeriesKey, series.SeriesName, series.Points.ToArray()))
                    .ToArray());
    }

    private sealed class SeriesAccumulator
    {
        public SeriesAccumulator(string seriesKey, string seriesName)
        {
            SeriesKey = seriesKey;
            SeriesName = seriesName;
        }

        public string SeriesKey { get; }

        public string SeriesName { get; }

        public List<MetricPoint> Points { get; } = [];
    }
}
