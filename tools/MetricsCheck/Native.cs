using System.Runtime.InteropServices;
using System.Text;

namespace MetricsCheck;

/// <summary>
/// A PDH query of the harness's own, separate from the collector's, so the reference readings
/// don't share any code with what is being checked.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;

    private nint _query;

    public PdhQuery()
    {
        var status = PdhOpenQuery(null, 0, out _query);
        if (status != 0)
        {
            throw new InvalidOperationException($"PdhOpenQuery failed: 0x{status:X8}");
        }
    }

    public nint Add(string path)
    {
        var status = PdhAddEnglishCounter(_query, path, 0, out var counter);
        if (status != 0)
        {
            Console.WriteLine($"  (counter unavailable: {path}, 0x{status:X8})");
            return 0;
        }

        return counter;
    }

    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public double? Single(nint counter)
    {
        if (counter == 0)
        {
            return null;
        }

        var status = PdhGetFormattedCounterValue(counter, PdhFmtDouble | PdhFmtNoCap100, out _, out var value);
        return status == 0 && value.CStatus == 0 ? value.DoubleValue : null;
    }

    public Dictionary<string, double> Array(nint counter)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter == 0)
        {
            return values;
        }

        uint size = 0;
        uint count = 0;
        var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, 0);
        if (status != PdhMoreData || size == 0)
        {
            return values;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, ref count, buffer);
            if (status != 0)
            {
                return values;
            }

            var itemSize = Marshal.SizeOf<PdhItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhItem>(buffer + (i * itemSize));
                var name = Marshal.PtrToStringUni(item.Name);
                if (item.Value.CStatus == 0 && !string.IsNullOrEmpty(name))
                {
                    values[name] = item.Value.DoubleValue;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return values;
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            _ = PdhCloseQuery(_query);
            _query = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhItem
    {
        public nint Name;
        public PdhValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint buffer);
}

internal static class Native
{
    public static (ulong TotalPhys, ulong AvailPhys, uint MemoryLoad) MemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.TotalPhys, status.AvailPhys, status.MemoryLoad)
            : (0, 0, 0);
    }

    /// <summary>What Explorer prints for a byte count ("929 GB": 1024-based, three digits).</summary>
    public static string ExplorerSize(long bytes)
    {
        var buffer = new StringBuilder(64);
        _ = StrFormatByteSizeW(bytes, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>
    /// Per-adapter temperature as Task Manager shows it, from the graphics kernel's adapter
    /// performance data (the same D3DKMT query Task Manager makes). Keyed by the LUID in the PDH
    /// instance form, <c>luid_0x00000000_0x0000ADBB</c>.
    /// </summary>
    public static Dictionary<string, double> KernelGpuTemperatures()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var enumAdapters = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref enumAdapters) != 0 || enumAdapters.NumAdapters == 0)
        {
            return result;
        }

        var infoSize = Marshal.SizeOf<AdapterInfo>();
        var adapters = Marshal.AllocHGlobal((int)(infoSize * enumAdapters.NumAdapters));
        try
        {
            enumAdapters.Adapters = adapters;
            if (D3DKMTEnumAdapters2(ref enumAdapters) != 0)
            {
                return result;
            }

            for (var i = 0; i < enumAdapters.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<AdapterInfo>(adapters + (i * infoSize));
                var perf = new AdapterPerfData();
                var perfPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AdapterPerfData>());
                try
                {
                    Marshal.StructureToPtr(perf, perfPointer, false);
                    var query = new QueryAdapterInfo
                    {
                        Adapter = info.Adapter,
                        Type = KmtqaiTypeAdapterPerfData,
                        PrivateData = perfPointer,
                        PrivateDataSize = (uint)Marshal.SizeOf<AdapterPerfData>(),
                    };
                    if (D3DKMTQueryAdapterInfo(ref query) == 0)
                    {
                        perf = Marshal.PtrToStructure<AdapterPerfData>(perfPointer);
                        if (perf.Temperature > 0)
                        {
                            var key = $"luid_0x{(uint)info.LuidHigh:X8}_0x{info.LuidLow:X8}";
                            result[key] = Math.Max(result.GetValueOrDefault(key), perf.Temperature / 10d);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(perfPointer);
                    var close = new CloseAdapter { Adapter = info.Adapter };
                    _ = D3DKMTCloseAdapter(ref close);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(adapters);
        }

        return result;
    }

    private const int KmtqaiTypeAdapterPerfData = 62;

    [StructLayout(LayoutKind.Sequential)]
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Adapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint NumAdapters;
        public nint Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public nint PrivateData;
        public uint PrivateDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOc;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Adapter;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern nint StrFormatByteSizeW(long size, StringBuilder buffer, int bufferSize);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 enumAdapters);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);
}
