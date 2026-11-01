using System.Management;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace SwissOlly.Collector;

internal sealed class TemperatureSensorReader : IDisposable
{
    private Computer? _hardwareMonitor;
    private readonly UpdateVisitor _updateVisitor = new();
    private static bool _wmiFailed;
    private bool _initialized;
    private bool _initFailed;

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            _hardwareMonitor = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
            };
            _hardwareMonitor.Open();
        }
        catch
        {
            _hardwareMonitor = null;
            _initFailed = true;
        }
    }

    public TemperatureReading Read()
    {
        EnsureInitialized();

        var cpuTemperatures = new List<double>();
        var gpuTemperatures = new List<double>();
        var discoveredSensors = new List<string>();

        if (_hardwareMonitor is not null && !_initFailed)
        {
            try
            {
                _hardwareMonitor.Accept(_updateVisitor);
                foreach (var hardware in _hardwareMonitor.Hardware)
                {
                    CollectTemperatures(hardware, cpuTemperatures, gpuTemperatures, discoveredSensors);
                }
            }
            catch
            {
                // Sensor access can fail on some systems when the current process is not elevated.
            }
        }

        double? cpuTemperature = cpuTemperatures.Count > 0 ? cpuTemperatures.Max() : null;
        if (!cpuTemperature.HasValue && !_wmiFailed &&
            TryGetThermalZoneTemperatureCelsius(out var thermalZoneTemperatureCelsius))
        {
            cpuTemperature = thermalZoneTemperatureCelsius;
            discoveredSensors.Add("WMI: MSAcpi_ThermalZoneTemperature");
        }

        double? gpuTemperature = gpuTemperatures.Count > 0 ? gpuTemperatures.Max() : null;
        return new TemperatureReading(cpuTemperature, gpuTemperature, discoveredSensors.ToArray());
    }

    public void Dispose()
    {
        var monitor = _hardwareMonitor;
        _hardwareMonitor = null;
        if (monitor is null)
        {
            return;
        }

        // Close on a background thread — LibreHardwareMonitor.Close() can be slow
        Task.Run(() =>
        {
            try
            {
                monitor.Close();
            }
            catch
            {
            }
        });
    }

    private static void CollectTemperatures(
        IHardware hardware,
        ICollection<double> cpuTemperatures,
        ICollection<double> gpuTemperatures,
        ICollection<string> discoveredSensors)
    {
        foreach (var subHardware in hardware.SubHardware)
        {
            CollectTemperatures(subHardware, cpuTemperatures, gpuTemperatures, discoveredSensors);
        }

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
            {
                continue;
            }

            var value = sensor.Value.Value;
            if (value <= 0d || value >= 150d)
            {
                continue;
            }

            discoveredSensors.Add($"{hardware.HardwareType}: {hardware.Name} // {sensor.Name} = {value:0.#} C");
            ResolveTemperatureBucket(hardware, sensor, cpuTemperatures, gpuTemperatures)?.Add(value);
        }
    }

    private static ICollection<double>? ResolveTemperatureBucket(
        IHardware hardware,
        ISensor sensor,
        ICollection<double> cpuTemperatures,
        ICollection<double> gpuTemperatures)
    {
        if (hardware.HardwareType == HardwareType.Cpu)
        {
            return cpuTemperatures;
        }

        if (hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia)
        {
            return gpuTemperatures;
        }

        var sensorIdentity = $"{hardware.Name} {sensor.Name}".ToLowerInvariant();
        if (sensorIdentity.Contains("gpu", StringComparison.Ordinal) ||
            sensorIdentity.Contains("graphics", StringComparison.Ordinal) ||
            sensorIdentity.Contains("junction", StringComparison.Ordinal) ||
            sensorIdentity.Contains("hotspot", StringComparison.Ordinal) ||
            sensorIdentity.Contains("hot spot", StringComparison.Ordinal) ||
            sensorIdentity.Contains("vram", StringComparison.Ordinal))
        {
            return gpuTemperatures;
        }

        if (sensorIdentity.Contains("cpu", StringComparison.Ordinal) ||
            sensorIdentity.Contains("package", StringComparison.Ordinal) ||
            sensorIdentity.Contains("processor", StringComparison.Ordinal) ||
            sensorIdentity.Contains("core", StringComparison.Ordinal) ||
            sensorIdentity.Contains("ccd", StringComparison.Ordinal) ||
            sensorIdentity.Contains("tdie", StringComparison.Ordinal) ||
            sensorIdentity.Contains("tctl", StringComparison.Ordinal) ||
            sensorIdentity.Contains("socket", StringComparison.Ordinal) ||
            sensorIdentity.Contains("peci", StringComparison.Ordinal) ||
            sensorIdentity.Contains("diode", StringComparison.Ordinal) ||
            sensorIdentity.Contains("package id", StringComparison.Ordinal) ||
            sensorIdentity.Contains("k10", StringComparison.Ordinal) ||
            sensorIdentity.Contains("ryzen", StringComparison.Ordinal))
        {
            return cpuTemperatures;
        }

        return null;
    }

    private static bool TryGetThermalZoneTemperatureCelsius(out double temperatureCelsius)
    {
        temperatureCelsius = 0d;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();
            foreach (ManagementObject result in results)
            {
                var rawValue = result["CurrentTemperature"];
                if (rawValue is null)
                {
                    continue;
                }

                var converted = (Convert.ToDouble(rawValue, System.Globalization.CultureInfo.InvariantCulture) / 10d) - 273.15d;
                if (converted is > 0d and < 150d)
                {
                    temperatureCelsius = converted;
                    return true;
                }
            }
        }
        catch (ManagementException)
        {
            _wmiFailed = true;
        }
        catch (COMException)
        {
            _wmiFailed = true;
        }
        catch (InvalidOperationException)
        {
            _wmiFailed = true;
        }

        return false;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}

internal sealed record TemperatureReading(
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    string[] Sensors);
