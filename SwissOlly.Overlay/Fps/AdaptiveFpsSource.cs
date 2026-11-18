using System.Diagnostics;
using SwissOlly.Core.Models;

namespace SwissOlly.Overlay.Fps;

/// <summary>
/// Uses process-attributed present events for game FPS. It never substitutes compositor cadence,
/// because that measures monitor refresh and can substantially overstate a game's frame rate.
///
/// A failed start is retried while the same game stays tracked (<see cref="FpsStartRetry"/>).
/// 1.2.0 probed once per run and turned FPS off for good after any failure, which is the likely
/// reason the owner saw no FPS in Overwatch or Battlefield 6 (2026-11-16).
///
/// Thread-safe: the overlay controller calls in from its poll timer and the UI thread, and the
/// retry timer calls in from the thread pool. Events are raised under the lock; the controller's
/// handlers never call back into this class.
/// </summary>
internal sealed class AdaptiveFpsSource : IFpsSource
{
    private readonly object _gate = new();
    private readonly FpsStartRetry _retry = new(Stopwatch.Frequency);
    private EtwPresentFpsSource? _etw;
    private Timer? _retryTimer;
    private uint _pid;
    private int _generation;
    private bool _orphansChecked;
    private bool _disposed;

    public event Action<double>? FpsUpdated;
    public event Action<string>? ErrorOccurred;

    /// <summary>Raised when the live source changes, so the UI can explain what it is showing.</summary>
    public event Action<FpsAccuracy>? AccuracyChanged;

    /// <summary>Raised when a start works after one or more failed tries for the same game.</summary>
    public event Action? Recovered;

    public FpsAccuracy Accuracy
    {
        get
        {
            lock (_gate)
                return _etw is null ? FpsAccuracy.Unavailable : FpsAccuracy.Exact;
        }
    }

    public void StartTracking(uint pid)
    {
        lock (_gate)
        {
            if (_disposed) return;

            StopLocked();
            _pid = pid;
            _generation++;
            _retry.Reset();

            if (!_orphansChecked)
            {
                _orphansChecked = true;
                EtwPresentFpsSource.StopOrphanedSessions();
            }

            TryStartLocked();
        }
    }

    public void StopTracking()
    {
        lock (_gate)
        {
            StopLocked();
            _pid = 0;
            _generation++;
            _retry.Reset();
        }
    }

    private void TryStartLocked()
    {
        var etw = new EtwPresentFpsSource();
        string? failure = null;
        void OnStartError(string reason) => failure ??= reason;

        etw.ErrorOccurred += OnStartError;
        etw.StartTracking(_pid);
        etw.ErrorOccurred -= OnStartError;

        if (etw.IsRunning)
        {
            etw.FpsUpdated += OnFps;
            etw.ErrorOccurred += OnError;
            _etw = etw;

            bool recovered = _retry.IsFailing;
            _retry.Reset();
            AccuracyChanged?.Invoke(FpsAccuracy.Exact);
            if (recovered)
            {
                FpsLog.Write($"FPS: started for pid={_pid} after failed tries");
                FpsLog.Flush();
                Recovered?.Invoke();
            }

            return;
        }

        etw.Dispose();

        // Never substitute monitor refresh for a game's FPS; leave it unavailable and try again.
        var delay = _retry.OnFailure(Stopwatch.GetTimestamp());
        failure ??= "etw-unavailable";
        FpsLog.Write($"FPS: start for pid={_pid} failed ({failure}); next try in {delay.TotalSeconds:0} s");
        FpsLog.Flush();

        AccuracyChanged?.Invoke(FpsAccuracy.Unavailable);
        ErrorOccurred?.Invoke(failure);

        int generation = _generation;
        _retryTimer?.Dispose();
        _retryTimer = new Timer(_ => Retry(generation), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void Retry(int generation)
    {
        try
        {
            lock (_gate)
            {
                // Another game, a stop or a success since this was scheduled.
                if (_disposed || generation != _generation || _pid == 0 || _etw is not null) return;
                TryStartLocked();
            }
        }
        catch (Exception ex)
        {
            // A thread-pool timer must not throw: it would end the process.
            FpsLog.Write($"FPS: retry threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void StopLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;

        if (_etw is null) return;

        _etw.StopTracking();
        _etw.FpsUpdated -= OnFps;
        _etw.ErrorOccurred -= OnError;
        _etw.Dispose();
        _etw = null;
    }

    private void OnFps(double fps) => FpsUpdated?.Invoke(fps);

    private void OnError(string reason) => ErrorOccurred?.Invoke(reason);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
            _pid = 0;
            _generation++;
        }
    }
}
