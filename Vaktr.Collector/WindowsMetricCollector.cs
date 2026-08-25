using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Net.NetworkInformation;
using Vaktr.Collector.Interop;
using Vaktr.Core.Interfaces;
using Vaktr.Core.Models;

namespace Vaktr.Collector;

public sealed class WindowsMetricCollector : IMetricCollector
{
    private const double BytesPerMegabyte = 1024d * 1024d;
    private const double BitsPerMegabit = 1_000_000d;
    private static readonly TimeSpan DriveUsageRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HostActivityRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessActivityRefreshInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TemperatureRefreshInterval = TimeSpan.FromSeconds(20);

    private readonly nint _query;
    private readonly nint _cpuTotalCounter;
    private readonly nint _cpuPerCoreCounter;
    private readonly nint _cpuFrequencyCounter;
    private readonly nint _diskReadCounter;
    private readonly nint _diskWriteCounter;
    private readonly nint _networkReceiveCounter;
    private readonly nint _networkSendCounter;
    private readonly nint _gpuEngineCounter;
    private readonly nint _gpuMemoryCounter;
    private readonly bool _hasAnyPdhCounters;
    private readonly Dictionary<string, NetworkBaseline> _networkBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessCpuBaseline> _processCpuBaselines = [];
    private readonly List<CachedMetricValue> _cachedDriveUsageValues = [];
    private readonly List<CachedMetricValue> _cachedHostActivityValues = [];
    private readonly List<CachedMetricValue> _cachedTemperatureValues = [];
    private readonly List<ProcessActivitySample> _cachedProcessActivity = [];
    private readonly HashSet<int> _activeProcessIds = [];
    private readonly List<int> _staleProcessIds = [];
    // Reused per-counter scratch buffers for TryGetArrayValues — avoids allocating a fresh
    // dictionary up to 7x every scrape. Keyed by counter handle since some callers (disk,
    // network) need two of these arrays alive at once, so a single shared scratch dict isn't safe.
    private readonly Dictionary<nint, Dictionary<string, double>> _arrayValueScratch = [];
    private readonly List<(int Index, double Value)> _coreListScratch = [];
    private LiveBoardDetails? _cachedLiveBoardDetails;
    private readonly TemperatureSensorReader _temperatureReader = new();
    private nint _pdhArrayBuffer;
    private uint _pdhArrayBufferSize;

    private ulong _previousIdleTime;
    private ulong _previousKernelTime;
    private ulong _previousUserTime;
    private bool _cpuFallbackInitialized;
    private DateTimeOffset _lastDriveUsageRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHostActivityRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProcessActivityRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTemperatureRefreshUtc = DateTimeOffset.MinValue;
    private int _processBaselineSamplesCollected;
    private int _cachedProcessCount;
    private long _cachedThreadCount;
    private long _cachedHandleCount;

    public WindowsMetricCollector()
    {
        ThrowIfFailed(PdhNative.PdhOpenQuery(null, nint.Zero, out _query), "Failed to open the PDH query.");

        _cpuTotalCounter = TryAddCounter(@"\Processor(_Total)\% Processor Time");
        _cpuPerCoreCounter = TryAddCounter(@"\Processor(*)\% Processor Time");
        _cpuFrequencyCounter = TryAddCounter(@"\Processor Information(_Total)\Processor Frequency");
        _diskReadCounter = TryAddCounter(@"\LogicalDisk(*)\Disk Read Bytes/sec");
        _diskWriteCounter = TryAddCounter(@"\LogicalDisk(*)\Disk Write Bytes/sec");
        _networkReceiveCounter = TryAddCounter(@"\Network Interface(*)\Bytes Received/sec");
        _networkSendCounter = TryAddCounter(@"\Network Interface(*)\Bytes Sent/sec");
        _gpuEngineCounter = TryAddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _gpuMemoryCounter = TryAddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");

        _hasAnyPdhCounters =
            _cpuTotalCounter != nint.Zero ||
            _cpuPerCoreCounter != nint.Zero ||
            _cpuFrequencyCounter != nint.Zero ||
            _diskReadCounter != nint.Zero ||
            _diskWriteCounter != nint.Zero ||
            _networkReceiveCounter != nint.Zero ||
            _networkSendCounter != nint.Zero ||
            _gpuEngineCounter != nint.Zero ||
            _gpuMemoryCounter != nint.Zero;

        if (_hasAnyPdhCounters)
        {
            _ = PdhNative.PdhCollectQueryData(_query);
        }

    }

