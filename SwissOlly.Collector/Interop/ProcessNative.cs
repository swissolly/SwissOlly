using System.Runtime.InteropServices;

namespace SwissOlly.Collector.Interop;

internal static class ProcessNative
{
    internal const uint Th32csSnapProcess = 0x00000002;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessVmRead = 0x0010;
    internal static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(nint snapshot, ref ProcessEntry32 processEntry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(nint snapshot, ref ProcessEntry32 processEntry);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessHandleCount(nint handle, out uint handleCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        nint handle,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMemoryInfo(
        nint handle,
        out ProcessMemoryCounters counters,
        uint size);

    internal static bool IsValidHandle(nint handle) =>
        handle != 0 && handle != InvalidHandleValue;
}
