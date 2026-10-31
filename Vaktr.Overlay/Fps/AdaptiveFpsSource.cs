using Vaktr.Overlay.Rendering;

namespace Vaktr.Overlay.Fps;

/// <summary>
/// Uses process-attributed present events for game FPS. It never substitutes compositor cadence,
/// because that measures monitor refresh and can substantially overstate a game's frame rate.
/// </summary>
internal sealed class AdaptiveFpsSource : IFpsSource
{
    private EtwPresentFpsSource? _etw;
    private IFpsSource? _active;
    private bool _etwUsable;
    private bool _probed;
    private bool _unavailableReported;
    private bool _errorRaisedThisStart;
    private string _unavailableReason = "etw-unavailable";

    public event Action<double>? FpsUpdated;
    public event Action<string>? ErrorOccurred;

    /// <summary>Raised when the live source changes, so the UI can explain what it is showing.</summary>
    public event Action<FpsAccuracy>? AccuracyChanged;

    public FpsAccuracy Accuracy => _active?.Accuracy ?? FpsAccuracy.Unavailable;

    public void StartTracking(uint pid)
    {
        StopTracking();

        if (!_probed)
        {
            _probed = true;
            _etwUsable = EtwPresentFpsSource.IsAvailable(out _unavailableReason);
            OverlayLog.Write($"FPS source probe: ETW {(_etwUsable ? "available" : "unavailable")}");
        }

        _errorRaisedThisStart = false;
        if (_etwUsable)
        {
            _etw = new EtwPresentFpsSource();
            Attach(_etw);
            _etw.StartTracking(pid);

            if (_etw.IsRunning)
            {
                _active = _etw;
                _unavailableReported = false;
                AccuracyChanged?.Invoke(FpsAccuracy.Exact);
                return;
            }

            // Session creation failed after the probe said it should work. Do not substitute
            // monitor refresh for a game's FPS; leave the reading unavailable instead.
            Detach(_etw);
            _etw.Dispose();
            _etw = null;
            _etwUsable = false;
            _unavailableReason = "etw-unavailable";
        }

        if (!_errorRaisedThisStart && !_unavailableReported)
        {
            ErrorOccurred?.Invoke(_unavailableReason);
        }

        _unavailableReported = true;
        AccuracyChanged?.Invoke(FpsAccuracy.Unavailable);
    }

    public void StopTracking()
    {
        if (_active == null) return;

        _active.StopTracking();
        Detach(_active);

        if (ReferenceEquals(_active, _etw))
        {
            _etw?.Dispose();
            _etw = null;
        }

        _active = null;
    }

    private void Attach(IFpsSource src)
    {
        src.FpsUpdated += OnFps;
        src.ErrorOccurred += OnError;
    }

    private void Detach(IFpsSource src)
    {
        src.FpsUpdated -= OnFps;
        src.ErrorOccurred -= OnError;
    }

    private void OnFps(double fps) => FpsUpdated?.Invoke(fps);

    private void OnError(string reason)
    {
        _errorRaisedThisStart = true;
        if (reason.StartsWith("etw-", StringComparison.Ordinal))
        {
            _unavailableReason = reason;
        }
        ErrorOccurred?.Invoke(reason);
    }

    public void Dispose()
    {
        StopTracking();
        _etw?.Dispose();
    }
}
