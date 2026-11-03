using SwissOlly.Collector;
using SwissOlly.Core.Interfaces;

namespace SwissOlly.Tests;

public sealed class CollectorServiceTests
{
    [Fact]
    public async Task Live_Snapshots_Keep_Flowing_While_History_Writes_Fail_And_Recovery_Is_Reported()
    {
        var store = new FlakyStore { FailWrites = true };
        await using var service = new CollectorService(new CountingCollector(), store);
        var snapshots = 0;
        var states = new List<Exception?>();
        var firstSnapshot = new TaskCompletionSource();
        var recovered = new TaskCompletionSource();
        service.SnapshotCollected += (_, _) =>
        {
            Interlocked.Increment(ref snapshots);
            firstSnapshot.TrySetResult();
        };
        service.HistoryWriteStateChanged += (_, error) =>
        {
            lock (states)
            {
                states.Add(error);
            }

            if (error is null)
            {
                recovered.TrySetResult();
            }
        };

        await service.StartAsync(new SwissOllyConfig { ScrapeIntervalSeconds = 1 }.Normalize(), CancellationToken.None);
        await firstSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));

        store.FailWrites = false;
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync();

        Assert.True(Volatile.Read(ref snapshots) >= 2);
        lock (states)
        {
            // Exactly one failure report, not one per sample, then one recovery.
            Assert.Equal(2, states.Count);
            Assert.IsType<IOException>(states[0]);
            Assert.Null(states[1]);
        }
    }

    [Fact]
    public async Task Sampling_Starts_Even_When_The_Store_Cannot_Open()
    {
        var store = new FlakyStore { FailInitialize = true, FailWrites = true };
        await using var service = new CollectorService(new CountingCollector(), store);
        var firstSnapshot = new TaskCompletionSource();
        Exception? reported = null;
        service.SnapshotCollected += (_, _) => firstSnapshot.TrySetResult();
        service.HistoryWriteStateChanged += (_, error) => reported ??= error;

        await service.StartAsync(new SwissOllyConfig { ScrapeIntervalSeconds = 1 }.Normalize(), CancellationToken.None);
        await firstSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync();

        Assert.IsType<DirectoryNotFoundException>(reported);
    }

    private sealed class CountingCollector : IMetricCollector
    {
        public Task<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new MetricSnapshot(now,
            [
                new MetricSample("cpu-total", "CPU Total", "usage", "Usage", MetricCategory.Cpu, MetricUnit.Percent, 12, now),
            ]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FlakyStore : IMetricStore
    {
        public volatile bool FailInitialize;
        public volatile bool FailWrites;

        public Task InitializeAsync(SwissOllyConfig config, CancellationToken cancellationToken) =>
            FailInitialize ? Task.FromException(new DirectoryNotFoundException("E:\\Data is gone")) : Task.CompletedTask;

        public Task AppendSnapshotAsync(MetricSnapshot snapshot, CancellationToken cancellationToken) =>
            FailWrites ? Task.FromException(new IOException("There is not enough space on the disk.")) : Task.CompletedTask;

        public Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(DateTimeOffset fromUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSeriesHistory>>([]);

        public Task<IReadOnlyList<MetricSeriesHistory>> LoadHistoryAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan bucket, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSeriesHistory>>([]);

        public Task PruneAsync(SwissOllyConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
