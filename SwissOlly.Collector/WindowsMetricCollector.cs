using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Net.NetworkInformation;
using SwissOlly.Collector.Interop;
using SwissOlly.Core.Interfaces;
using SwissOlly.Core.Models;

namespace SwissOlly.Collector;

public sealed class WindowsMetricCollector : IMetricCollector
{
    private const double BytesPerMegabyte = 1024d * 1024d;
    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;
    private const double BitsPerMegabit = 1_000_000d;
    private const int InitialProcessBufferBytes = 1 << 20;
    private static readonly TimeSpan DriveUsageRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProcessActivityRefreshInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TemperatureRefreshInterval = TimeSpan.FromSeconds(20);

    private readonly nint _query;
    private readonly nint _cpuTotalCounter;
    private readonly nint _cpuPerCoreCounter;
    private readonly nint _cpuFrequencyCounter;
    private readonly nint _cpuPerformanceCounter;
    private readonly nint _diskReadCounter;
    private readonly nint _diskWriteCounter;
    private readonly nint _networkReceiveCounter;
    private readonly nint _networkSendCounter;
    private readonly nint _gpuEngineCounter;
    private readonly nint _gpuMemoryCounter;
    private readonly bool _hasAnyPdhCounters;
    private readonly int _logicalProcessorCount;
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
    private readonly List<(int Group, int Number, double Value)> _coreListScratch = [];
    private readonly Dictionary<string, double> _gpuEngineScratch = new(StringComparer.OrdinalIgnoreCase);
    private LiveBoardDetails? _cachedLiveBoardDetails;
    private readonly TemperatureSensorReader _temperatureReader = new();
    private nint _pdhArrayBuffer;
    private uint _pdhArrayBufferSize;
    private nint _processBuffer;
    private int _processBufferSize;

    private ulong _previousIdleTime;
    private ulong _previousKernelTime;
    private ulong _previousUserTime;
    private bool _cpuFallbackInitialized;
    // Refresh intervals and rates run on the monotonic clock. Wall-clock deltas went negative when
    // the system clock was set back, which froze drive and temperature readings until it caught up
    // and turned the next network rate into a spike.
    private long _lastDriveUsageRefreshTicks;
    private long _lastProcessActivityRefreshTicks;
    private long _lastTemperatureRefreshTicks;
    private int _processBaselineSamplesCollected;
    private double _nominalClockMhz;

    public WindowsMetricCollector()
    {
        ThrowIfFailed(PdhNative.PdhOpenQuery(null, nint.Zero, out _query), "Failed to open the PDH query.");

        // Task Manager reports % Processor Utility, which accounts for boost and power states;
        // % Processor Time reads several points apart from it. Fall back where Utility is missing.
        _cpuTotalCounter = TryAddCounter(@"\Processor Information(_Total)\% Processor Utility");
        if (_cpuTotalCounter == nint.Zero)
        {
            _cpuTotalCounter = TryAddCounter(@"\Processor(_Total)\% Processor Time");
        }

        _cpuPerCoreCounter = TryAddCounter(@"\Processor Information(*)\% Processor Utility");
        if (_cpuPerCoreCounter == nint.Zero)
        {
            _cpuPerCoreCounter = TryAddCounter(@"\Processor(*)\% Processor Time");
        }

        _cpuFrequencyCounter = TryAddCounter(@"\Processor Information(_Total)\Processor Frequency");
        _cpuPerformanceCounter = TryAddCounter(@"\Processor Information(_Total)\% Processor Performance");
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

        // Every logical processor in every group, not this process's affinity or job limit.
        var activeProcessors = (int)ProcessNative.GetActiveProcessorCount(ProcessNative.AllProcessorGroups);
        _logicalProcessorCount = activeProcessors > 0 ? activeProcessors : Environment.ProcessorCount;
    }

    public Task<MetricSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow;
        var nowTicks = Stopwatch.GetTimestamp();
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
        var liveDetails = AddHostActivity(samples, timestamp, nowTicks);
        AddDriveUsage(samples, timestamp, nowTicks);
        AddTemperatures(samples, timestamp, nowTicks);

