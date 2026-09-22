using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(12, 6, 12, 6),
            Background = ResolveBrush("SurfaceElevatedBrush", "#111D2A"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#243D55"),
        };

        _glow = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(9),
            Opacity = 0,
            IsHitTestVisible = false,
        };

        _label = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 11,
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
        GotFocus += (_, _) =>
        {
            _isFocused = true;
            _isHovered = true;
            UpdateVisualState();
        };
        LostFocus += (_, _) =>
        {
            _isFocused = false;
            _isHovered = false;
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
            if (isLight)
            {
                _cachedIdleBg = ResolveBrush("SurfaceElevatedBrush", "#F0F5FB");
                _cachedHoverBg = ResolveBrush("SurfaceStrongBrush", "#E0EAF4");
                _cachedAccentBg = ResolveBrush("AccentSoftBrush", "#C8E8F8");
            }
            else
            {
                _cachedIdleBg = CreateSurfaceGradient("#101A28", "#122031");
                _cachedHoverBg = CreateSurfaceGradient("#132232", "#17293D");
                _cachedAccentBg = CreateSurfaceGradient("#18344B", "#122437");
            }
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
        _surface.BorderThickness = new Thickness(showBorder ? (_isFocused ? 1.2 : 1.0) : 0);

        if (showBorder)
        {
            _surface.BorderBrush = ResolveBrush("AccentStrongBrush", isLight ? "#04506E" : "#9EEFFF");
        }

        // Selected chips read at full strength; the rest sit back so the active range is obvious
        // at a glance instead of having to be picked out of four identically-weighted labels.
        _label.Foreground = useAccent || _isHovered
            ? ResolveBrush("TextPrimaryBrush", isLight ? "#0A1824" : "#F2F8FF")
            : ResolveBrush("TextSecondaryBrush", isLight ? "#3C5771" : "#B7CCE1");
        Opacity = _isPressed ? 0.84 : _isHovered ? 0.97 : 1.0;
        _glow.Background = ResolveBrush("AccentHaloBrush", "#2B8FE6C4");
        _glow.Opacity = _isFocused ? 0.08 : useAccent ? 0.04 : 0;
    }

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static Brush CreateSurfaceGradient(string startHex, string endHex) => ThemeVisuals.CreateThemeResourceGradient(startHex, endHex);

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Windows.UI.Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);
}
