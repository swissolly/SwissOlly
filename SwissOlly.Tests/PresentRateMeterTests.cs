using SwissOlly.Core.Models;

namespace SwissOlly.Tests;

public class PresentRateMeterTests
{
    private const long Second = 10_000;

    /// <summary>Presents every <paramref name="interval"/> ticks over [from, to), arriving as they happen.</summary>
    private static void Present(PresentRateMeter meter, PresentSource source, long from, long to, long interval)
    {
        for (var tick = from; tick < to; tick += interval)
        {
            meter.Add(source, tick, tick);
        }
    }

    [Fact]
    public void Runtime_Presents_Give_The_Frame_Rate()
    {
        var meter = new PresentRateMeter(Second, nowTick: 0);
        Present(meter, PresentSource.Runtime, 0, 3 * Second, Second / 100);

        var reading = meter.Read(3 * Second);

        Assert.Equal(PresentReadingKind.Rate, reading.Kind);
        Assert.Equal(PresentSource.Runtime, reading.Source);
        Assert.Equal(100d, reading.FramesPerSecond, 1);
    }

    [Fact]
    public void A_Game_With_Only_Kernel_Presents_Still_Gets_A_Rate()
    {
        // Vulkan and OpenGL games: nothing from DXGI or Direct3D 9, which read "--" for good.
        var meter = new PresentRateMeter(Second, nowTick: 0);
        Present(meter, PresentSource.Kernel, 0, 3 * Second, Second / 144);

        var reading = meter.Read(3 * Second);

        Assert.Equal(PresentReadingKind.Rate, reading.Kind);
        Assert.Equal(PresentSource.Kernel, reading.Source);
        Assert.InRange(reading.FramesPerSecond, 143d, 146d);
    }

    [Fact]
    public void Runtime_Presents_Are_Used_When_Both_Arrive()
    {
        // The kernel sees the same presents; they must not be added together.
        var meter = new PresentRateMeter(Second, nowTick: 0);
        Present(meter, PresentSource.Runtime, 0, 3 * Second, Second / 60);
        Present(meter, PresentSource.Kernel, 0, 3 * Second, Second / 60);

        var reading = meter.Read(3 * Second);

        Assert.Equal(PresentSource.Runtime, reading.Source);
        Assert.InRange(reading.FramesPerSecond, 59d, 61d);
    }

    [Fact]
    public void The_Kernel_Takes_Over_When_Runtime_Presents_Stop()
    {
        var meter = new PresentRateMeter(Second, nowTick: 0);
        Present(meter, PresentSource.Runtime, 0, Second, Second / 60);
        Present(meter, PresentSource.Kernel, 0, 6 * Second, Second / 120);

        Assert.Equal(PresentSource.Runtime, meter.Read(Second).Source);

        var later = meter.Read(6 * Second);
        Assert.Equal(PresentSource.Kernel, later.Source);
        Assert.InRange(later.FramesPerSecond, 119d, 122d);
    }

    [Fact]
    public void The_Same_Batch_Is_Not_Reported_Twice()
    {
        var meter = new PresentRateMeter(Second, nowTick: 0);
        Present(meter, PresentSource.Runtime, 0, Second, Second / 60);

        Assert.Equal(PresentReadingKind.Rate, meter.Read(Second).Kind);
        Assert.Equal(PresentReadingKind.None, meter.Read(Second + (Second / 2)).Kind);
    }

    [Fact]
    public void A_Stall_Is_Reported_Once_And_A_Rate_Comes_Back_After_It()
    {
        var meter = new PresentRateMeter(Second, nowTick: 0);
        Present(meter, PresentSource.Kernel, 0, Second, Second / 60);
        Assert.Equal(PresentReadingKind.Rate, meter.Read(Second).Kind);

        // Nothing for over two seconds: minimised or loading.
        Assert.Equal(PresentReadingKind.Stalled, meter.Read(4 * Second).Kind);
        Assert.Equal(PresentReadingKind.None, meter.Read(5 * Second).Kind);

        Present(meter, PresentSource.Kernel, 6 * Second, 7 * Second, Second / 30);
        var back = meter.Read(7 * Second);
        Assert.Equal(PresentReadingKind.Rate, back.Kind);
        Assert.InRange(back.FramesPerSecond, 29d, 31d);
    }

    [Fact]
    public void No_Presents_At_All_Is_A_Stall_After_Two_Seconds_Not_Before()
    {
        var meter = new PresentRateMeter(Second, nowTick: 0);

        Assert.Equal(PresentReadingKind.None, meter.Read(Second).Kind);
        Assert.Equal(PresentReadingKind.Stalled, meter.Read(3 * Second).Kind);
    }
}
