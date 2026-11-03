using System.Runtime.InteropServices;

namespace SwissOlly.Collector.Interop;

/// <summary>
/// The system-wide process snapshot Task Manager reads: one call returns every process's CPU
/// times, private working set, thread and handle counts, including services and protected
/// processes a normal user cannot open. The collector never opens a process handle.
/// </summary>
internal static class ProcessNative
{
    internal const int SystemProcessInformation = 5;
    internal const int StatusSuccess = 0;
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    internal const ushort AllProcessorGroups = 0xFFFF;

    // SYSTEM_PROCESS_INFORMATION field offsets on 64-bit Windows; x64 and ARM64 share the layout,
    // and it has been stable since Vista (System.Diagnostics.Process parses the same structure).
    internal const int NextEntryOffset = 0;
    internal const int NumberOfThreadsOffset = 4;
    internal const int WorkingSetPrivateSizeOffset = 8;
    internal const int CycleTimeOffset = 24;
    internal const int CreateTimeOffset = 32;
    internal const int UserTimeOffset = 40;
    internal const int KernelTimeOffset = 48;
    internal const int ImageNameLengthOffset = 56;
    internal const int ImageNameBufferOffset = 64;
    internal const int UniqueProcessIdOffset = 80;
    internal const int HandleCountOffset = 96;
    internal const int MinimumEntrySize = 104;

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(
        int systemInformationClass,
        nint systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    internal static extern uint GetActiveProcessorCount(ushort groupNumber);
}
