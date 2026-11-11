using System.Diagnostics;
using System.Runtime.InteropServices;
using SwissOlly.Overlay.Fps;
using static SwissOlly.Overlay.Fps.EtwInterop;

// FpsProbe [seconds]
// FpsProbe track <pid> [seconds] [kernel]
//
// The first form listens for present events from every process for a while (10 s by default)
// and prints, per process, how many arrived each second from each source:
//   DXGI    Microsoft-Windows-DXGI Present_Start (42, 55): Direct3D 10, 11 and 12
//   D3D9    Microsoft-Windows-D3D9 Present_Start (1)
//   Kernel  Microsoft-Windows-DxgKernel Present_Info (184): every present, whatever the API
// It also notes which process owned the window in front, once a second, since the overlay
// counts presents for that process only.
//
// The second form runs the overlay's own FPS source (EtwPresentFpsSource) on one process and
// prints each reading it would show. With "kernel" it leaves DXGI and D3D9 off, so the kernel's
// count can be compared with the usual one for a game that has both.
//
// Both use their own session names, so they can run while SwissOlly is tracking a game.
// Needs the same right as the overlay: Administrators or the Performance Log Users group.

if (args.Length >= 2 && args[0] == "track" && uint.TryParse(args[1], out var trackPid))
{
    var trackSeconds = args.Length > 2 && int.TryParse(args[2], out var given) ? Math.Clamp(given, 1, 600) : 10;
    var kernelOnly = args.Contains("kernel");
    using var source = new EtwPresentFpsSource("SwissOllyFpsProbeTrack", runtimeEvents: !kernelOnly);
    var clock = Stopwatch.StartNew();
    source.FpsUpdated += fps => Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:0.0} s  {fps,5:0} fps");
    source.ErrorOccurred += reason => Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:0.0} s  {reason}");
    Console.WriteLine($"Tracking pid {trackPid} ({Probe.ProcessName(trackPid)}) for {trackSeconds} s, {(kernelOnly ? "kernel presents only" : "as the overlay does")}.");
    source.StartTracking(trackPid);
    Thread.Sleep(trackSeconds * 1000);
    source.StopTracking();
    return 0;
}

const string SessionName = "SwissOllyFpsProbe";
var seconds = args.Length > 0 && int.TryParse(args[0], out var parsed) ? Math.Clamp(parsed, 1, 600) : 10;

var counts = new Dictionary<(uint Pid, int Source), int>();
var gate = new object();

void OnEvent(ref EVENT_RECORD record)
{
    var id = record.EventHeader.EventDescriptor.Id;
    var provider = record.EventHeader.ProviderId;
    int source;
    if (provider == DxgiProviderGuid && (id == EventIdDxgiPresentStart || id == EventIdDxgiPresentMpoStart)) source = 0;
    else if (provider == D3d9ProviderGuid && id == EventIdD3d9PresentStart) source = 1;
    else if (provider == DxgKernelProviderGuid && id == EventIdDxgKernelPresentInfo) source = 2;
    else return;

    lock (gate)
    {
        var key = (record.EventHeader.ProcessId, source);
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }
}

var properties = Probe.AllocProperties();
var rc = StartTrace(out var session, SessionName, properties);
if (rc == ErrorAlreadyExists)
{
    ControlTrace(0, SessionName, properties, EventTraceControlStop);
    rc = StartTrace(out session, SessionName, properties);
}

if (rc != ErrorSuccess)
{
    Console.WriteLine(rc == ErrorAccessDenied
        ? "Access denied: this account is not in Performance Log Users (or has not signed out since it was added)."
        : $"StartTrace failed: {rc}");
    return 1;
}

