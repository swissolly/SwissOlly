using SwissOlly.Core.Models;

namespace SwissOlly.Core.Interfaces;

public interface IMetricCollector : IAsyncDisposable
{
    Task<MetricSnapshot> CollectAsync(CancellationToken cancellationToken);
}

public interface IMetricStore : IAsyncDisposable
{
    Task InitializeAsync(SwissOllyConfig config, CancellationToken cancellationToken);

    Task AppendSnapshotAsync(MetricSnapshot snapshot, CancellationToken cancellationToken);

    Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(
        DateTimeOffset fromUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// History in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), averaged into
    /// <paramref name="bucket"/>-wide buckets, or as stored when <paramref name="bucket"/> is zero.
    /// Synchronous underneath (SQLite has no async I/O): call it off the UI thread.
    /// </summary>
    Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan bucket,
        CancellationToken cancellationToken);

    Task PruneAsync(SwissOllyConfig config, CancellationToken cancellationToken);
}

public interface IConfigStore
{
    Task<SwissOllyConfig> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(SwissOllyConfig config, CancellationToken cancellationToken);
}
