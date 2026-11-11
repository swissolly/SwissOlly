using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using SwissOlly.App.Controls;
using SwissOlly.Core.Models;

namespace SwissOlly.App;

// TEMPORARY perf probe driver (SWISSOLLY_PERF_PROBE=1). Remove before finishing.
public sealed partial class ShellWindow
{
    private DispatcherQueueTimer? _perfProbeTimer;
    private readonly Queue<(string Label, Action Run)> _perfProbeSteps = new();
    private readonly Dictionary<ThemeMode, ThemePreviewCard> _themeCards = [];

    private void StartPerfProbeIfEnabled()
    {
        if (!PerfProbe.Enabled || _perfProbeTimer is not null)
        {
            return;
        }

        var original = _viewModel.SelectedTheme;
        PerfProbe.Log($"=== probe start {DateTime.Now:O} theme={original} panels={_panelCards.Count} " +
#if DEBUG
            "config=Debug" +
#else
            "config=Release" +
#endif
            $" proc={Environment.ProcessorCount}");

        void Add(string label, Action run) => _perfProbeSteps.Enqueue((label, run));
        void Track(string label, Action action)
        {
            var start = PerfProbe.Now;
            action();
            PerfProbe.Log($"  {label} sync: {PerfProbe.Ms(start):0.000} ms");
            PerfProbe.TrackAfter(label, start, _uiQueue);
        }

        void Theme(ThemeMode mode) => Add($"theme {mode}", () =>
        {
            var from = _draftThemeMode;
            if (from == mode || !_themeCards.TryGetValue(mode, out var card))
            {
                return;
            }

            var flip = IsLightTheme(from) != IsLightTheme(mode);
            PerfProbe.Log($"- theme {from} -> {mode} ({(flip ? "flip" : "same")}) panels={_panelCards.Count}");
            Track("click", card.RaiseClick);
        });

        Add("open settings", () =>
        {
            PerfProbe.Log("- open Settings");
            Track("settings", () => SetActiveShellView(settings: true));
        });

        for (var round = 0; round < 2; round++)
        {
            foreach (var theme in ThemeCatalog.All)
            {
                Theme(theme.Mode);
            }
        }

        ThemeMode[] alternating =
        [
            ThemeMode.Dark, ThemeMode.Light, ThemeMode.Obsidian, ThemeMode.Frost, ThemeMode.Midnight, ThemeMode.Sandstone,
            ThemeMode.Nord, ThemeMode.Blossom, ThemeMode.Violet, ThemeMode.Light, ThemeMode.Synthwave, ThemeMode.Frost,
            ThemeMode.Forest, ThemeMode.Sandstone, ThemeMode.Ember, ThemeMode.Blossom,
        ];
        foreach (var mode in alternating)
        {
            Theme(mode);
        }

        Theme(original);
        Add("overview", () =>
        {
            PerfProbe.Log("- show Overview");
            Track("overview", () => SetActiveShellView(settings: false));
        });
        Add("settings again", () =>
        {
            PerfProbe.Log("- show Settings");
            Track("settings", () => SetActiveShellView(settings: true));
        });
        Add("theme while settings", () => { });
        Theme(original == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);
        Theme(original);

        // Screenshot sweep for stale colours: each theme reached from one of the other kind
        // (dark after light and back), Settings top and bottom, then the Overview.
        ThemeMode[] sweep =
        [
            ThemeMode.Light, ThemeMode.Dark, ThemeMode.Frost, ThemeMode.Obsidian, ThemeMode.Sandstone, ThemeMode.Midnight,
            ThemeMode.Blossom, ThemeMode.Nord, ThemeMode.Violet, ThemeMode.Synthwave, ThemeMode.Forest, ThemeMode.Ember,
        ];
        foreach (var mode in sweep)
        {
            Theme(mode);
            Add($"shot settings-top {mode}", () => Capture($"{(int)mode:00}-{mode}-settings-top"));
            Add($"scroll settings {mode}", () => _scrollHost.ChangeView(null, _scrollHost.ScrollableHeight, null, true));
            Add($"shot settings-end {mode}", () => Capture($"{(int)mode:00}-{mode}-settings-end"));
            Add($"overview {mode}", () => SetActiveShellView(settings: false));
            Add($"shot overview {mode}", () => Capture($"{(int)mode:00}-{mode}-overview"));
            Add($"scroll overview {mode}", () => _scrollHost.ChangeView(null, _scrollHost.ScrollableHeight, null, true));
            Add($"shot overview-end {mode}", () => Capture($"{(int)mode:00}-{mode}-overview-end"));
            Add($"back {mode}", () =>
            {
                _scrollHost.ChangeView(null, 0, null, true);
                SetActiveShellView(settings: true);
                _scrollHost.ChangeView(null, 0, null, true);
            });
        }

        Theme(original);
        Add("overview again", () =>
        {
            PerfProbe.Log("- show Overview");
            Track("overview", () => SetActiveShellView(settings: false));
        });
        Add("done", () =>
        {
            PerfProbe.Log($"=== probe done {DateTime.Now:O}");
            _perfProbeTimer?.Stop();
            ExitApplication();
        });

        _perfProbeTimer = _uiQueue.CreateTimer();
        _perfProbeTimer.IsRepeating = true;
        _perfProbeTimer.Interval = TimeSpan.FromSeconds(12);
        _perfProbeTimer.Tick += (timer, _) =>
        {
            timer.Interval = TimeSpan.FromMilliseconds(900);
            if (_perfProbeSteps.Count == 0)
            {
                timer.Stop();
                return;
            }

            var (label, run) = _perfProbeSteps.Dequeue();
            try
            {
                run();
            }
            catch (Exception ex)
            {
                PerfProbe.Log($"  step {label} threw {ex}");
            }
        };
        _perfProbeTimer.Start();
    }

