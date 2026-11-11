using System.Runtime.InteropServices;

namespace SwissOlly.Collector.Interop;

internal static class PdhNative
{
    internal const uint ErrorSuccess = 0;
    internal const uint PdhMoreData = 0x800007D2;
    internal const uint PdhFmtDouble = 0x00000200;
    internal const uint PdhFmtNoCap100 = 0x00008000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFmtCounterValueDouble
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFmtCounterValueItemDouble
    {
        public nint SzName;
        public PdhFmtCounterValueDouble FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQuery(
        string? dataSource,
        nint userData,
        out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    internal static extern uint PdhAddEnglishCounter(
        nint query,
        string counterPath,
        nint userData,
        out nint counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(nint query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(
        nint counter,
        uint format,
        out uint counterType,
        out PdhFmtCounterValueDouble value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static extern uint PdhGetFormattedCounterArray(
        nint counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        nint itemBuffer);
}
