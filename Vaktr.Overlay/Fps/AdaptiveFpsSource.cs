using Vaktr.Overlay.Rendering;

namespace Vaktr.Overlay.Fps;

/// <summary>
/// Prefers exact present-based FPS, degrading to compositor timing when the machine can't
/// give us an ETW session. Callers see one <see cref="IFpsSource"/> and are told, via
/// <see cref="AccuracyChanged"/>, which one they actually got.
/// </summary>
internal sealed class AdaptiveFpsSource : IFpsSource
{
    private readonly DwmFpsSource _dwm = new();
    private EtwPresentFpsSource? _etw;
    private IFpsSource? _active;
    private bool _etwUsable;
    private bool _probed;

    public event Action<double>? FpsUpdated;
    public event Action<string>? ErrorOccurred;

    /// <summary>Raised when the live source changes, so the UI can explain what it is showing.</summary>
    public event Action<FpsAccuracy>? AccuracyChanged;

    public FpsAccuracy Accuracy => _active?.Accuracy ?? FpsAccuracy.Approximate;

    public void StartTracking(uint pid)
    {
        StopTracking();

        if (!_probed)
        {
            _probed = true;
            _etwUsable = EtwPresentFpsSource.IsAvailable();
            OverlayLog.Write($"FPS source probe: ETW {(_etwUsable ? "available" : "unavailable")}");
        }

        if (_etwUsable)
        {
            _etw = new EtwPresentFpsSource();
            Attach(_etw);
            _etw.StartTracking(pid);

            if (_etw.IsRunning)
            {
                _active = _etw;
                AccuracyChanged?.Invoke(FpsAccuracy.Exact);
                return;
            }

            // Session creation failed after the probe said it should work — don't retry per game.
            OverlayLog.Write("FPS: ETW start failed, falling back to compositor timing");
            Detach(_etw);
            _etw.Dispose();
            _etw = null;
            _etwUsable = false;
        }

        Attach(_dwm);
        _dwm.StartTracking(pid);
        _active = _dwm;
        AccuracyChanged?.Invoke(FpsAccuracy.Approximate);
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
        // A startup failure is handled inline in StartTracking; anything reaching here is a
        // runtime condition the controller should surface (stall, exclusive fullscreen).
        ErrorOccurred?.Invoke(reason);
    }

    public void Dispose()
    {
        StopTracking();
        _dwm.Dispose();
        _etw?.Dispose();
    }
}