EventRecordCallback callback = OnEvent;
try
{
    var dxgi = EnableTraceEx2(session, ref DxgiProviderGuid, EventControlCodeEnableProvider, TraceLevelInformation, 0, 0, 0, IntPtr.Zero);
    var d3d9 = EnableTraceEx2(session, ref D3d9ProviderGuid, EventControlCodeEnableProvider, TraceLevelInformation, 0, 0, 0, IntPtr.Zero);
    var kernel = EnableTraceForEventIds(session, ref DxgKernelProviderGuid, DxgKernelBaseKeyword, [(ushort)EventIdDxgKernelPresentInfo]);
    Console.WriteLine($"Providers enabled (0 = ok): DXGI={dxgi} D3D9={d3d9} Kernel={kernel}");

    var logfile = new EVENT_TRACE_LOGFILE
    {
        LoggerName = Marshal.StringToHGlobalUni(SessionName),
        ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
        EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback),
    };
    var trace = OpenTrace(ref logfile);
    Marshal.FreeHGlobal(logfile.LoggerName);
    if (trace == InvalidProcessTraceHandle)
    {
        Console.WriteLine($"OpenTrace failed: {Marshal.GetLastWin32Error()}");
        return 1;
    }

    var consumer = new Thread(() => ProcessTrace([trace], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true };
    consumer.Start();

    Console.WriteLine($"Listening for {seconds} s. Switch to the game now and stay in it.");
    var foreground = new Dictionary<uint, int>();
    for (var second = 0; second < seconds; second++)
    {
        Thread.Sleep(1000);
        Probe.GetWindowThreadProcessId(Probe.GetForegroundWindow(), out var pid);
        foreground[pid] = foreground.GetValueOrDefault(pid) + 1;
    }

    ControlTrace(session, SessionName, properties, EventTraceControlStop);
    session = 0;
    CloseTrace(trace);
    consumer.Join(3000);

    Dictionary<(uint Pid, int Source), int> snapshot;
    lock (gate) snapshot = new(counts);

    Console.WriteLine();
    Console.WriteLine($"{"PID",7}  {"Process",-34} {"DXGI/s",8} {"D3D9/s",8} {"Kernel/s",9}  In front");
    foreach (var pid in snapshot.Keys.Select(key => key.Pid).Concat(foreground.Keys).Distinct()
                 .OrderByDescending(pid => foreground.GetValueOrDefault(pid))
                 .ThenByDescending(pid => snapshot.GetValueOrDefault((pid, 2))))
    {
        double Rate(int source) => snapshot.GetValueOrDefault((pid, source)) / (double)seconds;
        var front = foreground.GetValueOrDefault(pid);
        Console.WriteLine($"{pid,7}  {Probe.ProcessName(pid),-34} {Rate(0),8:0.0} {Rate(1),8:0.0} {Rate(2),9:0.0}  {(front > 0 ? $"{front} of {seconds} s" : "")}");
    }

    Console.WriteLine();
    Console.WriteLine("The overlay counts presents for the process in front: DXGI or D3D9 when it has them,");
    Console.WriteLine("the kernel's otherwise (a Vulkan or OpenGL game has only the Kernel column).");
    Console.WriteLine("A game in front with no rates at all presents from another process: look for it above.");
    return 0;
}
finally
{
    if (session != 0)
    {
        ControlTrace(session, SessionName, properties, EventTraceControlStop);
    }

    Marshal.FreeHGlobal(properties);
    GC.KeepAlive(callback);
}

internal static class Probe
{
    internal static IntPtr AllocProperties()
    {
        var structSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        const int NameBytes = 1024;
        var total = structSize + (NameBytes * 2);
        var buffer = Marshal.AllocHGlobal(total);
        for (var i = 0; i < total; i++)
        {
            Marshal.WriteByte(buffer, i, 0);
        }

        var properties = new EVENT_TRACE_PROPERTIES
        {
            Wnode = { BufferSize = (uint)total, Flags = WnodeFlagTracedGuid, ClientContext = 1 },
            LogFileMode = EventTraceRealTimeMode,
            BufferSize = 64,
            MinimumBuffers = 16,
            MaximumBuffers = 64,
            FlushTimer = 1,
            LoggerNameOffset = (uint)structSize,
            LogFileNameOffset = (uint)(structSize + NameBytes),
        };
        Marshal.StructureToPtr(properties, buffer, false);
        return buffer;
    }

    internal static string ProcessName(uint pid)
    {
        if (pid == 0)
        {
            return "(none)";
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "(exited or protected)";
        }
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
