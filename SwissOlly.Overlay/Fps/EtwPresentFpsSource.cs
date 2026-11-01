using System.Runtime.InteropServices;
using SwissOlly.Overlay.Rendering;
using static SwissOlly.Overlay.Fps.EtwInterop;

namespace SwissOlly.Overlay.Fps;

/// <summary>
/// Per-process present rate, measured by counting the tracked game's own present calls on the
/// Microsoft-Windows-DXGI and Microsoft-Windows-D3D9 ETW providers. This does not correlate
/// presents with frames confirmed as displayed by the monitor.
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
    private const string SessionName = "SwissOllyFpsSession";
    private const double WindowSeconds = 1.0;
    private const int EmitIntervalMs = 500;
    private const double StallSeconds = 2.0;

    private readonly object _gate = new();
    private readonly Queue<long> _presentTicks = new();

    private ulong _sessionHandle;
    private ulong _traceHandle = InvalidProcessTraceHandle;
    private IntPtr _properties = IntPtr.Zero;
    private Thread? _consumer;
    private System.Threading.Timer? _emitTimer;
    private volatile uint _trackedPid;
    private volatile bool _running;
    private long _lastPresentTick;
    private long _newestPresentTick;
    private long _lastEmittedTick;
    private bool _stallReported;

    // Held in a field so the GC cannot collect the thunk while ETW holds the pointer.
    private EventRecordCallback? _callback;

    public event Action<double>? FpsUpdated;
    public event Action<string>? ErrorOccurred;

    public FpsAccuracy Accuracy => FpsAccuracy.Exact;

    /// <summary>True once a session is up and the consumer thread is running.</summary>
    internal bool IsRunning => _running;

    /// <summary>Cheap permission probe: start and immediately tear down a session.</summary>
    internal static bool IsAvailable(out string failureReason)
    {
        failureReason = string.Empty;
        IntPtr props = AllocProperties();
        try
        {
            uint rc = StartTrace(out ulong handle, SessionName, props);
            if (rc == ErrorAlreadyExists)
            {
                StopStaleSession();
                rc = StartTrace(out handle, SessionName, props);
            }

            if (rc != ErrorSuccess)
            {
                OverlayLog.Write($"ETW probe: StartTrace rc={rc}"
                    + (rc == ErrorAccessDenied ? " (access denied — not in Performance Log Users)" : ""));
                failureReason = rc == ErrorAccessDenied
                    ? "etw-access-denied"
                    : $"etw-probe-failed-{rc}";
                return false;
            }

            ControlTrace(handle, SessionName, props, EventTraceControlStop);
            return true;
        }
        catch (DllNotFoundException)
        {
            failureReason = "etw-unavailable";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            failureReason = "etw-unavailable";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    public void StartTracking(uint pid)
    {
        StopTracking();

        _trackedPid = pid;
        _properties = AllocProperties();

        uint rc = StartTrace(out _sessionHandle, SessionName, _properties);
        if (rc == ErrorAlreadyExists)
        {
            // A previous SwissOlly process crashed without stopping its session. ETW sessions
            // outlive the process that created them, so clean up and retry once.
            OverlayLog.Write("ETW: stale session found — stopping and retrying");
            StopStaleSession();
            rc = StartTrace(out _sessionHandle, SessionName, _properties);
        }

        if (rc != ErrorSuccess)
        {
            Cleanup();
            string why = rc == ErrorAccessDenied
                ? "etw-access-denied"
                : $"etw-start-failed-{rc}";
            OverlayLog.Write($"ETW: StartTrace failed rc={rc}");
            ErrorOccurred?.Invoke(why);
            return;
        }

        // MatchAnyKeyword = 0 matches every event the provider emits. DXGI and D3D9 only emit a
        // couple of events per present, so the volume is a few hundred per second at most; the
        // callback discards everything that is not a present from the tracked PID.
        uint e1 = EnableTraceEx2(_sessionHandle, ref DxgiProviderGuid, EventControlCodeEnableProvider,
            TraceLevelInformation, 0, 0, 0, IntPtr.Zero);
        uint e2 = EnableTraceEx2(_sessionHandle, ref D3d9ProviderGuid, EventControlCodeEnableProvider,
            TraceLevelInformation, 0, 0, 0, IntPtr.Zero);
        OverlayLog.Write($"ETW: EnableTraceEx2 dxgi={e1} d3d9={e2}");

        if (e1 != ErrorSuccess && e2 != ErrorSuccess)
        {
            Cleanup();
            ErrorOccurred?.Invoke($"etw-enable-failed-{e1}");
            return;
        }

        _callback = OnEvent;
        var logfile = new EVENT_TRACE_LOGFILE
        {
            LoggerName = Marshal.StringToHGlobalUni(SessionName),
            ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };

        _traceHandle = OpenTrace(ref logfile);
        Marshal.FreeHGlobal(logfile.LoggerName);

        if (_traceHandle == InvalidProcessTraceHandle)
        {
            int err = Marshal.GetLastWin32Error();
            OverlayLog.Write($"ETW: OpenTrace failed err={err}");
            Cleanup();
            ErrorOccurred?.Invoke($"etw-open-failed-{err}");
            return;
        }

        lock (_gate)
        {
            _presentTicks.Clear();
            _lastPresentTick = System.Diagnostics.Stopwatch.GetTimestamp();
            _lastEmittedTick = 0;
            _stallReported = false;
        }

        _running = true;
        _consumer = new Thread(ConsumeLoop) { IsBackground = true, Name = "SwissOllyEtwFps" };
        _consumer.Start();
        _emitTimer = new System.Threading.Timer(Emit, null, EmitIntervalMs, EmitIntervalMs);

        OverlayLog.Write($"ETW: tracking pid={pid} — exact present-based FPS active");
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
            ControlTrace(_sessionHandle, SessionName, _properties, EventTraceControlStop);

        if (_traceHandle != InvalidProcessTraceHandle)
        {
            uint rc = CloseTrace(_traceHandle);
            if (rc != ErrorSuccess && rc != ErrorCtxClosePending)
                OverlayLog.Write($"ETW: CloseTrace rc={rc}");
        }

        // If ProcessTrace has not returned, ETW may still invoke the callback. Dropping the
        // delegate root here would let the GC collect the thunk and the next event would call
        // into freed memory - an access violation on the ETW consumer thread, unrecoverable.
        var consumerExited = _consumer?.Join(5000) ?? true;
        if (!consumerExited)
        {
            OverlayLog.Write("ETW: consumer thread did not exit; keeping callback rooted");
        }

        _consumer = null;
        Cleanup();

        if (consumerExited)
        {
            _callback = null;
        }

        lock (_gate)
            _presentTicks.Clear();
    }

    private void ConsumeLoop()
    {
        try
        {
            // Blocks until CloseTrace is called from StopTracking.
            var handles = new[] { _traceHandle };
            uint rc = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            if (rc != ErrorSuccess && _running)
                OverlayLog.Write($"ETW: ProcessTrace returned rc={rc}");
        }
        catch (Exception ex)
        {
            OverlayLog.Write($"ETW: ProcessTrace threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnEvent(ref EVENT_RECORD record)
    {
        uint tracked = _trackedPid;
        if (tracked == 0 || record.EventHeader.ProcessId != tracked) return;

        int id = record.EventHeader.EventDescriptor.Id;
        bool isPresent =
            (record.EventHeader.ProviderId == DxgiProviderGuid
                && (id == EventIdDxgiPresentStart || id == EventIdDxgiPresentMpoStart))
            || (record.EventHeader.ProviderId == D3d9ProviderGuid && id == EventIdD3d9PresentStart);

        if (!isPresent) return;

        // ClientContext = 1 on the session, so TimeStamp is a raw QPC value on the same
        // timebase as Stopwatch.GetTimestamp().
        long ts = record.EventHeader.TimeStamp;
        lock (_gate)
        {
            _presentTicks.Enqueue(ts);
            // Tracked explicitly: Queue<T>.Last() is Enumerable.Last over a non-indexable
            // collection, so it walked all ~300 entries (twice) on every emit.
            _newestPresentTick = ts;
            _lastPresentTick = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    private void Emit(object? state)
    {
        if (!_running) return;

        double fps;
        var raiseStall = false;
        try
        {
        lock (_gate)
        {
            long freq = System.Diagnostics.Stopwatch.Frequency;

            if (System.Diagnostics.Stopwatch.GetTimestamp() - _lastPresentTick > StallSeconds * freq)
            {
                _presentTicks.Clear();
                if (!_stallReported)
                {
                    // Game is alive but not presenting - minimised, loading, or fully occluded.
                    // Raised outside the lock below: subscribers run arbitrary code (the
                    // controller re-renders under its own lock), and any path back into _gate
                    // would deadlock the emit timer against the consumer callback.
                    _stallReported = true;
                    raiseStall = true;
                }
                return;
            }

            _stallReported = false;

            if (_presentTicks.Count < 2) return;

            long cutoff = _newestPresentTick - (long)(WindowSeconds * freq);
            while (_presentTicks.Count > 2 && _presentTicks.Peek() < cutoff)
                _presentTicks.Dequeue();

            if (_presentTicks.Count < 2) return;

            // ETW delivers real-time events in buffer-sized batches roughly once a second, which
            // is slower than this timer. Without this guard the same window is recomputed twice
            // and the HUD emits each reading twice in a row.
            long newest = _newestPresentTick;
            if (newest == _lastEmittedTick) return;
            _lastEmittedTick = newest;

            double span = (newest - _presentTicks.Peek()) / (double)freq;
            if (span <= 0) return;

            // count-1 intervals span the window between first and last present.
            fps = (_presentTicks.Count - 1) / span;
        }

        }
        finally
        {
            if (raiseStall)
            {
                ErrorOccurred?.Invoke("no-presents");
            }
        }

        FpsUpdated?.Invoke(Math.Round(fps));
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
            ControlTrace(_sessionHandle, SessionName, _properties, EventTraceControlStop);
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

    private static void StopStaleSession()
    {
        IntPtr props = AllocProperties();
        try { ControlTrace(0, SessionName, props, EventTraceControlStop); }
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
