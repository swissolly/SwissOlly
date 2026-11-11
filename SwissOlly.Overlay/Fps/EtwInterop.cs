using System.Runtime.InteropServices;

namespace SwissOlly.Overlay.Fps;

// Raw ETW consumer P/Invoke surface.
//
// Deliberately hand-rolled rather than taking a dependency on
// Microsoft.Diagnostics.Tracing.TraceEvent: that package carries native helper binaries
// (KernelTraceControl.dll, msdia140.dll) whose ARM64 availability was the original reason
// the ETW FPS source was dropped in favour of DWM flush timing. These are plain Win32
// entry points in advapi32, so they resolve identically on x64 and ARM64.
//
// Structure layouts below mirror evntrace.h / evntcons.h exactly. Regions we never read
// are declared as `fixed` byte blobs of the correct size — this keeps the structs blittable
// (no marshaller allocation per call) while documenting that the bytes are intentionally opaque.
internal static class EtwInterop
{
    // Microsoft-Windows-DXGI. Presents are reported here for every D3D10/11/12 swapchain.
    internal static Guid DxgiProviderGuid = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");

    // Microsoft-Windows-D3D9. Older titles present through D3D9 and never touch DXGI.
    internal static Guid D3d9ProviderGuid = new("783aca0a-790e-4d7f-8451-aa850511c6b9");

    // DXGI event IDs (Microsoft-Windows-DXGI manifest).
    internal const int EventIdDxgiPresentStart = 42;
    internal const int EventIdDxgiPresentMpoStart = 55;

    // D3D9 event ID for IDirect3DDevice9::Present entry.
    internal const int EventIdD3d9PresentStart = 1;

    // Microsoft-Windows-DxgKernel, the graphics kernel. Every present passes through it whatever
    // the API, so it also covers Vulkan and OpenGL, which the two providers above never see.
    // Present_Info is logged once per present, on the presenting process's thread. Measured
    // 2026-11-09 from a session without elevation: the same count as DXGI's Present_Start for
    // Overwatch (140/s) and VS Code, and as D3D9's for Visual Studio.
    internal static Guid DxgKernelProviderGuid = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    internal const int EventIdDxgKernelPresentInfo = 184;
    internal const ulong DxgKernelBaseKeyword = 0x1;

    private const uint EventFilterTypeEventId = 0x80000200;
    private const uint EnableTraceParametersVersion2 = 2;

    internal const uint EventTraceRealTimeMode = 0x00000100;
    internal const uint WnodeFlagTracedGuid = 0x00020000;
    internal const uint ProcessTraceModeRealTime = 0x00000100;
    internal const uint ProcessTraceModeEventRecord = 0x10000000;

    internal const uint EventControlCodeEnableProvider = 1;
    internal const uint EventTraceControlStop = 1;
    internal const byte TraceLevelInformation = 4;

    internal const uint ErrorSuccess = 0;
    internal const uint ErrorAccessDenied = 5;
    internal const uint ErrorAlreadyExists = 183;
    internal const uint ErrorCtxClosePending = 7007;

    // OpenTrace returns this on failure (INVALID_PROCESSTRACE_HANDLE).
    internal const ulong InvalidProcessTraceHandle = 0xFFFFFFFFFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    // EVENT_TRACE, inlined into EVENT_TRACE_LOGFILE as CurrentEvent. We never read it, but its
    // exact size determines where the callback pointers land, so the layout must be faithful.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EVENT_TRACE
    {
        public fixed byte Header[48];       // EVENT_TRACE_HEADER
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public ETW_BUFFER_CONTEXT BufferContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public fixed byte TimeZone[172];    // TIME_ZONE_INFORMATION
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EventRecordCallback(ref EVENT_RECORD eventRecord);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint BufferCallback(IntPtr logfile);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct EVENT_TRACE_LOGFILE
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
    internal static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    internal static extern uint ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", EntryPoint = "EnableTraceEx2")]
    internal static extern uint EnableTraceEx2(
        ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_FILTER_DESCRIPTOR
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ENABLE_TRACE_PARAMETERS
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public IntPtr EnableFilterDesc;
        public uint FilterDescCount;
    }

    /// <summary>
    /// Enables a provider for the listed event IDs only. The graphics kernel logs thousands of
    /// events a second; this filter runs in the kernel, so only the wanted ones reach the session.
    /// </summary>
    internal static uint EnableTraceForEventIds(ulong sessionHandle, ref Guid providerId, ulong matchAnyKeyword, ReadOnlySpan<ushort> eventIds)
    {
        // EVENT_FILTER_EVENT_ID: BOOLEAN FilterIn, UCHAR Reserved, USHORT Count, USHORT Events[].
        int filterSize = 4 + (2 * eventIds.Length);
        IntPtr filter = Marshal.AllocHGlobal(filterSize);
        IntPtr descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<EVENT_FILTER_DESCRIPTOR>());
        IntPtr parameters = Marshal.AllocHGlobal(Marshal.SizeOf<ENABLE_TRACE_PARAMETERS>());
        try
        {
            Marshal.WriteByte(filter, 0, 1);
            Marshal.WriteByte(filter, 1, 0);
            Marshal.WriteInt16(filter, 2, (short)eventIds.Length);
            for (int i = 0; i < eventIds.Length; i++)
                Marshal.WriteInt16(filter, 4 + (2 * i), unchecked((short)eventIds[i]));

            Marshal.StructureToPtr(
                new EVENT_FILTER_DESCRIPTOR { Ptr = (ulong)filter, Size = (uint)filterSize, Type = EventFilterTypeEventId },
                descriptor, false);
            Marshal.StructureToPtr(
                new ENABLE_TRACE_PARAMETERS { Version = EnableTraceParametersVersion2, EnableFilterDesc = descriptor, FilterDescCount = 1 },
                parameters, false);

            return EnableTraceEx2(sessionHandle, ref providerId, EventControlCodeEnableProvider,
                TraceLevelInformation, matchAnyKeyword, 0, 0, parameters);
        }
        finally
        {
            Marshal.FreeHGlobal(parameters);
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(filter);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE logfile);

    [DllImport("advapi32.dll", EntryPoint = "ProcessTrace")]
    internal static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", EntryPoint = "CloseTrace")]
    internal static extern uint CloseTrace(ulong traceHandle);
}
