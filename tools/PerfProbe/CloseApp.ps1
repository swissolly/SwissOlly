# Closes every running SwissOlly.exe the way its X button does (WM_SYSCOMMAND, SC_CLOSE) and waits.
# Plain WM_CLOSE was ignored twice by a copy running under the Visual Studio debugger.
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class CloseShell {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l); delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static bool Close(uint pid) {
    IntPtr f = IntPtr.Zero;
    EnumWindows(delegate (IntPtr h, IntPtr l) { uint p; GetWindowThreadProcessId(h, out p); if (p != pid) return true; var c = new StringBuilder(256); GetClassName(h, c, 256); if (c.ToString() == "WinUIDesktopWin32WindowClass") { f = h; return false; } return true; }, IntPtr.Zero);
    return f != IntPtr.Zero && PostMessage(f, 0x0112, (IntPtr)0xF060, IntPtr.Zero);
  }
}
'@
foreach ($p in Get-Process SwissOlly -ErrorAction SilentlyContinue) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if (-not [CloseShell]::Close([uint32]$p.Id)) { "pid $($p.Id): no window to close"; continue }
    if ($p.WaitForExit(30000)) { "closed pid $($p.Id) in $($sw.ElapsedMilliseconds) ms" } else { "pid $($p.Id) still running" }
}
