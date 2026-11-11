using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Hardware;
using SwissOlly.Collector;

namespace MetricsCheck;

/// <summary>
/// GPU usage, dedicated memory and temperature against independent readings: the harness's own
/// GPU Engine query aggregated the way Task Manager does (each engine summed across processes,
/// then the busiest engine), GPU Adapter Memory, the graphics kernel's adapter temperature (what
/// Task Manager shows), LibreHardwareMonitor's individual sensors and, when present, nvidia-smi.
/// </summary>
internal static class GpuCheck
{
    private const double GiB = 1024d * 1024d * 1024d;

    public static async Task<int> RunAsync(Options options)
    {
        var seconds = (int)options.Number("seconds", 60);
        await using var collector = new WindowsMetricCollector();
        using var pdh = new PdhQuery();
        var engineCounter = pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        var memoryCounter = pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        using var smi = NvidiaSmi.TryStart();
        var sensors = new Computer { IsGpuEnabled = true };
        sensors.Open();

        _ = await collector.CollectAsync(CancellationToken.None);
        pdh.Collect();
        Console.WriteLine("Usage %: SO = SwissOlly, TM = busiest engine summed across processes (Task Manager), max inst = busiest single process-engine instance.");
        Console.WriteLine("Memory GiB. Temperatures C: kernel = D3DKMT adapter perf data (Task Manager's GPU temperature).");
        Console.WriteLine();
        Console.WriteLine("    t | SO use  TM use max inst  smi | SO VRAM PDH VRAM smi VRAM | SO temp kernel  smi | LHM sensors");

        var usage = new List<(double So, double Tm, double MaxInstance, double Smi)>();
        var memory = new List<(double So, double Pdh, double Smi)>();
        var temperature = new List<(double So, double Kernel, double Smi, double Core, double Hottest)>();
        // Optional known load from t = 10 s for 30 s (see GpuLoad).
        GpuLoad? load = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            for (var i = 0; i < seconds; i++)
            {
                await timer.WaitForNextTickAsync();
                if (options.Flag("load") && i == 10)
                {
                    Console.WriteLine("  -- D3D11 load on (texture copies, about half the time; 1 GiB of textures)");
                    load = GpuLoad.Start();
                }
                else if (load is not null && i == 40)
                {
                    load.Dispose();
                    Console.WriteLine($"  -- D3D11 load off after {load.Batches} batches{(load.Error is null ? string.Empty : $", error: {load.Error}")}");
                    load = null;
                }

                var snapshot = await collector.CollectAsync(CancellationToken.None);
                pdh.Collect();
                var now = Clock.Seconds;
                var engines = pdh.Array(engineCounter);
                var taskManager = BusiestEngine(engines);
                var maxInstance = engines.Where(e => e.Key != "_Total").Select(e => e.Value).DefaultIfEmpty(0).Max();
                var pdhMemory = pdh.Array(memoryCounter).Where(m => m.Key != "_Total").Sum(m => m.Value) / GiB;
                var kernel = Native.KernelGpuTemperatures();
                var kernelTemperature = kernel.Count == 0 ? double.NaN : kernel.Values.Max();
                var lhm = ReadSensors(sensors);
                var core = lhm.Where(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).DefaultIfEmpty(double.NaN).Max();
                var hottest = lhm.Select(s => s.Value).DefaultIfEmpty(double.NaN).Max();
                var smiReading = smi?.Latest;

                var soUsage = Snapshots.Value(snapshot, "gpu-total", "usage");
                var soMemory = Snapshots.Value(snapshot, "gpu-memory", "dedicated-gb");
                var soTemperature = Snapshots.Value(snapshot, "gpu-temperature", "temperature");
                usage.Add((soUsage, taskManager, maxInstance, smiReading?.Utilization ?? double.NaN));
                memory.Add((soMemory, pdhMemory, (smiReading?.MemoryMiB ?? double.NaN) / 1024d));
                temperature.Add((soTemperature, kernelTemperature, smiReading?.Temperature ?? double.NaN, core, hottest));

                Console.WriteLine(
                    $"{now,5:0.0} | {soUsage,6:0.0} {taskManager,6:0.0} {maxInstance,8:0.0} {smiReading?.Utilization,4:0} | {soMemory,7:0.000} {pdhMemory,8:0.000} {(smiReading?.MemoryMiB ?? double.NaN) / 1024d,8:0.000} | {soTemperature,7:0.0} {kernelTemperature,6:0.0} {smiReading?.Temperature,4:0} | {string.Join(", ", lhm.Select(s => $"{s.Name} {s.Value:0.0}"))}");
            }
        }
        finally
        {
            load?.Dispose();
            sensors.Close();
        }