    // In-process PrintWindow(hwnd, hdc, 2) from a worker, saved as a top-down 32-bit BMP.
    private void Capture(string name)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "swissolly-perf", "shots");
        _ = Task.Run(() =>
        {
            try
            {
                ProbeGdi.GetWindowRect(hwnd, out var rect);
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                var screen = ProbeGdi.GetDC(0);
                var memory = ProbeGdi.CreateCompatibleDC(screen);
                var header = new ProbeGdi.BitmapInfoHeader
                {
                    Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32,
                };
                var bitmap = ProbeGdi.CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
                var old = ProbeGdi.SelectObject(memory, bitmap);
                ProbeGdi.PrintWindow(hwnd, memory, 2);
                var pixels = new byte[width * height * 4];
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                ProbeGdi.SelectObject(memory, old);
                ProbeGdi.DeleteObject(bitmap);
                ProbeGdi.DeleteDC(memory);
                ProbeGdi.ReleaseDC(0, screen);

                Directory.CreateDirectory(folder);
                using var file = File.Create(System.IO.Path.Combine(folder, name + ".bmp"));
                using var writer = new BinaryWriter(file);
                writer.Write((ushort)0x4D42);
                writer.Write(54 + pixels.Length);
                writer.Write(0);
                writer.Write(54);
                writer.Write(40);
                writer.Write(width);
                writer.Write(-height);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(0);
                writer.Write(pixels.Length);
                writer.Write(2835);
                writer.Write(2835);
                writer.Write(0);
                writer.Write(0);
                writer.Write(pixels);
                PerfProbe.Log($"  shot {name}");
            }
            catch (Exception ex)
            {
                PerfProbe.Log($"  shot {name} failed {ex.Message}");
            }
        });
    }

    private static class ProbeGdi
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
            public int Compression; public int SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
            public int ClrUsed; public int ClrImportant;
        }

        [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
        [DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint hdc);
        [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
        [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);
    }
}
