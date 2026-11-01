using Vaktr.Core.Interfaces;
using Vaktr.Core.Models;

namespace Vaktr.Collector;

public sealed class CollectorService : IAsyncDisposable
{
    private static readonly TimeSpan InitialCollectionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecurringCollectionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly IMetricCollector _collector;
    private readonly IMetricStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _loopCancellation;
    private PeriodicTimer? _timer;
    private Task? _loopTask;

    // The collector is synchronous PDH/WMI work that only checks its token on entry, so a timeout
    // can abandon a sample but never stop it. These track the call still running: later ticks (and
    // a loop restarted by Apply) skip instead of entering the collector concurrently, and disposal
    // waits for it so PDH buffers are not freed underneath it.
    private int _collecting;
    private Task? _inFlightCollect;

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

        var inFlight = Volatile.Read(ref _inFlightCollect);
        var collectorIdle = inFlight is null || inFlight.IsCompleted;
        if (!collectorIdle)
        {
            try
            {
                await inFlight!.WaitAsync(ShutdownGrace).ConfigureAwait(false);
                collectorIdle = true;
            }
            catch (TimeoutException)
            {
                // Still wedged: leave its handles to process exit rather than free them mid-call.
            }
            catch
            {
                collectorIdle = true;
            }
        }

        if (collectorIdle)
        {
            await _collector.DisposeAsync().ConfigureAwait(false);
        }

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
        if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
        {
            // The previous sample is still stuck in the collector; it was already reported.
            return;
        }

        var collect = Task.Run(() => _collector.CollectAsync(cancellationToken), CancellationToken.None);
        Volatile.Write(ref _inFlightCollect, collect);
        _ = collect.ContinueWith(
            completed =>
            {
                _ = completed.Exception; // observe faults from samples that were abandoned
                Volatile.Write(ref _collecting, 0);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            var snapshot = await collect.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            await _store.AppendSnapshotAsync(snapshot, timeoutCancellation.Token).ConfigureAwait(false);
            RaiseSnapshotCollected(snapshot);
        }
        catch (Exception ex) when (ex is TimeoutException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
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
