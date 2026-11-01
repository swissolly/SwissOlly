using System.Collections.Concurrent;

namespace SwissOlly.Overlay.Rendering;

/// <summary>
/// Diagnostic log for the overlay.
///
/// Previously this opened, appended to and closed a file on every call, on the caller's thread —
/// including from <c>ApplyPendingBitmap</c> on every <c>UpdateLayeredWindow</c> and from
/// <c>PushRender</c> on every FPS emit. That is two to three blocking file open/write/close
/// cycles per second on the overlay message-loop thread *during gameplay*, which is precisely
/// the thread that must never stall. It was also written from four threads at once, so sharing
/// violations were swallowed and lines were silently lost, and it grew without bound.
///
/// Now: off unless explicitly enabled, queued, and drained by a single background writer.
/// </summary>
internal static class OverlayLog
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "swissolly-overlay.log");

    private static readonly BlockingCollection<string> Queue = new(boundedCapacity: 512);
    private static readonly Lazy<bool> Started = new(StartWriter);

    /// <summary>
    /// Enabled by setting <c>SWISSOLLY_OVERLAY_LOG=1</c>. Off by default so the shipping overlay does
    /// no disk I/O at all; the cost of a disabled call is one boolean test.
    /// </summary>
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SWISSOLLY_OVERLAY_LOG") == "1";

    internal static void Write(string msg)
    {
        System.Diagnostics.Trace.WriteLine($"[SwissOllyOverlay] {msg}");

        if (!Enabled || !Started.Value)
        {
            return;
        }

        // Never block a caller. If the writer is behind, drop the line rather than stall the
        // overlay thread mid-frame.
        Queue.TryAdd($"{DateTime.Now:HH:mm:ss.fff}  {msg}");
    }

    private static bool StartWriter()
    {
        try
        {
            var thread = new Thread(DrainLoop)
            {
                IsBackground = true,
                Name = "SwissOllyOverlayLog",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DrainLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                // Simple size cap: the old log grew to tens of MB across a gaming session.
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Delete(Path);
                }

                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never take the overlay down.
            }
        }
    }
}
