using System.Runtime.InteropServices;
using SwissOlly.Core.Models;
using static SwissOlly.Overlay.Fps.EtwInterop;

namespace SwissOlly.Overlay.Fps;

/// <summary>
/// Per-process present rate, measured by counting the tracked game's own present calls on the
/// Microsoft-Windows-DXGI and Microsoft-Windows-D3D9 ETW providers. This does not correlate
/// presents with frames confirmed as displayed by the monitor.
///
/// A game that presents through Vulkan or OpenGL makes no DXGI or D3D9 calls, so the graphics
/// kernel's own present event is counted too and used when the other two are silent
/// (<see cref="PresentRateMeter"/>). If that provider can't be enabled, the first two still work.
///
/// This is what <see cref="DwmFpsSource"/> could not do: DWM flush timing measures the
/// compositor's cadence, which is the display refresh rate regardless of what the game is
/// presenting. Counting presents attributed to a PID measures the game's submission rate.
///
/// Requires rights to create a real-time ETW session — Administrators or the built-in
/// "Performance Log Users" group. The installer adds the installing user to that group, so a
/// normal install gets exact FPS without the app ever running elevated. When the right is
/// absent (portable use, build-from-source, group membership not yet picked up by the logon
/// token) <see cref="StartTracking"/> fails cleanly and <see cref="AdaptiveFpsSource"/> reports
/// FPS as unavailable rather than substituting the monitor refresh rate.
/// </summary>
internal sealed class EtwPresentFpsSource : IFpsSource
{
    // Each process gets "SwissOllyFpsSession-<pid>". 1.2.0 used the bare prefix for every copy,
    // so a second copy (a Debug build started at sign-in, say) stopped the first one's session
    // when it cleaned up what it took for a stale one.
    private const string SessionPrefix = "SwissOllyFpsSession";
    private const int EmitIntervalMs = 500;

    private readonly object _gate = new();
    private readonly string _sessionName;
    private readonly bool _runtimeEvents;
    private PresentRateMeter? _meter;
    private PresentSource? _reportedSource;

    private ulong _sessionHandle;
    private ulong _traceHandle = InvalidProcessTraceHandle;
    private IntPtr _properties = IntPtr.Zero;
    private Thread? _consumer;
    private System.Threading.Timer? _emitTimer;
    private volatile uint _trackedPid;
    private volatile bool _running;

    // For the one "no presents" log line: what arrived for the tracked process and for others.
    // If others' presents arrive and the game's never do, ETW works and the game's are hidden
    // (anti-cheat) or come from another process; if nothing arrives at all, the session is
    // empty. Battlefield 6 reads "--" for AMD's and NVIDIA's overlays too (forum reports, 2026).
    private const long NoPresentsReportAfterMs = 10_000;
    private long _ownPresents;
    private long _otherPresents;
    private long _trackingStartedTick;
    private bool _noPresentsReported;

    // Held in a field so the GC cannot collect the thunk while ETW holds the pointer.
    private EventRecordCallback? _callback;

    public event Action<double>? FpsUpdated;
    public event Action<string>? ErrorOccurred;

    public FpsAccuracy Accuracy => FpsAccuracy.Exact;

    internal EtwPresentFpsSource()
        : this($"{SessionPrefix}-{Environment.ProcessId}", runtimeEvents: true)
    {
    }

    /// <summary>
    /// For <c>tools/FpsProbe</c>: its own session name, so it can run beside the app (a second
    /// session of the same name would stop the app's), and optionally without the DXGI and D3D9
    /// providers, to see the kernel's count alone for a game that has both.
    /// </summary>
    internal EtwPresentFpsSource(string sessionName, bool runtimeEvents)
    {
        _sessionName = sessionName;
        _runtimeEvents = runtimeEvents;
    }

    /// <summary>True once a session is up and the consumer thread is running.</summary>
    internal bool IsRunning => _running;

