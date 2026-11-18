using SwissOlly.Core.Models;
using SwissOlly.Overlay.Rendering;

namespace SwissOlly.Overlay.Fps;

/// <summary>
/// The FPS path's own short log, always kept. The last <see cref="MaxLines"/> lines live in
/// memory; they're written to <c>%LocalAppData%\SwissOlly\fps.log</c> only when FPS fails or
/// recovers, so a bug report has the error codes without anyone turning a log on first.
/// In 1.2.0 the only record was the overlay log, off unless an environment variable was set.
///
/// Lines also go to <see cref="OverlayLog"/>. Writing is a lock and a queue; the file write runs
/// on the thread pool, never on the caller's thread.
/// </summary>
internal static class FpsLog
{
    private const int MaxLines = 200;

    private static readonly object Gate = new();
    private static readonly Queue<string> Lines = new();
    private static int _flushQueued;

    internal static string FilePath => Path.Combine(SwissOllyConfig.SettingsDirectory, "fps.log");

    internal static void Write(string message)
    {
        OverlayLog.Write(message);

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            Lines.Enqueue(line);
            while (Lines.Count > MaxLines)
            {
                Lines.Dequeue();
            }
        }
    }

    /// <summary>Replaces fps.log with the recent lines, off the caller's thread.</summary>
    internal static void Flush()
    {
        if (Interlocked.Exchange(ref _flushQueued, 1) == 1)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(static _ =>
        {
            try
            {
                string[] snapshot;
                lock (Gate)
                {
                    Volatile.Write(ref _flushQueued, 0);
                    snapshot = [.. Lines];
                }

                var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                string[] header =
                [
                    $"SwissOlly {version?.ToString(3) ?? "?"}, Windows {Environment.OSVersion.Version}, process {Environment.ProcessId}",
                    $"Trace right in this sign-in: {(TraceRight.ProcessHasRight() ? "yes" : "no")}",
                    string.Empty,
                ];

                Directory.CreateDirectory(SwissOllyConfig.SettingsDirectory);
                File.WriteAllLines(FilePath, [.. header, .. snapshot]);
            }
            catch
            {
                // Diagnostics must never take the overlay down.
                Volatile.Write(ref _flushQueued, 0);
            }
        });
    }
}