    public Task<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var samples = new List<MetricSample>(64);
        var pdhAvailable = TryCollectPdhData();

        if (pdhAvailable)
        {
            AddCpuUsage(samples, timestamp);
            AddCpuFrequency(samples, timestamp);
        }
        else
        {
            AddCpuUsageFallback(samples, timestamp);
        }

        AddMemory(samples, timestamp);
        var liveDetails = AddHostActivity(samples, timestamp);
        AddDriveUsage(samples, timestamp);
        AddTemperatures(samples, timestamp);

        if (pdhAvailable)
        {
            AddDisk(samples, timestamp);
            AddNetwork(samples, timestamp);
            AddGpu(samples, timestamp);
        }
        else
        {
            AddNetworkFallback(samples, timestamp);
        }

        return Task.FromResult(new MetricSnapshot(timestamp, samples, liveDetails));
    }

    public ValueTask DisposeAsync()
    {
        _temperatureReader.Dispose();

        if (_query != nint.Zero)
        {
            _ = PdhNative.PdhCloseQuery(_query);
        }

        if (_pdhArrayBuffer != nint.Zero)
        {
            Marshal.FreeHGlobal(_pdhArrayBuffer);
            _pdhArrayBuffer = nint.Zero;
            _pdhArrayBufferSize = 0;
        }

        return ValueTask.CompletedTask;
    }

    private void AddCpuUsage(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var totalValue = TryGetSingleValue(_cpuTotalCounter);
        if (totalValue.HasValue)
        {
            samples.Add(new MetricSample(
                "cpu-total",
                "CPU Total",
                "usage",
                "Usage",
                MetricCategory.Cpu,
                MetricUnit.Percent,
                Math.Clamp(totalValue.Value, 0d, 100d),
                timestamp));
        }
        else
        {
            AddCpuUsageFallback(samples, timestamp);
        }

        var coreValues = TryGetArrayValues(_cpuPerCoreCounter);
        _coreListScratch.Clear();
        foreach (var (key, val) in coreValues)
        {
            if (int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var coreIndex))
            {
                _coreListScratch.Add((coreIndex, val));
            }
        }
        _coreListScratch.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var (coreIdx, value) in _coreListScratch)
        {
            samples.Add(new MetricSample(
                "cpu-cores",
                "CPU Cores",
                $"core-{coreIdx}",
                $"Core {coreIdx}",
                MetricCategory.Cpu,
                MetricUnit.Percent,
                Math.Clamp(value, 0d, 100d),
                timestamp));
        }
    }

    private void AddCpuUsageFallback(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();

        double usage;
        if (!_cpuFallbackInitialized)
        {
            usage = 0d;
            _cpuFallbackInitialized = true;
        }
        else
        {
            var idleDelta = idle - _previousIdleTime;
            var kernelDelta = kernel - _previousKernelTime;
            var userDelta = user - _previousUserTime;
            var totalDelta = kernelDelta + userDelta;
            usage = totalDelta == 0
                ? 0d
                : Math.Clamp(((totalDelta - idleDelta) / (double)totalDelta) * 100d, 0d, 100d);
        }

        _previousIdleTime = idle;
        _previousKernelTime = kernel;
        _previousUserTime = user;

        samples.Add(new MetricSample(
            "cpu-total",
            "CPU Total",
            "usage",
            "Usage",
            MetricCategory.Cpu,
            MetricUnit.Percent,
            usage,
            timestamp));
    }

    private void AddCpuFrequency(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var value = TryGetSingleValue(_cpuFrequencyCounter);
        if (!value.HasValue || value.Value <= 0d)
        {
            return;
        }

        samples.Add(new MetricSample(
            "cpu-frequency",
            "CPU Clock",
            "clock",
            "Clock",
            MetricCategory.Cpu,
            MetricUnit.Megahertz,
            value.Value,
            timestamp));
    }

    private static void AddMemory(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var memoryStatus = MemoryStatusEx.Create();
        if (!GlobalMemoryStatusEx(ref memoryStatus))
        {
            return;
        }

        var totalGb = memoryStatus.TotalPhys / 1024d / 1024d / 1024d;
        var availableGb = memoryStatus.AvailPhys / 1024d / 1024d / 1024d;
        var usedGb = Math.Max(0d, totalGb - availableGb);

        samples.Add(new MetricSample(
            "memory",
            "Memory",
            "used-gb",
            "Used",
            MetricCategory.Memory,
            MetricUnit.Gigabytes,
            usedGb,
            timestamp));

        samples.Add(new MetricSample(
            "memory",
            "Memory",
            "available-gb",
            "Available",
            MetricCategory.Memory,
            MetricUnit.Gigabytes,
            availableGb,
            timestamp));

        samples.Add(new MetricSample(
            "memory",
            "Memory",
            "total-gb",
            "Total",
            MetricCategory.Memory,
            MetricUnit.Gigabytes,
            totalGb,
            timestamp));
    }

    private void AddDisk(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var reads = TryGetArrayValues(_diskReadCounter);
        var writes = TryGetArrayValues(_diskWriteCounter);

        var instances = reads.Keys.Concat(writes.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            if (!IsLogicalDriveInstance(instance))
            {
                continue;
            }

            var panelKey = $"disk-{Sanitize(instance)}";
            var panelTitle = $"Disk {instance}\\";

            samples.Add(new MetricSample(
                panelKey,
                panelTitle,
                "read",
                "Read",
                MetricCategory.Disk,
                MetricUnit.MegabytesPerSecond,
                reads.GetValueOrDefault(instance) / BytesPerMegabyte,
                timestamp));

            samples.Add(new MetricSample(
                panelKey,
                panelTitle,
                "write",
                "Write",
                MetricCategory.Disk,
                MetricUnit.MegabytesPerSecond,
                writes.GetValueOrDefault(instance) / BytesPerMegabyte,
                timestamp));
        }
    }

    private void AddDriveUsage(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var shouldRefresh = _cachedDriveUsageValues.Count == 0 || timestamp - _lastDriveUsageRefreshUtc >= DriveUsageRefreshInterval;
        if (shouldRefresh)
        {
            _cachedDriveUsageValues.Clear();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed || string.IsNullOrWhiteSpace(drive.Name))
                    {
                        continue;
                    }

                    var totalBytes = drive.TotalSize;
                    if (totalBytes <= 0)
                    {
                        continue;
                    }

                    var totalGb = totalBytes / 1024d / 1024d / 1024d;
                    var usedGb = Math.Max(0d, totalGb - (drive.AvailableFreeSpace / 1024d / 1024d / 1024d));
                    var usedPercent = Math.Clamp((1d - (drive.AvailableFreeSpace / (double)totalBytes)) * 100d, 0d, 100d);
                    var driveLabel = drive.Name.TrimEnd('\\');
                    var panelKey = $"volume-{Sanitize(driveLabel)}";
                    var panelTitle = $"Drive {driveLabel}";

                    _cachedDriveUsageValues.Add(new CachedMetricValue(
                        panelKey,
                        panelTitle,
                        "used-percent",
                        "Used",
                        MetricCategory.Disk,
                        MetricUnit.Percent,
                        usedPercent));

                    _cachedDriveUsageValues.Add(new CachedMetricValue(
                        panelKey,
                        panelTitle,
                        "used-gb",
                        "Used GiB",
                        MetricCategory.Disk,
                        MetricUnit.Gigabytes,
                        usedGb));

                    _cachedDriveUsageValues.Add(new CachedMetricValue(
                        panelKey,
                        panelTitle,
                        "total-gb",
                        "Total GiB",
                        MetricCategory.Disk,
                        MetricUnit.Gigabytes,
                        totalGb));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            _lastDriveUsageRefreshUtc = timestamp;
        }

        foreach (var cachedValue in _cachedDriveUsageValues)
        {
            samples.Add(new MetricSample(
                cachedValue.PanelKey,
                cachedValue.PanelTitle,
                cachedValue.SeriesKey,
                cachedValue.SeriesName,
                cachedValue.Category,
                cachedValue.Unit,
                cachedValue.Value,
                timestamp));
        }
    }

    private LiveBoardDetails? AddHostActivity(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var shouldRefresh = _cachedHostActivityValues.Count == 0 || timestamp - _lastHostActivityRefreshUtc >= HostActivityRefreshInterval;
        // First two process scans happen quickly (6s apart) to establish CPU baselines, then every 60s
        var processInterval = _processBaselineSamplesCollected < 2
            ? TimeSpan.FromSeconds(6)
            : ProcessActivityRefreshInterval;
        var shouldRefreshProcesses = _cachedProcessActivity.Count == 0 || timestamp - _lastProcessActivityRefreshUtc >= processInterval;
        if (shouldRefresh || shouldRefreshProcesses)
        {
            _cachedHostActivityValues.Clear();
            if (shouldRefreshProcesses)
            {
                _cachedProcessActivity.Clear();
            }

            var processCount = 0;
            var threadCount = 0L;
            var handleCount = _cachedHandleCount;
            _activeProcessIds.Clear();
            EnumerateProcesses(
                timestamp,
                shouldRefreshProcesses,
                shouldRefresh || _cachedHandleCount == 0,
                _activeProcessIds,
                ref processCount,
                ref threadCount,
                ref handleCount);

            if (shouldRefreshProcesses)
            {
                _staleProcessIds.Clear();
                foreach (var pid in _processCpuBaselines.Keys)
                {
                    if (!_activeProcessIds.Contains(pid))
                    {
                        _staleProcessIds.Add(pid);
                    }
                }
                foreach (var pid in _staleProcessIds)
                {
                    _processCpuBaselines.Remove(pid);
                }

                _lastProcessActivityRefreshUtc = timestamp;
                _processBaselineSamplesCollected++;
                _cachedLiveBoardDetails = _cachedProcessActivity.Count == 0
                    ? null
                    : new LiveBoardDetails(_cachedProcessActivity.ToArray());
            }

            _cachedProcessCount = processCount;
            _cachedThreadCount = threadCount;
            _cachedHandleCount = handleCount;

            _cachedHostActivityValues.Add(new CachedMetricValue(
                "host-activity",
                "Host Activity",
                "processes",
                "Processes",
                MetricCategory.System,
                MetricUnit.Count,
                _cachedProcessCount));

            _cachedHostActivityValues.Add(new CachedMetricValue(
                "host-activity",
                "Host Activity",
                "threads",
                "Threads",
                MetricCategory.System,
                MetricUnit.Count,
                _cachedThreadCount));

            _cachedHostActivityValues.Add(new CachedMetricValue(
                "host-activity",
                "Host Activity",
                "handles",
                "Handles",
                MetricCategory.System,
                MetricUnit.Count,
                _cachedHandleCount));

            _lastHostActivityRefreshUtc = timestamp;
        }

        foreach (var cachedValue in _cachedHostActivityValues)
        {
            samples.Add(new MetricSample(
                cachedValue.PanelKey,
                cachedValue.PanelTitle,
                cachedValue.SeriesKey,
                cachedValue.SeriesName,
                cachedValue.Category,
                cachedValue.Unit,
                cachedValue.Value,
                timestamp));
        }

        return _cachedLiveBoardDetails;
    }

    private void AddNetwork(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var receives = TryGetArrayValues(_networkReceiveCounter);
        var sends = TryGetArrayValues(_networkSendCounter);
        if (receives.Count == 0 && sends.Count == 0)
        {
            AddNetworkFallback(samples, timestamp);
            return;
        }

        var instances = receives.Keys.Concat(sends.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            if (string.IsNullOrWhiteSpace(instance) ||
                instance.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
                instance.Contains("isatap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var panelKey = $"net-{Sanitize(instance)}";
            var panelTitle = $"Network {Beautify(instance)}";

            samples.Add(new MetricSample(
                panelKey,
                panelTitle,
                "download",
                "Down",
                MetricCategory.Network,
                MetricUnit.MegabitsPerSecond,
                receives.GetValueOrDefault(instance) * 8d / BitsPerMegabit,
                timestamp));

            samples.Add(new MetricSample(
                panelKey,
                panelTitle,
                "upload",
                "Up",
                MetricCategory.Network,
                MetricUnit.MegabitsPerSecond,
                sends.GetValueOrDefault(instance) * 8d / BitsPerMegabit,
                timestamp));
        }
    }

    private void AddGpu(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var usageValues = TryGetArrayValues(_gpuEngineCounter);
        var maxEngineUsage = -1d;
        foreach (var (key, val) in usageValues)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                key.Equals("_Total", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("engtype_copy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var clamped = Math.Max(0d, val);
            if (clamped > maxEngineUsage)
            {
                maxEngineUsage = clamped;
            }
        }

        if (maxEngineUsage >= 0d)
        {
            samples.Add(new MetricSample(
                "gpu-total",
                    "GPU Usage",
                "usage",
                "Usage",
                MetricCategory.Gpu,
                MetricUnit.Percent,
                Math.Clamp(maxEngineUsage, 0d, 100d),
                timestamp));
        }

        var memoryValues = TryGetArrayValues(_gpuMemoryCounter);
        var dedicatedBytes = 0d;
        foreach (var (key, val) in memoryValues)
        {
            if (!string.IsNullOrWhiteSpace(key) && !key.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            {
                dedicatedBytes += Math.Max(0d, val);
            }
        }

        if (dedicatedBytes > 0d)
        {
            samples.Add(new MetricSample(
                "gpu-memory",
                "GPU Memory",
                "dedicated-gb",
                "Dedicated",
                MetricCategory.Gpu,
                MetricUnit.Gigabytes,
                dedicatedBytes / 1024d / 1024d / 1024d,
                timestamp));
        }
    }

    private void AddNetworkFallback(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                    networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var stats = networkInterface.GetIPv4Statistics();
                var panelKey = $"net-{Sanitize(networkInterface.Name)}";
                var panelTitle = $"Network {Beautify(networkInterface.Name)}";
                var receiveMbps = 0d;
                var sendMbps = 0d;

                if (_networkBaselines.TryGetValue(panelKey, out var baseline))
                {
                    var seconds = Math.Max(0.25d, (timestamp - baseline.Timestamp).TotalSeconds);
                    var receiveDelta = Math.Max(0L, stats.BytesReceived - baseline.BytesReceived);
                    var sendDelta = Math.Max(0L, stats.BytesSent - baseline.BytesSent);
                    receiveMbps = (receiveDelta * 8d) / seconds / BitsPerMegabit;
                    sendMbps = (sendDelta * 8d) / seconds / BitsPerMegabit;
                    _networkBaselines[panelKey] = new NetworkBaseline(stats.BytesReceived, stats.BytesSent, timestamp);
                }
                else
                {
                    _networkBaselines.Add(panelKey, new NetworkBaseline(stats.BytesReceived, stats.BytesSent, timestamp));
                }

                samples.Add(new MetricSample(
                    panelKey,
                    panelTitle,
                    "download",
                    "Down",
                    MetricCategory.Network,
                    MetricUnit.MegabitsPerSecond,
                    receiveMbps,
                    timestamp));

                samples.Add(new MetricSample(
                    panelKey,
                    panelTitle,
                    "upload",
                    "Up",
                    MetricCategory.Network,
                    MetricUnit.MegabitsPerSecond,
                    sendMbps,
                    timestamp));
            }
            catch (NetworkInformationException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
    }

    private void AddTemperatures(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var shouldRefresh = _cachedTemperatureValues.Count == 0 || timestamp - _lastTemperatureRefreshUtc >= TemperatureRefreshInterval;
        if (shouldRefresh)
        {
            _cachedTemperatureValues.Clear();
            var reading = _temperatureReader.Read();
            var cpuTemperature = reading.CpuTemperatureCelsius;
            var gpuTemperature = reading.GpuTemperatureCelsius;

            if (cpuTemperature.HasValue)
            {
                _cachedTemperatureValues.Add(new CachedMetricValue(
                    "cpu-temperature",
                    "CPU Temperature",
                    "temperature",
                    "Temperature",
                    MetricCategory.Cpu,
                    MetricUnit.Celsius,
                    cpuTemperature.Value));
            }

            if (gpuTemperature.HasValue)
            {
                _cachedTemperatureValues.Add(new CachedMetricValue(
                    "gpu-temperature",
                    "GPU Temperature",
                    "temperature",
                    "Temperature",
                    MetricCategory.Gpu,
                    MetricUnit.Celsius,
                    gpuTemperature.Value));
            }

            _lastTemperatureRefreshUtc = timestamp;
        }

        foreach (var cachedValue in _cachedTemperatureValues)
        {
            samples.Add(new MetricSample(
                cachedValue.PanelKey,
                cachedValue.PanelTitle,
                cachedValue.SeriesKey,
                cachedValue.SeriesName,
                cachedValue.Category,
                cachedValue.Unit,
                cachedValue.Value,
                timestamp));
        }
    }

    private nint TryAddCounter(string counterPath)
    {
        var status = PdhNative.PdhAddEnglishCounter(_query, counterPath, nint.Zero, out var counter);
        return status == PdhNative.ErrorSuccess ? counter : nint.Zero;
    }

    private bool TryCollectPdhData()
    {
        if (!_hasAnyPdhCounters)
        {
            return false;
        }

        var status = PdhNative.PdhCollectQueryData(_query);
        return status == PdhNative.ErrorSuccess;
    }

    private static double? TryGetSingleValue(nint counter)
    {
        if (counter == nint.Zero)
        {
            return null;
        }

        var status = PdhNative.PdhGetFormattedCounterValue(
            counter,
            PdhNative.PdhFmtDouble | PdhNative.PdhFmtNoCap100,
            out _,
            out var value);

        return status == PdhNative.ErrorSuccess && value.CStatus == PdhNative.ErrorSuccess
            ? value.DoubleValue
            : null;
    }

    private Dictionary<string, double> TryGetArrayValues(nint counter)
    {
        if (!_arrayValueScratch.TryGetValue(counter, out var values))
        {
            values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _arrayValueScratch[counter] = values;
        }
        else
        {
            values.Clear();
        }

        if (counter == nint.Zero)
        {
            return values;
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        var format = PdhNative.PdhFmtDouble | PdhNative.PdhFmtNoCap100;
        var status = PdhNative.PdhGetFormattedCounterArray(counter, format, ref bufferSize, ref itemCount, nint.Zero);
        if (status != PdhNative.PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return values;
        }

        EnsurePdhArrayBuffer(bufferSize);
        status = PdhNative.PdhGetFormattedCounterArray(counter, format, ref bufferSize, ref itemCount, _pdhArrayBuffer);
        if (status != PdhNative.ErrorSuccess)
        {
            return values;
        }

        var itemSize = Marshal.SizeOf<PdhNative.PdhFmtCounterValueItemDouble>();
        for (var index = 0; index < itemCount; index++)
        {
            var itemPointer = _pdhArrayBuffer + (index * itemSize);
            var item = Marshal.PtrToStructure<PdhNative.PdhFmtCounterValueItemDouble>(itemPointer);
            if (item.FmtValue.CStatus != PdhNative.ErrorSuccess)
            {
                continue;
            }

            var instanceName = Marshal.PtrToStringUni(item.SzName);
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                continue;
            }

            values[instanceName] = item.FmtValue.DoubleValue;
        }

        return values;
    }

    private void EnsurePdhArrayBuffer(uint requiredSize)
    {
        if (requiredSize <= _pdhArrayBufferSize && _pdhArrayBuffer != nint.Zero)
        {
            return;
        }

        if (_pdhArrayBuffer != nint.Zero)
        {
            Marshal.FreeHGlobal(_pdhArrayBuffer);
        }

        _pdhArrayBuffer = Marshal.AllocHGlobal((int)requiredSize);
        _pdhArrayBufferSize = requiredSize;
    }

    private static bool IsLogicalDriveInstance(string instance) =>
        !string.IsNullOrWhiteSpace(instance) &&
        !instance.Equals("_Total", StringComparison.OrdinalIgnoreCase) &&
        instance.EndsWith(":", StringComparison.Ordinal);

    private static string Beautify(string instance) =>
        instance.Replace('_', ' ').Replace('#', ' ').Trim();

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static void ThrowIfFailed(uint status, string message)
    {
        if (status != PdhNative.ErrorSuccess)
        {
            throw new InvalidOperationException($"{message} PDH status: 0x{status:X8}.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;

        public static MemoryStatusEx Create()
        {
            return new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    private readonly record struct NetworkBaseline(long BytesReceived, long BytesSent, DateTimeOffset Timestamp);

    private void EnumerateProcesses(
        DateTimeOffset timestamp,
        bool shouldRefreshProcesses,
        bool shouldRefreshHandleCounts,
        HashSet<int> activePids,
        ref int processCount,
        ref long threadCount,
        ref long handleCount)
    {
        var snapshotHandle = ProcessNative.CreateToolhelp32Snapshot(ProcessNative.Th32csSnapProcess, 0);
        if (!ProcessNative.IsValidHandle(snapshotHandle))
        {
            return;
        }

        try
        {
            var entry = new ProcessNative.ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessNative.ProcessEntry32>(),
                szExeFile = string.Empty,
            };

            if (!ProcessNative.Process32First(snapshotHandle, ref entry))
            {
                return;
            }

            do
            {
                var processId = unchecked((int)entry.th32ProcessID);
                if (processId <= 0)
                {
                    ResetProcessEntry(ref entry);
                    continue;
                }

                processCount++;
                var processThreads = unchecked((int)entry.cntThreads);
                threadCount += processThreads;

                var processHandles = 0;
                var cpuPercent = 0d;
                var workingSetGb = 0d;
                var processName = NormalizeProcessName(entry.szExeFile, processId);

                var shouldOpenProcess = shouldRefreshProcesses || shouldRefreshHandleCounts;
                if (shouldOpenProcess)
                {
                    var desiredAccess = shouldRefreshProcesses
                        ? ProcessNative.ProcessQueryLimitedInformation | ProcessNative.ProcessVmRead
                        : ProcessNative.ProcessQueryLimitedInformation;

                    var processHandle = ProcessNative.OpenProcess(
                        desiredAccess,
                        false,
                        processId);

                    if (!ProcessNative.IsValidHandle(processHandle) && shouldRefreshProcesses)
                    {
                        processHandle = ProcessNative.OpenProcess(
                            ProcessNative.ProcessQueryLimitedInformation,
                            false,
                            processId);
                    }

                    if (ProcessNative.IsValidHandle(processHandle))
                    {
                        try
                        {
                            if (shouldRefreshHandleCounts && ProcessNative.GetProcessHandleCount(processHandle, out var handleValue))
                            {
                                processHandles = unchecked((int)handleValue);
                                handleCount += processHandles;
                            }

                            if (shouldRefreshProcesses)
                            {
                                activePids.Add(processId);
                                TryGetProcessCpuUsage(processHandle, processId, timestamp, out cpuPercent);

                                if (ProcessNative.GetProcessMemoryInfo(
                                        processHandle,
                                        out var memoryCounters,
                                        (uint)Marshal.SizeOf<ProcessNative.ProcessMemoryCounters>()))
                                {
                                    workingSetGb = Math.Max(0d, (double)memoryCounters.WorkingSetSize / 1024d / 1024d / 1024d);
                                }
                            }
                        }
                        finally
                        {
                            ProcessNative.CloseHandle(processHandle);
                        }
                    }
                    else if (shouldRefreshProcesses)
                    {
                        activePids.Add(processId);
                    }
                }

                if (shouldRefreshProcesses)
                {
                    _cachedProcessActivity.Add(new ProcessActivitySample(
                        processId,
                        processName,
                        cpuPercent,
                        workingSetGb,
                        processThreads,
                        processHandles));
                }

                ResetProcessEntry(ref entry);
            }
            while (ProcessNative.Process32Next(snapshotHandle, ref entry));
        }
        finally
        {
            ProcessNative.CloseHandle(snapshotHandle);
        }
    }

    private bool TryGetProcessCpuUsage(nint processHandle, int processId, DateTimeOffset timestamp, out double cpuPercent)
    {
        cpuPercent = 0d;
        if (!ProcessNative.GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out var kernelTime,
                out var userTime))
        {
            return false;
        }

        var totalProcessorTime = TimeSpan.FromTicks((long)(kernelTime.ToUInt64() + userTime.ToUInt64()));
        var creationStamp = creationTime.ToUInt64();
        if (_processCpuBaselines.TryGetValue(processId, out var baseline) &&
            baseline.CreationTime == creationStamp)
        {
            var elapsedSeconds = Math.Max(0.25d, (timestamp - baseline.Timestamp).TotalSeconds);
            var cpuSeconds = Math.Max(0d, (totalProcessorTime - baseline.TotalProcessorTime).TotalSeconds);
            cpuPercent = Math.Clamp((cpuSeconds / (elapsedSeconds * Environment.ProcessorCount)) * 100d, 0d, 100d);
        }

        _processCpuBaselines[processId] = new ProcessCpuBaseline(totalProcessorTime, timestamp, creationStamp);
        return true;
    }

    private static string NormalizeProcessName(string executableName, int processId)
    {
        var trimmed = Path.GetFileNameWithoutExtension(executableName)?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? $"PID {processId}" : trimmed;
    }

    private static void ResetProcessEntry(ref ProcessNative.ProcessEntry32 entry)
    {
        entry.dwSize = (uint)Marshal.SizeOf<ProcessNative.ProcessEntry32>();
        entry.szExeFile = string.Empty;
    }

    private readonly record struct ProcessCpuBaseline(TimeSpan TotalProcessorTime, DateTimeOffset Timestamp, ulong CreationTime);

    private readonly record struct CachedMetricValue(
        string PanelKey,
        string PanelTitle,
        string SeriesKey,
        string SeriesName,
        MetricCategory Category,
        MetricUnit Unit,
        double Value);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);
}
