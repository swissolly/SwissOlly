using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Vaktr.App.Controls;
using Windows.Foundation;

namespace Vaktr.App;

// TEMPORARY diagnostic. Drives a resize/move gesture through the real handlers, frame by frame,
// and logs what the code computes. Enabled only by VAKTR_GESTURE_PROBE=<br|left|move>.
public sealed partial class ShellWindow
{
    private static readonly string ProbeLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vaktr-probe.log");

    private void MaybeStartGestureProbe()
    {
        var mode = Environment.GetEnvironmentVariable("VAKTR_GESTURE_PROBE");
        if (string.IsNullOrEmpty(mode))
        {
            return;
        }

        File.WriteAllText(ProbeLogPath, $"{DateTime.Now:HH:mm:ss.fff} probe {mode}\n");
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(9);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => RunGestureProbe(mode);
        timer.Start();
    }

    private static void ProbeLog(string line)
        => File.AppendAllText(ProbeLogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");

    private void RunGestureProbe(string mode)
    {
        SetLayoutEditMode(true, commit: false);
        var card = _panelCards.Values
            .Where(c => c.ActualWidth > 0 && c.Panel is not null)
            .OrderBy(c => Grid.GetRow(c)).ThenBy(c => Grid.GetColumn(c))
            .FirstOrDefault();
        if (card is null)
        {
            ProbeLog("no card");
            return;
        }

        var edge = mode switch
        {
            "left" => TelemetryPanelCard.ResizeEdge.Left,
            "move" => TelemetryPanelCard.ResizeEdge.None,
            _ => TelemetryPanelCard.ResizeEdge.BottomRight,
        };

        var local = new Point(
            edge.HasFlag(TelemetryPanelCard.ResizeEdge.Right) ? card.ActualWidth - 6 : edge.HasFlag(TelemetryPanelCard.ResizeEdge.Left) ? 4 : 120,
            edge.HasFlag(TelemetryPanelCard.ResizeEdge.Bottom) ? card.ActualHeight - 6 : edge == TelemetryPanelCard.ResizeEdge.None ? 30 : card.ActualHeight / 2);
        var origin = card.TransformToVisual(null).TransformPoint(local);
        ProbeLog($"card={card.Panel?.PanelKey} cell=({Grid.GetColumn(card)},{Grid.GetRow(card)},{Grid.GetColumnSpan(card)}x{Grid.GetRowSpan(card)}) size={card.ActualWidth:F0}x{card.ActualHeight:F0} grid={_dashboardGrid.ActualWidth:F0} edge={edge} origin={origin.X:F0},{origin.Y:F0}");

        var layoutPasses = 0;
        EventHandler<object> onLayout = (_, _) => layoutPasses++;
        _dashboardGrid.LayoutUpdated += onLayout;

        // Pointer path (window px from origin) as keyframes: (time s, dx, dy).
        (double T, double X, double Y)[] path = edge switch
        {
            TelemetryPanelCard.ResizeEdge.Left => [(0, 0, 0), (1.2, -260, 0), (3.6, -260, 0), (4.6, 60, 0), (7.0, 60, 0)],
            TelemetryPanelCard.ResizeEdge.None => [(0, 0, 0), (1.2, 380, 140), (3.6, 380, 140), (4.6, 520, 420), (7.0, 520, 420)],
            _ => [(0, 0, 0), (1.2, 300, 140), (3.6, 300, 140), (4.6, -90, 260), (7.0, -90, 260)],
        };

        var sw = Stopwatch.StartNew();
        var lastMs = 0d;
        var frame = 0;
        var released = false;
        card.ProbePointer(0, edge, origin);

        EventHandler<object>? onRender = null;
        onRender = (_, _) =>
        {
            var t = sw.Elapsed.TotalSeconds;
            var ms = sw.Elapsed.TotalMilliseconds;
            var dt = ms - lastMs;
            lastMs = ms;
            frame++;

            if (t >= path[^1].T)
            {
                if (!released)
                {
                    released = true;
                    card.ProbePointer(2, edge, origin);
                    ProbeLog($"RELEASE layoutPasses={layoutPasses}");
                }

                if (t > path[^1].T + 1.5)
                {
                    CompositionTarget.Rendering -= onRender;
                    _dashboardGrid.LayoutUpdated -= onLayout;
                    ProbeLog($"END cell=({Grid.GetColumn(card)},{Grid.GetRow(card)},{Grid.GetColumnSpan(card)}x{Grid.GetRowSpan(card)}) size={card.ActualWidth:F0}x{card.ActualHeight:F0}");
                }

                return;
            }

            var k = 0;
            while (k < path.Length - 2 && t > path[k + 1].T) k++;
            var a = path[k];
            var b = path[k + 1];
            var f = Math.Clamp((t - a.T) / Math.Max(0.001, b.T - a.T), 0, 1);
            var p = new Point(origin.X + a.X + ((b.X - a.X) * f), origin.Y + a.Y + ((b.Y - a.Y) * f));
            var passes = layoutPasses;
            layoutPasses = 0;
            card.ProbePointer(1, edge, p);

            if (frame % 3 == 0)
            {
                var slot = LayoutInformation.GetLayoutSlot(card);
                var ph = _layoutPlaceholder;
                var phText = ph is null || ph.Visibility != Visibility.Visible
                    ? "-"
                    : $"({Grid.GetColumn(ph)},{Grid.GetRow(ph)},{Grid.GetColumnSpan(ph)}x{Grid.GetRowSpan(ph)})";
                var screen = card.TransformToVisual(null).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
                ProbeLog($"f={frame,4} dt={dt,5:F1} ptr=({p.X - origin.X,6:F0},{p.Y - origin.Y,6:F0}) card={card.Width,6:F0}x{card.Height,-6:F0} actual={card.ActualWidth:F0}x{card.ActualHeight:F0} onscreen=({screen.X:F0},{screen.Y:F0} {screen.Width:F0}x{screen.Height:F0}) slot={slot.Width:F0}x{slot.Height:F0} placeholder={phText} layouts={passes}");
            }
        };
        CompositionTarget.Rendering += onRender;
    }
}
