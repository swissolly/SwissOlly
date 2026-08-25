namespace Vaktr.Overlay.Rendering;

internal static class OverlayLog
{
    private static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "vaktr-overlay.log");

    internal static void Write(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}";
        try { System.IO.File.AppendAllText(Path, line); } catch { }
        System.Diagnostics.Trace.WriteLine($"[VaktrOverlay] {msg}");
    }
}
