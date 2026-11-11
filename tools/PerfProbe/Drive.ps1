# Drives a running SwissOlly window without touching the mouse, the keyboard or the foreground:
# screenshots by PrintWindow, resizing by SetWindowPos (no activation), clicks through UI Automation.
#   -Do "shot:name" "size:1100x900" "wait:800" "invoke:Settings" "invoke:Processes#2" "scroll:100"
#       "set:Custom range from=2026-11-09 22:00" "dump" "crop:name:x,y,w,h"
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string[]]$Do,
    [string]$ExeLike = '*\Release\*'
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class Drive
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

    public static string Size(IntPtr hwnd)
    {
        RECT r; GetWindowRect(hwnd, out r);
        return (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + " at " + r.Left + "," + r.Top;
    }

    // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
    public static void Resize(IntPtr hwnd, int w, int h) { SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, 0x0002 | 0x0004 | 0x0010); }

    public static void Shot(IntPtr hwnd, string path, int cx, int cy, int cw, int ch)
    {
        RECT r; GetWindowRect(hwnd, out r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2);
                g.ReleaseHdc(hdc);
            }
            if (cw > 0 && ch > 0)
            {
                var rect = Rectangle.Intersect(new Rectangle(cx, cy, cw, ch), new Rectangle(0, 0, w, h));
                using (var part = bmp.Clone(rect, PixelFormat.Format32bppArgb)) part.Save(path, ImageFormat.Png);
            }
            else bmp.Save(path, ImageFormat.Png);
        }
    }
}
'@

$proc = Get-CimInstance Win32_Process -Filter "Name='SwissOlly.exe'" | Where-Object { $_.ExecutablePath -like $ExeLike } | Select-Object -First 1
if (-not $proc) { "no SwissOlly process matching $ExeLike"; exit 1 }
$hwnd = [Drive]::FindWindow([uint32]$proc.ProcessId)
if ($hwnd -eq [IntPtr]::Zero) { "no window for pid $($proc.ProcessId)"; exit 1 }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Find-ByName([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

foreach ($step in $Do) {
    $parts = $step.Split(':', 2)
    $verb = $parts[0]; $arg = if ($parts.Length -gt 1) { $parts[1] } else { '' }
    switch ($verb) {
        'wait' { Start-Sleep -Milliseconds ([int]$arg) }
        'size' { $wh = $arg.Split('x'); [Drive]::Resize($hwnd, [int]$wh[0], [int]$wh[1]); "size -> $([Drive]::Size($hwnd))" }
        'shot' { [Drive]::Shot($hwnd, (Join-Path $OutDir "$arg.png"), 0, 0, 0, 0); "shot $arg ($([Drive]::Size($hwnd)))" }
        'crop' {
            $nameAndRect = $arg.Split(':'); $c = $nameAndRect[1].Split(',') | ForEach-Object { [int]$_ }
            [Drive]::Shot($hwnd, (Join-Path $OutDir "$($nameAndRect[0]).png"), $c[0], $c[1], $c[2], $c[3]); "crop $($nameAndRect[0])"
        }
        'set' {
            # set:<accessible name>=<text>, for the app's text fields (they implement ValuePattern).
            $nameAndValue = $arg.Split('=', 2)
            $found = Find-ByName $nameAndValue[0]
            if ($found.Count -eq 0) { "set '$($nameAndValue[0])': not found"; break }
            $p = $null
            if ($found[0].TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) {
                $p.SetValue($nameAndValue[1]); "set '$($nameAndValue[0])' = '$($nameAndValue[1])'"
            }
            else { "set '$($nameAndValue[0])': no ValuePattern" }
        }
        'invoke' {
            # invoke:<accessible name>, or invoke:<name>#2 for the second element with that name.
            $index = 0
            if ($arg -match '^(.*)#(\d+)$') { $arg = $Matches[1]; $index = [int]$Matches[2] - 1 }
            # A button and the text inside it share a name: leave the text out when there is a control.
            $found = @(Find-ByName $arg)
            $controls = @($found | Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text })
            if ($controls.Count -gt 0) { $found = $controls }
            if ($found.Count -le $index) { "invoke '$arg': not found ($($found.Count) match)"; break }
            $el = $found[$index]; $done = $false
            foreach ($pattern in @([System.Windows.Automation.InvokePattern]::Pattern, [System.Windows.Automation.SelectionItemPattern]::Pattern, [System.Windows.Automation.TogglePattern]::Pattern)) {
                $p = $null
                if ($el.TryGetCurrentPattern($pattern, [ref]$p)) {
                    if ($p -is [System.Windows.Automation.InvokePattern]) { $p.Invoke() }
                    elseif ($p -is [System.Windows.Automation.SelectionItemPattern]) { $p.Select() }
                    else { $p.Toggle() }
                    "invoke '$arg' via $($p.GetType().Name) ($($found.Count) match)"; $done = $true; break
                }
            }
            if (-not $done) { "invoke '$arg': no usable pattern on $($el.Current.ControlType.ProgrammaticName)" }
        }
        'scroll' {
            $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
            $scrollers = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
            $n = 0
            foreach ($s in $scrollers) {
                $sp = $s.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, [double]$arg); $n++ }
            }
            "scroll $arg% on $n scroller(s)"
        }
        'dump' {
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            $all | Where-Object { $_.Current.Name -and $_.Current.ControlType.ProgrammaticName -notmatch 'Text$' } |
                ForEach-Object { "$($_.Current.ControlType.ProgrammaticName.Replace('ControlType.', '')): $($_.Current.Name)" } |
                Select-Object -Unique | Select-Object -First 120
        }
        default { "unknown step $step" }
    }
}