    /// <summary>
    /// Stops sessions left by SwissOlly processes that have gone (a crash leaves its session
    /// running until reboot, and Windows allows 64 in all). Only "SwissOllyFpsSession-&lt;pid&gt;"
    /// names whose process isn't a running SwissOlly; 1.2.0's unnumbered name is left alone,
    /// since a running 1.2.0 copy may own it. Best effort: listing sessions needs the same right
    /// as starting one.
    /// </summary>
    internal static void StopOrphanedSessions()
    {
        var buffers = new IntPtr[MaxTraceSessions];
        try
        {
            for (int i = 0; i < buffers.Length; i++)
                buffers[i] = AllocProperties();

            uint rc = QueryAllTraces(buffers, (uint)buffers.Length, out uint count);
            if (rc != ErrorSuccess && rc != ErrorMoreData)
            {
                FpsLog.Write($"ETW: QueryAllTraces rc={rc}; orphaned sessions not checked");
                return;
            }

            string ownPrefix = SessionPrefix + "-";
            for (int i = 0; i < Math.Min(count, (uint)buffers.Length); i++)
            {
                var props = Marshal.PtrToStructure<EVENT_TRACE_PROPERTIES>(buffers[i]);
                string? name = Marshal.PtrToStringUni(buffers[i] + (int)props.LoggerNameOffset);
                if (name is null
                    || !name.StartsWith(ownPrefix, StringComparison.Ordinal)
                    || !int.TryParse(name.AsSpan(ownPrefix.Length), out int ownerPid)
                    || ownerPid == Environment.ProcessId
                    || IsRunningSwissOlly(ownerPid))
                {
                    continue;
                }

                StopStaleSession(name);
                FpsLog.Write($"ETW: stopped orphaned session {name}");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            FpsLog.Write($"ETW: can't list sessions ({ex.GetType().Name})");
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static bool IsRunningSwissOlly(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName.StartsWith("SwissOlly", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch
        {
            return true; // can't tell: leave its session alone
        }
    }

    public void StartTracking(uint pid)
    {
        StopTracking();

        _trackedPid = pid;
        _properties = AllocProperties();

        uint rc;
        try
        {
            rc = StartTrace(out _sessionHandle, _sessionName, _properties);
            if (rc == ErrorAlreadyExists)
            {
                // Ours from an earlier start that didn't stop (or a crashed process with the
                // same ID). ETW sessions outlive their process, so stop it and retry once.
                FpsLog.Write($"ETW: session {_sessionName} already exists; stopping it and retrying");
                StopStaleSession(_sessionName);
                rc = StartTrace(out _sessionHandle, _sessionName, _properties);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            FpsLog.Write($"ETW: StartTrace unavailable ({ex.GetType().Name})");
            Cleanup();
            ErrorOccurred?.Invoke("etw-unavailable");
            return;
        }

        if (rc != ErrorSuccess)
        {
            Cleanup();
            string why = rc == ErrorAccessDenied
                ? "etw-access-denied"
                : $"etw-start-failed-{rc}";
            FpsLog.Write($"ETW: StartTrace {_sessionName} failed rc={rc}");
            ErrorOccurred?.Invoke(why);
            return;
        }

        // MatchAnyKeyword = 0 matches every event the provider emits. DXGI and D3D9 only emit a
        // couple of events per present, so the volume is a few hundred per second at most; the
        // callback discards everything that is not a present from the tracked PID.
        const uint NotEnabled = uint.MaxValue;
        uint e1 = NotEnabled;
        uint e2 = NotEnabled;
        if (_runtimeEvents)
        {
            e1 = EnableTraceEx2(_sessionHandle, ref DxgiProviderGuid, EventControlCodeEnableProvider,
                TraceLevelInformation, 0, 0, 0, IntPtr.Zero);
            e2 = EnableTraceEx2(_sessionHandle, ref D3d9ProviderGuid, EventControlCodeEnableProvider,
                TraceLevelInformation, 0, 0, 0, IntPtr.Zero);
        }

        // The kernel logs far more than presents, so it is enabled for that one event ID only.
        uint e3 = EnableTraceForEventIds(_sessionHandle, ref DxgKernelProviderGuid, DxgKernelBaseKeyword,
            [(ushort)EventIdDxgKernelPresentInfo]);
        FpsLog.Write($"ETW: EnableTraceEx2 dxgi={e1} d3d9={e2} kernel={e3}"
            + (_runtimeEvents ? string.Empty : " (dxgi and d3d9 not asked for)"));

        if (e1 != ErrorSuccess && e2 != ErrorSuccess && e3 != ErrorSuccess)
        {
            Cleanup();
            ErrorOccurred?.Invoke($"etw-enable-failed-{(_runtimeEvents ? e1 : e3)}");
            return;
        }

        _callback = OnEvent;
        var logfile = new EVENT_TRACE_LOGFILE
        {
            LoggerName = Marshal.StringToHGlobalUni(_sessionName),
            ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };

        _traceHandle = OpenTrace(ref logfile);
        Marshal.FreeHGlobal(logfile.LoggerName);

        if (_traceHandle == InvalidProcessTraceHandle)
        {
            int err = Marshal.GetLastWin32Error();
            FpsLog.Write($"ETW: OpenTrace failed err={err}");
            Cleanup();
            ErrorOccurred?.Invoke($"etw-open-failed-{err}");
            return;
        }

        lock (_gate)
        {
            _meter = new PresentRateMeter(
                System.Diagnostics.Stopwatch.Frequency, System.Diagnostics.Stopwatch.GetTimestamp());
            _reportedSource = null;
        }

        Interlocked.Exchange(ref _ownPresents, 0);
        Interlocked.Exchange(ref _otherPresents, 0);
        _trackingStartedTick = Environment.TickCount64;
        _noPresentsReported = false;
        _running = true;
        _consumer = new Thread(ConsumeLoop) { IsBackground = true, Name = "SwissOllyEtwFps" };
        _consumer.Start();
        _emitTimer = new System.Threading.Timer(Emit, null, EmitIntervalMs, EmitIntervalMs);

        FpsLog.Write($"ETW: tracking pid={pid} in {_sessionName}");
    }

    public void StopTracking()
    {
        if (!_running && _sessionHandle == 0) return;

        _running = false;
        _trackedPid = 0;

        _emitTimer?.Dispose();
        _emitTimer = null;

        // Stop the session first so real-time delivery ends, then close the consumer handle —
        // that is what makes the blocked ProcessTrace call return.
        if (_sessionHandle != 0 && _properties != IntPtr.Zero)
            ControlTrace(_sessionHandle, _sessionName, _properties, EventTraceControlStop);

        if (_traceHandle != InvalidProcessTraceHandle)
        {
            uint rc = CloseTrace(_traceHandle);
            if (rc != ErrorSuccess && rc != ErrorCtxClosePending)
                FpsLog.Write($"ETW: CloseTrace rc={rc}");
        }

        // If ProcessTrace has not returned, ETW may still invoke the callback. Dropping the
        // delegate root here would let the GC collect the thunk and the next event would call
        // into freed memory - an access violation on the ETW consumer thread, unrecoverable.
        var consumerExited = _consumer?.Join(5000) ?? true;
        if (!consumerExited)
        {
            FpsLog.Write("ETW: consumer thread did not exit; keeping callback rooted");
        }

        _consumer = null;
        Cleanup();

        if (consumerExited)
        {
            _callback = null;
        }

        lock (_gate)
            _meter = null;
    }

    private void ConsumeLoop()
    {
        try
        {
            // Blocks until CloseTrace is called from StopTracking.
            var handles = new[] { _traceHandle };
            uint rc = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            if (rc != ErrorSuccess && _running)
                FpsLog.Write($"ETW: ProcessTrace returned rc={rc}");
        }
        catch (Exception ex)
        {
            FpsLog.Write($"ETW: ProcessTrace threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnEvent(ref EVENT_RECORD record)
    {
        uint tracked = _trackedPid;
        if (tracked == 0) return;

        int id = record.EventHeader.EventDescriptor.Id;
        Guid provider = record.EventHeader.ProviderId;
        PresentSource source;
        if ((provider == DxgiProviderGuid && (id == EventIdDxgiPresentStart || id == EventIdDxgiPresentMpoStart))
            || (provider == D3d9ProviderGuid && id == EventIdD3d9PresentStart))
        {
            source = PresentSource.Runtime;
        }
        else if (provider == DxgKernelProviderGuid && id == EventIdDxgKernelPresentInfo)
        {
            source = PresentSource.Kernel;
        }
        else
        {
            return;
        }

        if (record.EventHeader.ProcessId != tracked)
        {
            Interlocked.Increment(ref _otherPresents);
            return;
        }

        Interlocked.Increment(ref _ownPresents);

        // ClientContext = 1 on the session, so TimeStamp is a raw QPC value on the same
        // timebase as Stopwatch.GetTimestamp().
        long ts = record.EventHeader.TimeStamp;
        lock (_gate)
            _meter?.Add(source, ts, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void Emit(object? state)
    {
        if (!_running) return;

        // Emit is the only writer of _noPresentsReported, and timer callbacks for one source
        // don't overlap at a 500 ms period, so no lock is needed for it.
        if (!_noPresentsReported
            && Environment.TickCount64 - _trackingStartedTick >= NoPresentsReportAfterMs
            && Interlocked.Read(ref _ownPresents) == 0)
        {
            _noPresentsReported = true;
            long others = Interlocked.Read(ref _otherPresents);
            FpsLog.Write($"ETW: no presents from pid={_trackedPid} in {NoPresentsReportAfterMs / 1000} s; "
                + (others > 0
                    ? $"{others} from other processes, so tracing works and this game's presents are missing (another process presents for it, or they're hidden)"
                    : "none from any process either, so the session receives nothing"));
            FpsLog.Flush();
        }

        PresentReading reading;
        var sourceChanged = false;
        lock (_gate)
        {
            if (_meter is null) return;

            reading = _meter.Read(System.Diagnostics.Stopwatch.GetTimestamp());
            if (reading.Kind == PresentReadingKind.Rate && _reportedSource != reading.Source)
            {
                _reportedSource = reading.Source;
                sourceChanged = true;
            }
        }

        // Raised outside the lock: subscribers run arbitrary code (the controller re-renders under
        // its own lock), and any path back into _gate would deadlock the emit timer against the
        // consumer callback.
        switch (reading.Kind)
        {
            case PresentReadingKind.Stalled:
                // Game is alive but not presenting - minimised, loading, or fully occluded.
                FpsLog.Write($"ETW: pid={_trackedPid} stopped presenting");
                ErrorOccurred?.Invoke("no-presents");
                break;
            case PresentReadingKind.Rate:
                if (sourceChanged)
                {
                    FpsLog.Write(reading.Source == PresentSource.Kernel
                        ? "ETW: no DXGI or D3D9 presents from this process; counting kernel presents"
                        : "ETW: counting DXGI/D3D9 presents");
                }

                FpsUpdated?.Invoke(Math.Round(reading.FramesPerSecond));
                break;
        }
    }

    private void Cleanup()
    {
        // An ETW session is a hard-capped, system-wide kernel resource that outlives this
        // process. Previously the failure paths after a successful StartTrace freed the
        // properties buffer and zeroed the handle WITHOUT stopping the session, leaving
        // SwissOllyFpsSession live with DXGI and D3D9 enabled, filling buffers nobody consumes,
        // for the rest of the OS boot - and unrecoverable, because StopTracking's guard sees
        // _sessionHandle == 0 and returns early.
        if (_sessionHandle != 0 && _properties != IntPtr.Zero)
        {
            ControlTrace(_sessionHandle, _sessionName, _properties, EventTraceControlStop);
        }

        if (_properties != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_properties);
            _properties = IntPtr.Zero;
        }

        _sessionHandle = 0;
        _traceHandle = InvalidProcessTraceHandle;

        // _callback is deliberately NOT nulled here. It is the only GC root for the native
        // thunk ETW still holds; see StopTracking.
    }

    private static void StopStaleSession(string sessionName)
    {
        IntPtr props = AllocProperties();
        try { ControlTrace(0, sessionName, props, EventTraceControlStop); }
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>
    /// EVENT_TRACE_PROPERTIES is a variable-length structure: the fixed header followed by
    /// buffer space that ETW writes the session and log file names into. The offsets must point
    /// inside our own allocation.
    /// </summary>
    private static IntPtr AllocProperties()
    {
        int structSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
        const int NameBytes = 1024;
        int total = structSize + NameBytes * 2;

        IntPtr buffer = Marshal.AllocHGlobal(total);
        for (int i = 0; i < total; i++)
            Marshal.WriteByte(buffer, i, 0);

        var props = new EVENT_TRACE_PROPERTIES
        {
            Wnode =
            {
                BufferSize = (uint)total,
                Flags = WnodeFlagTracedGuid,
                ClientContext = 1,      // 1 = QPC timestamps
            },
            LogFileMode = EventTraceRealTimeMode,
            // Small buffers with a 1s flush timer keep real-time delivery latency near a second.
            // ETW batches events into buffers, so this is the floor on how fresh the reading can be.
            BufferSize = 8,
            MinimumBuffers = 8,
            MaximumBuffers = 16,
            FlushTimer = 1,
            LoggerNameOffset = (uint)structSize,
            LogFileNameOffset = (uint)(structSize + NameBytes),
        };

        Marshal.StructureToPtr(props, buffer, false);
        return buffer;
    }

    public void Dispose() => StopTracking();
}
