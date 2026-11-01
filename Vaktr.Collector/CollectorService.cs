using Vaktr.Core.Interfaces;
using Vaktr.Core.Models;

namespace Vaktr.Collector;

public sealed class CollectorService : IAsyncDisposable
{
    private static readonly TimeSpan InitialCollectionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecurringCollectionTimeout = TimeSpan.FromSeconds(5);

    private readonly IMetricCollector _collector;
    private readonly IMetricStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _loopCancellation;
    private PeriodicTimer? _timer;
    private Task? _loopTask;

    public CollectorService(IMetricCollector collector, IMetricStore store)
    {
        _collector = collector;
        _store = store;
    }

    public event EventHandler<MetricSnapshot>? SnapshotCollected;
    public event EventHandler<Exception>? CollectionFailed;

    public async Task StartAsync(VaktrConfig config, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);

            await _store.InitializeAsync(config, cancellationToken).ConfigureAwait(false);

            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(config.ScrapeIntervalSeconds));
            _loopTask = Task.Factory.StartNew(
                    () => RunLoopAsync(_timer, _loopCancellation.Token),
                    _loopCancellation.Token,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _collector.DisposeAsync().ConfigureAwait(false);
        await _store.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task RunLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch
        {
            // If the runtime denies thread priority changes, we still keep the collector running.
        }

        await CollectOnceAsync(cancellationToken, InitialCollectionTimeout).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await CollectOnceAsync(cancellationToken, RecurringCollectionTimeout).ConfigureAwait(false);
        }
    }

    private async Task CollectOnceAsync(CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            var snapshot = await _collector.CollectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            await _store.AppendSnapshotAsync(snapshot, timeoutCancellation.Token).ConfigureAwait(false);
            RaiseSnapshotCollected(snapshot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RaiseCollectionFailed(new TimeoutException($"Telemetry sampling exceeded {timeout.TotalSeconds:0.#} seconds."));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseCollectionFailed(ex);
        }
    }

    // Subscribers run on this loop's thread. An exception escaping one used to propagate out of
    // the loop - first from SnapshotCollected, then again from the CollectionFailed raised in the
    // catch - faulting a task nobody observes, so sampling stopped silently for the session.
    private void RaiseSnapshotCollected(MetricSnapshot snapshot)
    {
        try
        {
            SnapshotCollected?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            RaiseCollectionFailed(ex);
        }
    }

    private void RaiseCollectionFailed(Exception exception)
    {
        try
        {
            CollectionFailed?.Invoke(this, exception);
        }
        catch
        {
            // Nothing left to report to; keep sampling.
        }
    }

    private Task StopInternalAsync()
    {
        var loopCancellation = _loopCancellation;
        var timer = _timer;

        _loopTask = null;
        _timer = null;
        _loopCancellation = null;

        // Cancel and dispose immediately — don't wait for the loop to exit.
        // The loop handles OperationCanceledException gracefully and will wind down
        // on its own. No need to block shutdown waiting for it.
        timer?.Dispose();
        loopCancellation?.Cancel();
        loopCancellation?.Dispose();

        return Task.CompletedTask;
    }
}