        Console.WriteLine();
        var usageDiffs = usage.Select(u => u.So - u.Tm).ToList();
        Console.WriteLine($"Usage: SO mean {usage.Average(u => u.So):0.00}%, Task Manager rule {usage.Average(u => u.Tm):0.00}%, busiest single instance {usage.Average(u => u.MaxInstance):0.00}%, nvidia-smi {usage.Select(u => u.Smi).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Average():0.00}%");
        Console.WriteLine($"       SO - TM per sample: mean {usageDiffs.Average():+0.00;-0.00}, mean |d| {usageDiffs.Average(Math.Abs):0.00}, max |d| {usageDiffs.Max(Math.Abs):0.00} points");
        Console.WriteLine($"VRAM : SO mean {memory.Average(m => m.So):0.000} GiB, PDH {memory.Average(m => m.Pdh):0.000} GiB ({Stats.Percent(memory.Average(m => m.So), memory.Average(m => m.Pdh)):+0.00;-0.00}%), nvidia-smi {memory.Select(m => m.Smi).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Average():0.000} GiB");
        Console.WriteLine($"       SO - PDH per sample: max |d| {memory.Max(m => Math.Abs(m.So - m.Pdh)) * 1024:0.0} MiB");
        var temperatureDiffs = temperature.Where(t => !double.IsNaN(t.So) && !double.IsNaN(t.Kernel)).Select(t => t.So - t.Kernel).ToList();
        Console.WriteLine($"Temp : SO mean {temperature.Average(t => t.So):0.0} C, kernel (Task Manager) {temperature.Average(t => t.Kernel):0.0} C, nvidia-smi {temperature.Select(t => t.Smi).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Average():0.0} C, LHM GPU Core {temperature.Average(t => t.Core):0.0} C, hottest LHM sensor {temperature.Average(t => t.Hottest):0.0} C");
        if (temperatureDiffs.Count > 0)
        {
            Console.WriteLine($"       SO - kernel per sample: mean {temperatureDiffs.Average():+0.0;-0.0}, max |d| {temperatureDiffs.Max(Math.Abs):0.0} C (SO refreshes temperature every 20 s)");
        }

        return 0;
    }

    /// <summary>Task Manager's rule, written independently of CounterMath.</summary>
    private static double BusiestEngine(Dictionary<string, double> instances)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in instances)
        {
            var luid = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (luid < 0)
            {
                continue;
            }

            var engine = name[luid..];
            engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, value);
        }

        return engines.Count == 0 ? double.NaN : Math.Min(100, engines.Values.Max());
    }

    private static List<(string Name, double Value)> ReadSensors(Computer computer)
    {
        var result = new List<(string, double)>();
        foreach (var hardware in computer.Hardware)
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value is { } value)
                {
                    result.Add((sensor.Name, value));
                }
            }
        }

        return result;
    }

    private sealed class NvidiaSmi : IDisposable
    {
        private readonly Process _process;

        private NvidiaSmi(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) => Parse(e.Data);
            _process.BeginOutputReadLine();
        }

        public Reading? Latest { get; private set; }

        public static NvidiaSmi? TryStart()
        {
            var path = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(path))
            {
                return null;
            }

            var process = Process.Start(new ProcessStartInfo(path, "--query-gpu=utilization.gpu,memory.used,temperature.gpu --format=csv,noheader,nounits -lms 1000")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            return process is null ? null : new NvidiaSmi(process);
        }

        private void Parse(string? line)
        {
            var parts = line?.Split(',', StringSplitOptions.TrimEntries);
            if (parts is { Length: 3 } &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var utilization) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var memory) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
            {
                Latest = new Reading(utilization, memory, temperature);
            }
        }

        public void Dispose()
        {
            try
            {
                _process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }

        public sealed record Reading(double Utilization, double MemoryMiB, double Temperature);
    }
}
