param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$DurationMs = 1500,
    [int]$IntervalMs = 30,
    [string]$Arguments = "",
    # Narrow the window by $ResizeBy pixels this long after it appears (0 = never), to check that
    # a chart's draw-in clip doesn't outlive a resize. Doesn't activate the window.
    [int]$ResizeAtMs = 0,
    [int]$ResizeBy = 160
)

# Launches $Exe, waits for its WinUI window, then captures it with PrintWindow(hwnd, hdc, 2)
# every $IntervalMs for $DurationMs. Frames are kept in memory during capture and written as
# PNG afterwards so encoding doesn't slow the loop. Never touches the foreground window.

$source = @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class StartupFrames
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            if (sb.ToString() == "WinUIDesktopWin32WindowClass") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Run(string exe, string args, string outDir, int durationMs, int intervalMs, int resizeAtMs, int resizeBy)
    {
        var log = new StringBuilder();
        var clock = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
        var proc = Process.Start(psi);
        log.AppendLine("pid=" + proc.Id);

        IntPtr hwnd = IntPtr.Zero;
        while (hwnd == IntPtr.Zero && clock.ElapsedMilliseconds < 20000)
        {
            hwnd = FindWindow((uint)proc.Id);
            if (hwnd == IntPtr.Zero) Thread.Sleep(1);
        }
        if (hwnd == IntPtr.Zero) { log.AppendLine("no window"); return log.ToString(); }
        var windowAt = clock.ElapsedMilliseconds;
        log.AppendLine("window at " + windowAt + " ms");

        var frames = new List<KeyValuePair<long, Bitmap>>();
        var start = clock.ElapsedMilliseconds;
        long next = start;
        while (clock.ElapsedMilliseconds - start < durationMs)
        {
            RECT r;
            GetWindowRect(hwnd, out r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (resizeAtMs > 0 && clock.ElapsedMilliseconds - start >= resizeAtMs && w > resizeBy)
            {
                // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w - resizeBy, h, 0x0002 | 0x0004 | 0x0010);
                log.AppendLine("resized at " + (clock.ElapsedMilliseconds - windowAt) + " ms");
                resizeAtMs = 0;
                GetWindowRect(hwnd, out r);
                w = r.Right - r.Left; h = r.Bottom - r.Top;
            }
            if (w > 0 && h > 0)
            {
                var t = clock.ElapsedMilliseconds;
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    PrintWindow(hwnd, hdc, 2);
                    g.ReleaseHdc(hdc);
                }
                frames.Add(new KeyValuePair<long, Bitmap>(t, bmp));
            }
            next += intervalMs;
            var wait = next - clock.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait);
        }

        System.IO.Directory.CreateDirectory(outDir);
        long prev = -1;
        foreach (var f in frames)
        {
            var name = string.Format("f{0:00000}.png", f.Key - windowAt);
            f.Value.Save(System.IO.Path.Combine(outDir, name), ImageFormat.Png);
            log.AppendLine(name + (prev >= 0 ? (" +" + (f.Key - prev)) : ""));
            prev = f.Key;
            f.Value.Dispose();
        }
        return log.ToString();
    }
}
"@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing
[StartupFrames]::Run($Exe, $Arguments, $OutDir, $DurationMs, $IntervalMs, $ResizeAtMs, $ResizeBy)
