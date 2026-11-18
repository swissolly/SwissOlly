namespace SwissOlly.Core.Models;

/// <summary>
/// When to try again after the FPS trace session fails to start. Every 5 seconds for the first
/// minute of failures, then every 30 seconds, for as long as the same game stays in front.
///
/// 1.2.0 tried once per run: one failed start (at the first game of the day, or for a window
/// that wasn't a game) turned FPS off until SwissOlly was restarted. Times are in ticks of any
/// steady clock, so this runs without a timer. Not thread-safe: the caller locks.
/// </summary>
public sealed class FpsStartRetry
{
    public static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FastPeriod = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(30);

    private readonly long _ticksPerSecond;
    private long? _firstFailureTick;

    public FpsStartRetry(long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        _ticksPerSecond = ticksPerSecond;
    }

    /// <summary>True after a failed start, until <see cref="Reset"/>.</summary>
    public bool IsFailing => _firstFailureTick.HasValue;

    /// <summary>A start failed at <paramref name="nowTick"/>: how long to wait before the next try.</summary>
    public TimeSpan OnFailure(long nowTick)
    {
        _firstFailureTick ??= nowTick;
        var failingFor = TimeSpan.FromSeconds((double)(nowTick - _firstFailureTick.Value) / _ticksPerSecond);
        return failingFor < FastPeriod ? FastInterval : SlowInterval;
    }

    /// <summary>The session started, or a different game came to the front.</summary>
    public void Reset() => _firstFailureTick = null;
}
