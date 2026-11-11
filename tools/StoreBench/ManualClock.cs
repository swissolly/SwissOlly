namespace StoreBench;

/// <summary>
/// A clock the tool moves by hand, so a year of history can be written in minutes and the
/// store's maintenance still runs every 15 simulated minutes, as it would in the app.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ManualClock(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Time passes: the wall clock and the monotonic clock move together.</summary>
    public void AdvanceTo(DateTimeOffset utcNow)
    {
        if (utcNow > _utcNow)
        {
            _timestamp += (utcNow - _utcNow).Ticks;
        }

        _utcNow = utcNow;
    }
}
