using System.Globalization;

namespace SwissOlly.Collector;

/// <summary>
/// Turns raw counter values into the numbers Task Manager shows. Pure functions, so the
/// accuracy rules can be unit tested without PDH.
/// </summary>
public static class CounterMath
{
    /// <summary>
    /// GPU utilisation as Task Manager reports it: each engine's utilisation summed across the
    /// processes using it, then the busiest engine. Instances look like
    /// <c>pid_1234_luid_0x00000000_0x0000D1F2_phys_0_eng_0_engtype_3D</c>, one per process per
    /// engine, so taking the maximum instance alone under-reads any engine two processes share.
    /// </summary>
    /// <returns>The busiest engine's utilisation (0–100), or null when there are no engines.</returns>
    public static double? BusiestGpuEnginePercent(
        IEnumerable<KeyValuePair<string, double>> instances,
        Dictionary<string, double> engineScratch)
    {
        engineScratch.Clear();
        foreach (var (instance, value) in instances)
        {
            if (string.IsNullOrWhiteSpace(instance) || instance.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var engine = GpuEngineKey(instance);
            engineScratch[engine] = engineScratch.GetValueOrDefault(engine) + Math.Max(0d, value);
        }

        if (engineScratch.Count == 0)
        {
            return null;
        }

        var busiest = 0d;
        foreach (var total in engineScratch.Values)
        {
            busiest = Math.Max(busiest, total);
        }

        return Math.Min(busiest, 100d);
    }

    /// <summary>The instance name without its leading <c>pid_N_</c>, which identifies the engine.</summary>
    public static string GpuEngineKey(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
        {
            return instance;
        }

        var separator = instance.IndexOf('_', 4);
        return separator < 0 ? instance : instance[(separator + 1)..];
    }

    /// <summary>
    /// Current clock as Task Manager's "Speed": the nominal frequency scaled by
    /// <c>% Processor Performance</c>. <c>Processor Frequency</c> alone reports the base clock and
    /// never shows boost.
    /// </summary>
    public static double EffectiveClockMhz(double nominalMhz, double? performancePercent) =>
        performancePercent is > 0d ? nominalMhz * performancePercent.Value / 100d : nominalMhz;

    /// <summary>
    /// Parses a processor counter instance: <c>"0,5"</c> (Processor Information: group, number) or
    /// <c>"5"</c> (Processor). Rejects <c>_Total</c> and <c>0,_Total</c>.
    /// </summary>
    public static bool TryParseProcessorInstance(string instance, out int group, out int number)
    {
        group = 0;
        number = 0;
        if (string.IsNullOrWhiteSpace(instance))
        {
            return false;
        }

        var comma = instance.IndexOf(',');
        if (comma < 0)
        {
            return int.TryParse(instance, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        return int.TryParse(instance.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out group)
            && int.TryParse(instance.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    /// A process's share of the whole machine from the CPU cycles it consumed over an interval,
    /// against what every logical processor could run at its nominal clock. Cycle counts are
    /// exact, where CPU time is sampled on the clock tick and misses short bursts, which made
    /// per-process figures add up to a third of the total at light load.
    /// </summary>
    public static double ProcessCpuPercentFromCycles(long cycles, double elapsedSeconds, double nominalClockMhz, int logicalProcessors)
    {
        if (cycles <= 0 || elapsedSeconds <= 0d || nominalClockMhz <= 0d || logicalProcessors <= 0)
        {
            return 0d;
        }

        return Math.Clamp(cycles / (elapsedSeconds * nominalClockMhz * 1_000_000d * logicalProcessors) * 100d, 0d, 100d);
    }

    /// <summary>
    /// A process's share of the whole machine from cycles when they are usable, otherwise from
    /// CPU time. Some virtual machines report no cycle time at all, and with cycles alone every
    /// process read 0% there.
    /// </summary>
    public static double ProcessCpuPercent(long cycles, long cpuTime100ns, double elapsedSeconds, double nominalClockMhz, int logicalProcessors) =>
        nominalClockMhz > 0d && cycles > 0
            ? ProcessCpuPercentFromCycles(cycles, elapsedSeconds, nominalClockMhz, logicalProcessors)
            : ProcessCpuPercent(cpuTime100ns, elapsedSeconds, logicalProcessors);

    /// <summary>
    /// A process's share of the whole machine (Task Manager semantics), from CPU time consumed
    /// over an interval. <paramref name="cpuTime100ns"/> is kernel plus user time in 100 ns units.
    /// The fallback when the nominal clock is unknown.
    /// </summary>
    public static double ProcessCpuPercent(long cpuTime100ns, double elapsedSeconds, int logicalProcessors)
    {
        if (cpuTime100ns <= 0 || elapsedSeconds <= 0d || logicalProcessors <= 0)
        {
            return 0d;
        }

        var cpuSeconds = cpuTime100ns / 10_000_000d;
        return Math.Clamp(cpuSeconds / (elapsedSeconds * logicalProcessors) * 100d, 0d, 100d);
    }
}
