using System.Text;
using Vaktr.Core.Models;

namespace Vaktr.App;

internal static class StartupTrace
{
    private static readonly object Gate = new();

    private static string LogPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vaktr",
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
            System.Diagnostics.Debug.WriteLine("[Vaktr] " + message);
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
}
