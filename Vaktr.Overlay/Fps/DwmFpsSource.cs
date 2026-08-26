using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vaktr.Overlay.Rendering;

namespace Vaktr.Overlay.Fps;

// Fallback FPS source, used only when no ETW session can be created (see EtwPresentFpsSource).
//
// DwmFlush() blocks until DWM finishes its next composition frame, so timing successive flushes
// measures the *compositor's* cadence — effectively the display refresh rate. It is NOT the
// game's frame rate and is not attributed to any process: the pid argument is deliberately
// unused because this measurement cannot distinguish one application from another. It reads
// correctly only while the game is v-synced to the refresh rate, and over-reports whenever the
// game runs below it. Prefer EtwPresentFpsSource; this exists so the overlay degrades to a
// plausible number instead of showing nothing.
//
// When the game is in exclusive fullscreen DWM is bypassed; DwmFlush returns a non-zero HRESULT
// immediately, letting us detect that case without blocking or polling with an arbitrary timeout.
public sealed class DwmFpsSource : IFpsSource
{
    private volatile bool _running;
    private Thread? _thread;

    public event Action<double>? FpsUpdated;
    public event Action<string>? ErrorOccurred;

    public FpsAccuracy Accuracy => FpsAccuracy.Approximate;

    public void StartTracking(uint pid)
    {
        StopTracking();
        _running = true;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "VaktrDwmFps" };
        _thread.Start();
    }

    public void StopTracking()
    {
        _running = false;
        // DwmFlush holds the thread for at most one frame (~7ms@144Hz, ~16ms@60Hz).
        // In the exclusive-fullscreen branch the thread sleeps 500ms, so cap join at 700ms.
        _thread?.Join(700);
        _thread = null;
    }

    private void PollLoop()
    {
        var sw = Stopwatch.StartNew();
        var window = new Queue<double>(); // frame times in seconds
        const int WindowSize = 60;        // ~1s at 60fps / ~0.4s at 144fps
        long lastFireStamp = 0;
        long fireEveryTicks = Stopwatch.Frequency / 2; // emit at most every 500ms
        int errorStreak = 0;
        const int ErrorStreakFirst = 1;   // report immediately on first failure
        const int ErrorStreakLimit = 6;   // re-report after sustained failures (3s)
        bool exclusiveReported = false;

        while (_running)
        {
            sw.Restart();
            int hr = DwmFlush();
            double frameTime = sw.Elapsed.TotalSeconds;

            if (!_running) break;

            if (hr != 0)
            {
                // DWM bypassed — game is in exclusive fullscreen. Back off to avoid spinning.
                Thread.Sleep(500);
                errorStreak++;
                if (errorStreak == ErrorStreakFirst || errorStreak == ErrorStreakLimit)
                {
                    // 0x80263001 = DWM not enabled / suspended for this output
                    OverlayLog.Write($"DwmFlush hr=0x{(uint)hr:X8} streak={errorStreak} — DWM bypassed (exclusive fullscreen)");
                    ErrorOccurred?.Invoke("exclusive-fullscreen");
                    exclusiveReported = true;
                }
                continue;
            }

            if (exclusiveReported)
            {
                OverlayLog.Write("DwmFlush resumed — DWM compositing active again");
                exclusiveReported = false;
            }
            errorStreak = 0;

            // Skip frames outside sane fps range [2, 500] to absorb startup and stall jitter.
            if (frameTime < 0.002 || frameTime > 0.5)
                continue;

            window.Enqueue(frameTime);
            while (window.Count > WindowSize)
                window.Dequeue();

            long now = Stopwatch.GetTimestamp();
            if (window.Count >= 10 && now - lastFireStamp >= fireEveryTicks)
            {
                lastFireStamp = now;
                double sum = 0;
                foreach (var t in window) sum += t;
                FpsUpdated?.Invoke(Math.Round(1.0 / (sum / window.Count)));
            }
        }
    }

    public void Dispose() => StopTracking();

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
