using SwissOlly.Core.Models;

namespace SwissOlly.Tests;

public class FpsStartRetryTests
{
    private const long Second = 10_000;

    [Fact]
    public void Retries_Every_Five_Seconds_For_The_First_Minute()
    {
        var retry = new FpsStartRetry(Second);

        Assert.Equal(FpsStartRetry.FastInterval, retry.OnFailure(100 * Second));
        Assert.Equal(FpsStartRetry.FastInterval, retry.OnFailure(105 * Second));
        Assert.Equal(FpsStartRetry.FastInterval, retry.OnFailure(159 * Second));
        Assert.True(retry.IsFailing);
    }

    [Fact]
    public void Slows_To_Thirty_Seconds_After_A_Minute_Of_Failures()
    {
        var retry = new FpsStartRetry(Second);
        retry.OnFailure(100 * Second);

        Assert.Equal(FpsStartRetry.SlowInterval, retry.OnFailure(160 * Second));
        Assert.Equal(FpsStartRetry.SlowInterval, retry.OnFailure(600 * Second));
    }

    [Fact]
    public void Reset_Starts_The_Fast_Minute_Again()
    {
        var retry = new FpsStartRetry(Second);
        retry.OnFailure(0);
        retry.OnFailure(120 * Second);

        retry.Reset();

        Assert.False(retry.IsFailing);
        Assert.Equal(FpsStartRetry.FastInterval, retry.OnFailure(500 * Second));
    }

    [Fact]
    public void Never_Gives_Up()
    {
        var retry = new FpsStartRetry(Second);
        retry.OnFailure(0);

        Assert.Equal(FpsStartRetry.SlowInterval, retry.OnFailure(24 * 3600 * Second));
    }
}
