using System.Text;
using SwissOlly.Core.Models;

namespace SwissOlly.App;

internal static class StartupTrace
{
    private static readonly object Gate = new();

    private static string LogPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwissOlly",
            "startup-trace.log");

    public static void Reset()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public static void Write(string message)
    {
        try
        {
            // Mirror the trace into Visual Studio's Output window in Debug builds.
            System.Diagnostics.Debug.WriteLine("[SwissOlly] " + message);
            var directory = System.IO.Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            lock (Gate)
            {
                System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public static void WriteException(string stage, Exception ex)
    {
        Write($"{stage} failed: {ex.GetType().Name}: {ex.Message}");
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            Write(ex.StackTrace.Replace(Environment.NewLine, " | "));
        }
    }

    private static string CrashLogPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwissOlly",
            "crash.log");

    // Past this size the log starts over, so a crash loop can't fill the disk.
    private const long CrashLogLimitBytes = 1024 * 1024;

    /// <summary>
    /// Records an exception that is about to end the app in crash.log, which, unlike the startup
    /// trace, survives the next launch, so it can go into a bug report.
    /// </summary>
    public static void WriteCrash(string source, Exception ex)
    {
        WriteException(source, ex);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var version = typeof(StartupTrace).Assembly.GetName().Version;
            var entry =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} SwissOlly {version} on {Environment.OSVersion.VersionString}{Environment.NewLine}" +
                $"{source}: {ex}{Environment.NewLine}{Environment.NewLine}";

            lock (Gate)
            {
                var file = new System.IO.FileInfo(CrashLogPath);
                if (file.Exists && file.Length > CrashLogLimitBytes)
                {
                    file.Delete();
                }

                System.IO.File.AppendAllText(CrashLogPath, entry, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
