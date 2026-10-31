using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Vaktr.App.Controls;

// Shared motion primitives.
//
// The app already animates layout transitions with Storyboards (TelemetryPanelCard), but live
// *values* — gauge arcs, readouts — snapped straight to their new number. Storyboards are the
// wrong tool there: a new target arrives every scrape tick, so we need to retarget mid-flight
// from wherever the value currently is, not restart a fixed From→To animation.
//
// Everything here is UI-thread only; CompositionTarget.Rendering fires on the UI thread.
internal static class Motion
{
    /// <summary>Standard duration for a value settling into place. Must stay well under the
    /// fastest scrape interval (1s) so a gauge is never permanently mid-flight.</summary>
    public const double ValueDurationSeconds = 0.30;

    /// <summary>
    /// How far an entering element travels. Kept small deliberately: a long rise reads as the app
    /// being slow to settle, and at a 6px travel the eye registers the arrival rather than the
    /// journey. Paired with short durations below so the whole launch sequence finishes well
    /// inside half a second.
    /// </summary>
    private const float EntranceRise = 6f;

    private static bool? s_animationsEnabled;

    /// <summary>
    /// Honours the OS "show animations in Windows" setting. When off, every animator in this
    /// file degrades to an immediate snap — the pre-animation behaviour.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get
        {
            if (s_animationsEnabled is { } cached) return cached;
            bool value;
            try
            {
                value = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                value = true;
            }
            s_animationsEnabled = value;
            return value;
        }
    }

    public static double EaseOutCubic(double t)
    {
        var inv = 1d - t;
        return 1d - (inv * inv * inv);
    }

    public static void PlayEntrance(UIElement element, TimeSpan delay = default)
    {
        if (!AnimationsEnabled || element.XamlRoot is null)
        {
            return;
        }

        Visual? visual = null;
        try
        {
            visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f),
                new Vector2(0.3f, 1f));

            // Build both animations BEFORE hiding the element. The previous order set
            // Opacity = 0 first, so any throw between there and StartAnimation — or an element
            // torn down mid-flight — left the visual permanently invisible with no way back.
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, easing);
            opacity.Duration = TimeSpan.FromMilliseconds(170);
            opacity.DelayTime = delay;

            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.InsertKeyFrame(0f, new Vector3(0f, EntranceRise, 0f));
            offset.InsertKeyFrame(1f, Vector3.Zero, easing);
            offset.Duration = TimeSpan.FromMilliseconds(210);
            offset.DelayTime = delay;

            visual.Opacity = 0f;
            visual.Offset = new Vector3(0f, EntranceRise, 0f);
            visual.StartAnimation("Opacity", opacity);
            visual.StartAnimation("Offset", offset);
        }
        catch (Exception)
        {
            // An entrance flourish is never worth a blank panel or a crash. Restore a visible,
            // un-offset state and carry on without the animation.
            try
            {
                if (visual is not null)
                {
                    visual.StopAnimation("Opacity");
                    visual.StopAnimation("Offset");
                    visual.Opacity = 1f;
                    visual.Offset = Vector3.Zero;
                }
            }
            catch
            {
                // Nothing further we can do; the element keeps its XAML-level opacity.
            }
        }
    }

}

/// <summary>
/// One process-wide CompositionTarget.Rendering subscription shared by every active animator.
/// Subscribing per-control would keep the compositor ticking for as many controls as exist;
/// this attaches on the first active animator and detaches the moment the last one settles, so
/// a static dashboard costs nothing.
/// </summary>
internal static class MotionTicker
{
    private static readonly List<Action<double>> s_callbacks = [];
    private static readonly List<Action<double>> s_scratch = [];
    private static readonly Stopwatch s_clock = new();
    private static bool s_attached;
    private static double s_lastElapsed;

    public static void Add(Action<double> tick)
    {
        if (s_callbacks.Contains(tick)) return;
        s_callbacks.Add(tick);

        if (!s_attached)
        {
            s_attached = true;
            s_clock.Restart();
            s_lastElapsed = 0;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public static void Remove(Action<double> tick)
    {
        s_callbacks.Remove(tick);

        if (s_callbacks.Count == 0 && s_attached)
        {
            s_attached = false;
            CompositionTarget.Rendering -= OnRendering;
            s_clock.Stop();
        }
    }

    private static void OnRendering(object? sender, object e)
    {
        var now = s_clock.Elapsed.TotalSeconds;
        var dt = now - s_lastElapsed;
        s_lastElapsed = now;

        // A callback may remove itself when it settles, so iterate a copy.
        s_scratch.Clear();
        s_scratch.AddRange(s_callbacks);

        foreach (var cb in s_scratch)
        {
            try
            {
                cb(dt);
            }
            catch (Exception)
            {
                // Without isolation one throwing animator aborted the whole loop, skipping every
                // remaining animator for that frame, and - because it was never removed - threw
                // again at 60Hz forever. Drop the offender and keep the rest running.
                s_callbacks.Remove(cb);

                if (s_callbacks.Count == 0 && s_attached)
                {
                    s_attached = false;
                    CompositionTarget.Rendering -= OnRendering;
                    s_clock.Stop();
                }
            }
        }
    }
}

/// <summary>
/// A double that eases toward a target. Retargeting mid-flight continues from the current
/// value rather than jumping, so rapid updates stay smooth. Unsubscribes from the ticker the
/// moment it reaches its target.
/// </summary>
internal sealed class AnimatedDouble
{
    private readonly Action _onTick;
    private readonly double _duration;
    private readonly Action<double> _tick;

    private double _from;
    private double _to;
    private double _elapsed;
    private bool _running;

    public AnimatedDouble(double initial, Action onTick, double durationSeconds = Motion.ValueDurationSeconds)
    {
        Current = initial;
        _from = initial;
        _to = initial;
        _onTick = onTick;
        _duration = durationSeconds;
        _tick = Advance;
    }

    public double Current { get; private set; }

    public bool IsAnimating => _running;

    public void AnimateTo(double target)
    {
        if (Math.Abs(target - _to) < 0.001 && _running) return;

        if (!Motion.AnimationsEnabled || _duration <= 0)
        {
            SnapTo(target);
            return;
        }

        if (Math.Abs(target - Current) < 0.05)
        {
            SnapTo(target);
            return;
        }

        _from = Current;
        _to = target;
        _elapsed = 0;

        if (!_running)
        {
            _running = true;
            MotionTicker.Add(_tick);
        }
    }

    public void SnapTo(double value)
    {
        Stop();
        _from = value;
        _to = value;
        Current = value;
        _onTick();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        MotionTicker.Remove(_tick);
    }

    private void Advance(double dt)
    {
        _elapsed += dt;
        var t = Math.Clamp(_elapsed / _duration, 0d, 1d);
        Current = _from + ((_to - _from) * Motion.EaseOutCubic(t));

        if (t >= 1d)
        {
            Current = _to;
            _running = false;
            MotionTicker.Remove(_tick);
        }

        _onTick();
    }
}
