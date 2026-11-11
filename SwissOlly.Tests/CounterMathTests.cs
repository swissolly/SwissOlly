using SwissOlly.Collector;

namespace SwissOlly.Tests;

public sealed class CounterMathTests
{
    private const string Engine3D = "luid_0x00000000_0x0000D1F2_phys_0_eng_0_engtype_3D";
    private const string EngineVideo = "luid_0x00000000_0x0000D1F2_phys_0_eng_3_engtype_VideoDecode";

    [Fact]
    public void Gpu_Usage_Sums_An_Engine_Across_The_Processes_Sharing_It()
    {
        var instances = new Dictionary<string, double>
        {
            [$"pid_100_{Engine3D}"] = 30,
            [$"pid_200_{Engine3D}"] = 25,
            [$"pid_100_{EngineVideo}"] = 40,
        };

        var busiest = CounterMath.BusiestGpuEnginePercent(instances, new Dictionary<string, double>());

        // Task Manager would show 55%: the 3D engine is the busiest once both processes count.
        Assert.Equal(55, busiest);
    }

    [Fact]
    public void Gpu_Usage_Ignores_Total_And_Negative_Readings_And_Caps_At_100()
    {
        var instances = new Dictionary<string, double>
        {
            ["_Total"] = 400,
            [$"pid_1_{Engine3D}"] = 70,
            [$"pid_2_{Engine3D}"] = 60,
            [$"pid_3_{EngineVideo}"] = -5,
        };

        Assert.Equal(100, CounterMath.BusiestGpuEnginePercent(instances, new Dictionary<string, double>()));
    }

    [Fact]
    public void Gpu_Usage_Is_Null_Without_Engines()
    {
        Assert.Null(CounterMath.BusiestGpuEnginePercent(new Dictionary<string, double>(), new Dictionary<string, double>()));
        Assert.Null(CounterMath.BusiestGpuEnginePercent(new Dictionary<string, double> { ["_Total"] = 3 }, new Dictionary<string, double>()));
    }

    [Fact]
    public void Gpu_Temperature_Is_The_Core_Sensor_Not_The_Hot_Spot()
    {
        // LibreHardwareMonitor's sensors on an RTX 4070 Super at idle; Task Manager showed 44.3 C.
        var sensors = new[] { ("GPU Core", 44d), ("GPU Hot Spot", 54.7d), ("GPU Memory Junction", 48d) };

        Assert.Equal(44, CounterMath.GpuTemperatureCelsius(sensors));
    }

    [Fact]
    public void Gpu_Temperature_Falls_Back_To_The_Hottest_Sensor_Without_A_Core_Reading()
    {
        Assert.Equal(61, CounterMath.GpuTemperatureCelsius([("GPU Hot Spot", 61d), ("GPU Memory", 52d)]));
        Assert.Null(CounterMath.GpuTemperatureCelsius([]));
    }

    [Theory]
    [InlineData("pid_4321_luid_0x1_phys_0_eng_0_engtype_3D", "luid_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("luid_0x1_phys_0_eng_0_engtype_3D", "luid_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_", "pid_")]
    public void Gpu_Engine_Key_Drops_Only_The_Process_Prefix(string instance, string expected)
    {
        Assert.Equal(expected, CounterMath.GpuEngineKey(instance));
    }

    [Theory]
    [InlineData(3801d, 106.1d, 4032.861d)]
    [InlineData(3801d, 50d, 1900.5d)]
    [InlineData(3801d, null, 3801d)]
    [InlineData(3801d, 0d, 3801d)]
    public void Clock_Scales_The_Nominal_Frequency_By_Processor_Performance(double nominal, double? performance, double expected)
    {
        Assert.Equal(expected, CounterMath.EffectiveClockMhz(nominal, performance), 3);
    }

    [Theory]
    [InlineData("0,5", 0, 5)]
    [InlineData("1,63", 1, 63)]
    [InlineData("7", 0, 7)]
    public void Processor_Instances_Parse_In_Both_Counter_Sets(string instance, int group, int number)
    {
        Assert.True(CounterMath.TryParseProcessorInstance(instance, out var parsedGroup, out var parsedNumber));
        Assert.Equal(group, parsedGroup);
        Assert.Equal(number, parsedNumber);
    }

    [Theory]
    [InlineData("_Total")]
    [InlineData("0,_Total")]
    [InlineData("")]
    [InlineData("-1")]
    public void Processor_Totals_Are_Not_Cores(string instance)
    {
        Assert.False(CounterMath.TryParseProcessorInstance(instance, out _, out _));
    }

    [Fact]
    public void Process_Cpu_From_Cycles_Is_A_Share_Of_Every_Processor_At_Nominal_Clock()
    {
        // 3.8 GHz nominal, 16 logical processors, 2 seconds: one core fully busy is 7.6e9 cycles.
        Assert.Equal(6.25, CounterMath.ProcessCpuPercentFromCycles(7_600_000_000L, 2, 3800, 16), 6);
        Assert.Equal(100, CounterMath.ProcessCpuPercentFromCycles(long.MaxValue, 2, 3800, 16));
        Assert.Equal(0, CounterMath.ProcessCpuPercentFromCycles(-1, 2, 3800, 16));
        Assert.Equal(0, CounterMath.ProcessCpuPercentFromCycles(1000, 2, 0, 16));
    }

    [Fact]
    public void Process_Cpu_Is_A_Share_Of_The_Whole_Machine()
    {
        // Two seconds of CPU over two seconds on 16 logical processors: one busy core, 6.25%.
        Assert.Equal(6.25, CounterMath.ProcessCpuPercent(2 * 10_000_000L, 2, 16), 6);
        Assert.Equal(100, CounterMath.ProcessCpuPercent(64 * 10_000_000L, 2, 16));
        Assert.Equal(0, CounterMath.ProcessCpuPercent(-5, 2, 16));
        Assert.Equal(0, CounterMath.ProcessCpuPercent(10_000_000L, 0, 16));
    }

    [Fact]
    public void Process_Cpu_Falls_Back_To_Cpu_Time_Where_Cycles_Are_Not_Reported()
    {
        // Cycles available: the cycle count decides.
        Assert.Equal(6.25, CounterMath.ProcessCpuPercent(7_600_000_000L, 0, 2, 3800, 16), 6);
        // No cycle time (some VMs) but CPU time moved: CPU time decides, not a flat 0%.
        Assert.Equal(6.25, CounterMath.ProcessCpuPercent(0, 2 * 10_000_000L, 2, 3800, 16), 6);
        // No nominal clock known: CPU time.
        Assert.Equal(6.25, CounterMath.ProcessCpuPercent(7_600_000_000L, 2 * 10_000_000L, 2, 0, 16), 6);
        // Idle: zero either way.
        Assert.Equal(0, CounterMath.ProcessCpuPercent(0, 0, 2, 3800, 16));
    }
}
