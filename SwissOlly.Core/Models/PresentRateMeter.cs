namespace SwissOlly.Core.Models;

/// <summary>Where a present event came from.</summary>
public enum PresentSource
{
    /// <summary>The graphics runtime in the game's process: DXGI (Direct3D 10, 11, 12) or Direct3D 9.</summary>
    Runtime,

    /// <summary>The graphics kernel, which every present passes through whatever the API.</summary>
    Kernel,
}

public enum PresentReadingKind
{
    /// <summary>Nothing new to show: too few presents yet, or none since the last reading.</summary>
    None,

    /// <summary>The process has stopped presenting (minimised, loading, hidden). Given once per stall.</summary>
    Stalled,

    /// <summary>A frame rate.</summary>
    Rate,
}

public readonly record struct PresentReading(PresentReadingKind Kind, double FramesPerSecond, PresentSource Source);

/// <summary>
/// Turns one process's present events into a frame rate: the presents in the last second, from
/// the first to the newest. Times are in ticks of any steady clock, so this runs without ETW.
///
/// Runtime events are used while they arrive, as before. A game that presents through Vulkan or
/// OpenGL has none, and read "--" for good; for those the kernel's events are counted instead.
/// The runtime stays first because it is the path every game so far was checked on.
/// Not thread-safe: the caller locks.
/// </summary>
public sealed class PresentRateMeter
{
    private sealed class Window
    {
        public readonly Queue<long> EventTicks = new();
        public long Newest;
        public long LastArrival;
        public long LastRead;
    }

    private readonly long _ticksPerSecond;
    private readonly long _windowTicks;
    private readonly long _stallTicks;
    private readonly Window _runtime = new();
    private readonly Window _kernel = new();
    private bool _stallReported;

    public PresentRateMeter(long ticksPerSecond, long nowTick, double windowSeconds = 1.0, double stallSeconds = 2.0)
    {
        _ticksPerSecond = Math.Max(1, ticksPerSecond);
        _windowTicks = (long)(windowSeconds * _ticksPerSecond);
        _stallTicks = (long)(stallSeconds * _ticksPerSecond);
        _runtime.LastArrival = _kernel.LastArrival = nowTick;
    }

    /// <summary>
    /// Records a present. <paramref name="eventTick"/> is when it happened and
    /// <paramref name="arrivalTick"/> when it reached us: events arrive in batches about a second
    /// apart, so the rate is worked out from the first and a stall from the second.
    /// </summary>
    public void Add(PresentSource source, long eventTick, long arrivalTick)
    {
        var window = source == PresentSource.Runtime ? _runtime : _kernel;
        window.EventTicks.Enqueue(eventTick);
        window.Newest = eventTick;
        window.LastArrival = arrivalTick;
    }

    public PresentReading Read(long nowTick)
    {
        var runtimeLive = Refresh(_runtime, nowTick);
        var kernelLive = Refresh(_kernel, nowTick);
        if (!runtimeLive && !kernelLive)
        {
            if (_stallReported)
            {
                return default;
            }

            _stallReported = true;
            return new PresentReading(PresentReadingKind.Stalled, 0d, PresentSource.Runtime);
        }

        _stallReported = false;

        var source = _runtime.EventTicks.Count >= 2 ? PresentSource.Runtime : PresentSource.Kernel;
        var window = source == PresentSource.Runtime ? _runtime : _kernel;
        if (window.EventTicks.Count < 2 || window.Newest == window.LastRead)
        {
            // Events come in batches slower than readings are asked for: without the second test
            // the same second would be reported twice in a row.
            return default;
        }

        window.LastRead = window.Newest;
        var span = (window.Newest - window.EventTicks.Peek()) / (double)_ticksPerSecond;
        return span <= 0d
            ? default
            : new PresentReading(PresentReadingKind.Rate, (window.EventTicks.Count - 1) / span, source);
    }

    /// <summary>Drops presents older than the window, or all of them once none has arrived for the
    /// stall time. Returns whether the source is still arriving.</summary>
    private bool Refresh(Window window, long nowTick)
    {
        if (nowTick - window.LastArrival > _stallTicks)
        {
            window.EventTicks.Clear();
            return false;
        }

        var cutoff = window.Newest - _windowTicks;
        while (window.EventTicks.Count > 2 && window.EventTicks.Peek() < cutoff)
        {
            window.EventTicks.Dequeue();
        }

        return true;
    }
}
