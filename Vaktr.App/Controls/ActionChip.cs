using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Vaktr.App.ViewModels;

namespace Vaktr.App.Controls;

public sealed class ActionChip : UserControl
{
    private readonly Border _surface;
    private readonly Border _glow;
    private readonly TextBlock _label;
    private bool _isActive;
    private bool _isFilled;
    private bool _isFocused;
    private bool _isHovered;
    private bool _isPressed;
    private readonly ScaleTransform _pressScale = new() { ScaleX = 1d, ScaleY = 1d };
    private string _text = string.Empty;

    // Cached brushes to avoid allocating new gradient objects on every hover/state change
    private Brush? _cachedIdleBg;
    private Brush? _cachedHoverBg;
    private Brush? _cachedAccentBg;
    private bool _brushCacheIsLight;

    public ActionChip()
    {
        UseLayoutRounding = true;
        UseSystemFocusVisuals = false;

        _surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            // Constant. The ring is drawn by changing the brush only: switching thickness between
            // 0, 1.0 and 1.2 by state re-ran layout on every hover and focus change and nudged the
            // label by a pixel each time.
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Background = ResolveBrush("SurfaceElevatedBrush", "#111D2A"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#243D55"),
            // Press feedback is a scale, not an opacity dip. RenderTransform keeps it off the
            // layout path entirely, so repeatedly mashing a range chip costs nothing.
            RenderTransform = _pressScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };

        _glow = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Opacity = 0,
            IsHitTestVisible = false,
        };

        _label = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        _surface.Child = new Grid
        {
            Children =
            {
                _glow,
                _label,
            },
        };
        Content = _surface;

        // Show hand cursor on hover to indicate clickable
        Loaded += (_, _) =>
        {
            try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand); }
            catch { /* cursor not supported in all hosts */ }
        };

        _surface.PointerEntered += OnPointerEntered;
        _surface.PointerExited += OnPointerExited;
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerReleased += OnPointerReleased;
        _surface.Tapped += OnTapped;

        IsTabStop = true;
        KeyDown += OnKeyDown;
        // The focus ring is for keyboard navigation only. Showing it for pointer and programmatic
        // focus - and treating focus as hover - made a chip that received focus when a flyout
        // opened look like a second selected option.
        GotFocus += (_, _) =>
        {
            _isFocused = FocusState == FocusState.Keyboard;
            UpdateVisualState();
        };
        LostFocus += (_, _) =>
        {
            _isFocused = false;
            UpdateVisualState();
        };

        UpdateVisualState();
    }

    public event EventHandler? Click;

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            _label.Text = value;
            AutomationProperties.SetName(this, value ?? string.Empty);
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            UpdateVisualState();
        }
    }

    public bool IsFilled
    {
        get => _isFilled;
        set
        {
            if (_isFilled == value)
            {
                return;
            }

            _isFilled = value;
            UpdateVisualState();
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        Click?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            Click?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    public void RefreshThemeResources()
    {
        // Invalidate brush cache on theme change
        _cachedIdleBg = null;
        _cachedHoverBg = null;
        _cachedAccentBg = null;
        UpdateVisualState();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        UpdateVisualState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        UpdateVisualState();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = true;
        UpdateVisualState();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isPressed = false;
        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        var useAccent = _isActive || _isFilled;
        var isLight = IsLightPaletteActive();

        // Rebuild cached brushes if theme changed or not yet created
        if (_cachedIdleBg is null || _brushCacheIsLight != isLight)
        {
            _brushCacheIsLight = isLight;

            // Both themes resolve from the palette. The dark branch used to hardcode a navy
            // gradient (#101A28/#122031) left over from an older palette, so once the app moved
            // to neutral surfaces every chip and entry field rendered visibly blue against grey
            // cards. Resolving from theme resources fixes that drift permanently and is what
            // lets any new palette apply to these controls without touching this file.
            _cachedIdleBg = ResolveBrush("SurfaceElevatedBrush", isLight ? "#F0F5FB" : "#222529");
            _cachedHoverBg = ResolveBrush("SurfaceStrongBrush", isLight ? "#E0EAF4" : "#292D31");
            _cachedAccentBg = ResolveBrush("AccentSoftBrush", isLight ? "#C8E8F8" : "#294A6B");
        }

        _surface.Background = useAccent
            ? _cachedAccentBg
            : _isHovered ? _cachedHoverBg : _cachedIdleBg;

        // Inactive chips carry a surface fill but no outline. Chips appear in groups of four
        // (1m / 5m / 15m / 1h, Chart / Highest / Lowest / Name), and outlining every one of them
        // made a row of ranges read as four competing buttons rather than one choice among four.
        // The selected chip is the only one that gets a border, so the group reads as a segmented
        // control. Focus keeps its ring regardless — that is an accessibility affordance, not
        // decoration.
        var showBorder = useAccent || _isFocused;
        _surface.BorderBrush = showBorder
            ? ResolveBrush("AccentStrongBrush", isLight ? "#04506E" : "#9EEFFF")
            : s_noBorder;

        // Selected chips read at full strength; the rest sit back so the active range is obvious
        // at a glance instead of having to be picked out of four identically-weighted labels.
        _label.Foreground = useAccent || _isHovered
            ? ResolveBrush("TextPrimaryBrush", isLight ? "#0A1824" : "#F2F8FF")
            : ResolveBrush("TextSecondaryBrush", isLight ? "#3C5771" : "#B7CCE1");
        // 0.97 on hover was below the perceptual threshold — it cost a composition update and
        // bought nothing. Hover is carried by the background change; press gets a real scale.
        ApplyPressScale(_isPressed);
        _glow.Background = ResolveBrush("AccentHaloBrush", "#2B8FE6C4");
        _glow.Opacity = _isFocused ? 0.08 : useAccent ? 0.04 : 0;
    }

    /// <summary>
    /// Press goes down instantly and eases back up. That asymmetry is what makes a button feel
    /// responsive rather than mushy — the press must land on the same frame as the click, while
    /// the release can afford to settle.
    /// </summary>
    private void ApplyPressScale(bool pressed)
    {
        // UpdateVisualState runs on every hover, focus, IsActive/IsFilled change and theme
        // refresh - eleven call sites. Starting a Storyboard on each one flooded XAML with
        // animations whenever a chip changed state, which is the same failure class as the
        // implicit-animation crash this control hit before. Only a real press transition
        // should animate.
        if (pressed == _lastAppliedPressed)
        {
            return;
        }

        _lastAppliedPressed = pressed;

        if (!Motion.AnimationsEnabled)
        {
            _pressScale.ScaleX = _pressScale.ScaleY = pressed ? 0.96d : 1d;
            return;
        }

        if (pressed)
        {
            _pressStoryboard?.Stop();
            _pressScale.ScaleX = _pressScale.ScaleY = 0.96d;
            return;
        }

        _pressStoryboard?.Stop();
        var sb = new Storyboard();
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var anim = new DoubleAnimation
            {
                To = 1d,
                Duration = new Duration(TimeSpan.FromMilliseconds(130)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(anim, _pressScale);
            Storyboard.SetTargetProperty(anim, axis);
            sb.Children.Add(anim);
        }
        _pressStoryboard = sb;
        sb.Begin();
    }

    private Storyboard? _pressStoryboard;
    private bool _lastAppliedPressed;
    private static readonly Brush s_noBorder = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static Brush CreateSurfaceGradient(string startHex, string endHex) => ThemeVisuals.CreateThemeResourceGradient(startHex, endHex);

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Windows.UI.Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);
}