        if (pdhAvailable)
        {
            AddDisk(samples, timestamp);
            AddNetwork(samples, timestamp, nowTicks);
            AddGpu(samples, timestamp);
        }
        else
        {
            AddNetworkFallback(samples, timestamp, nowTicks);
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

        if (_processBuffer != nint.Zero)
        {
            Marshal.FreeHGlobal(_processBuffer);
            _processBuffer = nint.Zero;
            _processBufferSize = 0;
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
            if (CounterMath.TryParseProcessorInstance(key, out var group, out var number))
            {
                _coreListScratch.Add((group, number, val));
            }
        }
        // Processor Information names cores "group,number"; numbering them in that order gives the
        // same sequence as Task Manager's logical processor graphs, across processor groups too.
        _coreListScratch.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Number.CompareTo(b.Number));

        for (var coreIdx = 0; coreIdx < _coreListScratch.Count; coreIdx++)
        {
            samples.Add(new MetricSample(
                "cpu-cores",
                "CPU Cores",
                $"core-{coreIdx}",
                $"Core {coreIdx}",
                MetricCategory.Cpu,
                MetricUnit.Percent,
                Math.Clamp(_coreListScratch[coreIdx].Value, 0d, 100d),
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

        // Also the yardstick for per-process CPU, which is measured in cycles.
        _nominalClockMhz = value.Value;
        samples.Add(new MetricSample(
            "cpu-frequency",
            "CPU Clock",
            "clock",
            "Clock",
            MetricCategory.Cpu,
            MetricUnit.Megahertz,
            CounterMath.EffectiveClockMhz(value.Value, TryGetSingleValue(_cpuPerformanceCounter)),
            timestamp));
    }

    private static void AddMemory(List<MetricSample> samples, DateTimeOffset timestamp)
    {
        var memoryStatus = MemoryStatusEx.Create();
        if (!GlobalMemoryStatusEx(ref memoryStatus))
        {
            return;
        }

        // Used is total minus available (standby and free count as available), which is the
        // figure behind Task Manager's memory percentage. Its "In use" also leaves out the
        // modified list, usually a few hundred MB.
        var totalGb = memoryStatus.TotalPhys / BytesPerGigabyte;
        var availableGb = memoryStatus.AvailPhys / BytesPerGigabyte;
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

    private void AddDriveUsage(List<MetricSample> samples, DateTimeOffset timestamp, long nowTicks)
    {
        var shouldRefresh = _cachedDriveUsageValues.Count == 0 || Stopwatch.GetElapsedTime(_lastDriveUsageRefreshTicks, nowTicks) >= DriveUsageRefreshInterval;
        if (shouldRefresh)
        {
            _cachedDriveUsageValues.Clear();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    // DriveType first: it's a cheap lookup, where IsReady queries the volume, and
                    // an offline mapped drive or an optical drive can take seconds to answer.
                    if (drive.DriveType != DriveType.Fixed || string.IsNullOrWhiteSpace(drive.Name) || !drive.IsReady)
                    {
                        continue;
                    }

                    var totalBytes = drive.TotalSize;
                    if (totalBytes <= 0)
                    {
                        continue;
                    }

                    var totalGb = totalBytes / BytesPerGigabyte;
                    var usedGb = Math.Max(0d, totalGb - (drive.AvailableFreeSpace / BytesPerGigabyte));
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

            _lastDriveUsageRefreshTicks = nowTicks;
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

    private LiveBoardDetails? AddHostActivity(List<MetricSample> samples, DateTimeOffset timestamp, long nowTicks)
    {
        // The first two process scans happen quickly (6s apart) to establish CPU baselines.
        var processInterval = _processBaselineSamplesCollected < 2
            ? TimeSpan.FromSeconds(6)
            : ProcessActivityRefreshInterval;
        var shouldRefresh = _cachedHostActivityValues.Count == 0 || Stopwatch.GetElapsedTime(_lastProcessActivityRefreshTicks, nowTicks) >= processInterval;
        if (shouldRefresh)
        {
            _cachedProcessActivity.Clear();
            _activeProcessIds.Clear();
            var processCount = 0;
            var threadCount = 0L;
            var handleCount = 0L;
            if (EnumerateProcesses(nowTicks, _activeProcessIds, ref processCount, ref threadCount, ref handleCount))
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

                _processBaselineSamplesCollected++;
                _cachedLiveBoardDetails = _cachedProcessActivity.Count == 0
                    ? null
                    : new LiveBoardDetails(_cachedProcessActivity.ToArray());

                // Counted the way \System\Processes, \System\Threads and Task Manager do: every
                // process including System Idle, and every thread including the idle threads.
                _cachedHostActivityValues.Clear();
                _cachedHostActivityValues.Add(new CachedMetricValue(
                    "host-activity",
                    "Host Activity",
                    "processes",
                    "Processes",
                    MetricCategory.System,
                    MetricUnit.Count,
                    processCount));

                _cachedHostActivityValues.Add(new CachedMetricValue(
                    "host-activity",
                    "Host Activity",
                    "threads",
                    "Threads",
                    MetricCategory.System,
                    MetricUnit.Count,
                    threadCount));

                _cachedHostActivityValues.Add(new CachedMetricValue(
                    "host-activity",
                    "Host Activity",
                    "handles",
                    "Handles",
                    MetricCategory.System,
                    MetricUnit.Count,
                    handleCount));
            }

            _lastProcessActivityRefreshTicks = nowTicks;
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

    private void AddNetwork(List<MetricSample> samples, DateTimeOffset timestamp, long nowTicks)
    {
        var receives = TryGetArrayValues(_networkReceiveCounter);
        var sends = TryGetArrayValues(_networkSendCounter);
        if (receives.Count == 0 && sends.Count == 0)
        {
            AddNetworkFallback(samples, timestamp, nowTicks);
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
        var busiestEngine = CounterMath.BusiestGpuEnginePercent(TryGetArrayValues(_gpuEngineCounter), _gpuEngineScratch);
        if (busiestEngine.HasValue)
        {
            samples.Add(new MetricSample(
                "gpu-total",
                "GPU Usage",
                "usage",
                "Usage",
                MetricCategory.Gpu,
                MetricUnit.Percent,
                busiestEngine.Value,
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
                dedicatedBytes / BytesPerGigabyte,
                timestamp));
        }
    }

    private void AddNetworkFallback(List<MetricSample> samples, DateTimeOffset timestamp, long nowTicks)
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
                    var seconds = Math.Max(0.25d, Stopwatch.GetElapsedTime(baseline.Ticks, nowTicks).TotalSeconds);
                    var receiveDelta = Math.Max(0L, stats.BytesReceived - baseline.BytesReceived);
                    var sendDelta = Math.Max(0L, stats.BytesSent - baseline.BytesSent);
                    receiveMbps = (receiveDelta * 8d) / seconds / BitsPerMegabit;
                    sendMbps = (sendDelta * 8d) / seconds / BitsPerMegabit;
                    _networkBaselines[panelKey] = new NetworkBaseline(stats.BytesReceived, stats.BytesSent, nowTicks);
                }
                else
                {
                    _networkBaselines.Add(panelKey, new NetworkBaseline(stats.BytesReceived, stats.BytesSent, nowTicks));
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

    private void AddTemperatures(List<MetricSample> samples, DateTimeOffset timestamp, long nowTicks)
    {
        var shouldRefresh = _cachedTemperatureValues.Count == 0 || Stopwatch.GetElapsedTime(_lastTemperatureRefreshTicks, nowTicks) >= TemperatureRefreshInterval;
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

            _lastTemperatureRefreshTicks = nowTicks;
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

    private readonly record struct NetworkBaseline(long BytesReceived, long BytesSent, long Ticks);

    /// <summary>
    /// Walks one SYSTEM_PROCESS_INFORMATION snapshot. Unlike opening each process, this sees
    /// services and protected processes too (System, dwm, csrss, antivirus), which a normal user
    /// cannot open and which used to read as 0% CPU, 0 memory and no handles.
    /// </summary>
    private bool EnumerateProcesses(
        long nowTicks,
        HashSet<int> activePids,
        ref int processCount,
        ref long threadCount,
        ref long handleCount)
    {
        if (nint.Size != 8 || !TryQuerySystemProcesses(out var length))
        {
            return false;
        }

        var offset = 0;
        while (offset + ProcessNative.MinimumEntrySize <= length)
        {
            var entry = _processBuffer + offset;
            var nextEntryOffset = Marshal.ReadInt32(entry, ProcessNative.NextEntryOffset);
            var processId = unchecked((int)Marshal.ReadInt64(entry, ProcessNative.UniqueProcessIdOffset));
            var threads = Marshal.ReadInt32(entry, ProcessNative.NumberOfThreadsOffset);
            var handles = Marshal.ReadInt32(entry, ProcessNative.HandleCountOffset);

            processCount++;
            threadCount += Math.Max(0, threads);
            handleCount += Math.Max(0, handles);

            // PID 0 is System Idle: its "CPU" is idle time, which would top every list.
            if (processId > 0)
            {
                activePids.Add(processId);
                var cpuTime = Marshal.ReadInt64(entry, ProcessNative.KernelTimeOffset) + Marshal.ReadInt64(entry, ProcessNative.UserTimeOffset);
                var cycles = Marshal.ReadInt64(entry, ProcessNative.CycleTimeOffset);
                var createTime = Marshal.ReadInt64(entry, ProcessNative.CreateTimeOffset);
                var cpuPercent = 0d;
                string name;
                if (_processCpuBaselines.TryGetValue(processId, out var baseline) && baseline.CreateTime == createTime)
                {
                    var elapsedSeconds = Stopwatch.GetElapsedTime(baseline.Ticks, nowTicks).TotalSeconds;
                    cpuPercent = CounterMath.ProcessCpuPercent(
                        cycles - baseline.Cycles, cpuTime - baseline.CpuTime, elapsedSeconds, _nominalClockMhz, _logicalProcessorCount);
                    name = baseline.Name;
                }
                else
                {
                    name = ReadProcessName(entry, processId);
                }

                _processCpuBaselines[processId] = new ProcessCpuBaseline(cpuTime, cycles, nowTicks, createTime, name);

                // Private working set: the measure in Task Manager's Memory column. The full
                // working set also counts shared DLL pages, which inflates every process.
                var privateWorkingSet = Math.Max(0L, Marshal.ReadInt64(entry, ProcessNative.WorkingSetPrivateSizeOffset));
                _cachedProcessActivity.Add(new ProcessActivitySample(
                    processId,
                    name,
                    cpuPercent,
                    privateWorkingSet / BytesPerGigabyte,
                    Math.Max(0, threads),
                    Math.Max(0, handles)));
            }

            if (nextEntryOffset <= 0)
            {
                break;
            }

            offset += nextEntryOffset;
        }

        return true;
    }

    private bool TryQuerySystemProcesses(out int length)
    {
        length = 0;
        if (_processBuffer == nint.Zero)
        {
            _processBufferSize = InitialProcessBufferBytes;
            _processBuffer = Marshal.AllocHGlobal(_processBufferSize);
        }

        // The process list can grow between the size query and the copy; retry a few times.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var status = ProcessNative.NtQuerySystemInformation(
                ProcessNative.SystemProcessInformation,
                _processBuffer,
                _processBufferSize,
                out var returnLength);

            if (status == ProcessNative.StatusSuccess)
            {
                length = Math.Min(returnLength > 0 ? returnLength : _processBufferSize, _processBufferSize);
                return true;
            }

            if (status != ProcessNative.StatusInfoLengthMismatch)
            {
                return false;
            }

            Marshal.FreeHGlobal(_processBuffer);
            _processBufferSize = Math.Max(_processBufferSize * 2, returnLength + (64 * 1024));
            _processBuffer = Marshal.AllocHGlobal(_processBufferSize);
        }

        return false;
    }

    private static string ReadProcessName(nint entry, int processId)
    {
        var lengthBytes = (ushort)Marshal.ReadInt16(entry, ProcessNative.ImageNameLengthOffset);
        var buffer = Marshal.ReadIntPtr(entry, ProcessNative.ImageNameBufferOffset);
        var imageName = lengthBytes == 0 || buffer == nint.Zero
            ? string.Empty
            : Marshal.PtrToStringUni(buffer, lengthBytes / 2);
        return NormalizeProcessName(imageName, processId);
    }

    private static string NormalizeProcessName(string executableName, int processId)
    {
        var trimmed = Path.GetFileNameWithoutExtension(executableName)?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? $"PID {processId}" : trimmed;
    }

    private readonly record struct ProcessCpuBaseline(long CpuTime, long Cycles, long Ticks, long CreateTime, string Name);

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
