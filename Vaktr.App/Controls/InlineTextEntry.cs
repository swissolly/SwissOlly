using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Vaktr.App.ViewModels;

namespace Vaktr.App.Controls;

public sealed class InlineTextEntry : UserControl
{
    private readonly Border _surface;
    private readonly Border _glow;
    private readonly TextBlock _label;
    private bool _isFocused;
    private bool _isHovered;
    private bool _isPressed;
    private string _text = string.Empty;
    private string _placeholderText = string.Empty;
    private Brush? _cachedIdleBg;
    private Brush? _cachedHoverBg;
    private Brush? _cachedFocusBg;
    private bool _brushCacheIsLight;

    public InlineTextEntry()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _label = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _surface = new Border
        {
            Background = CreateSurfaceGradient("#101B2A", "#142335"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            MinHeight = 40,
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

        _surface.Child = new Grid
        {
            Children =
            {
                _glow,
                _label,
            },
        };

        Content = _surface;

        Loaded += (_, _) =>
        {
            try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.IBeam); }
            catch { /* cursor not supported in all hosts */ }
        };

        Tapped += OnTapped;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;

        UpdateVisualState();
    }

    public event EventHandler? TextChanged;

    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }

            _text = value;
            UpdateVisualState();
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_placeholderText, value, StringComparison.Ordinal))
            {
                return;
            }

            _placeholderText = value;
            UpdateVisualState();
        }
    }

    public void RefreshThemeResources()
    {
        _cachedIdleBg = null;
        _cachedHoverBg = null;
        _cachedFocusBg = null;
        UpdateVisualState();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
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

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        _isFocused = true;
        UpdateVisualState();
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        _isFocused = false;
        _isPressed = false;
        UpdateVisualState();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Back:
                if (_text.Length > 0)
                {
                    Text = _text[..^1];
                }

                e.Handled = true;
                break;
            case VirtualKey.Escape:
                _ = Focus(FocusState.Programmatic);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                break;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        var character = args.Character;
        if (char.IsControl(character))
        {
            return;
        }

        Text += character;
        args.Handled = true;
    }

    private void UpdateVisualState()
    {
        var isLight = IsLightPaletteActive();
        if (_cachedIdleBg is null || _brushCacheIsLight != isLight)
        {
            _brushCacheIsLight = isLight;
            if (isLight)
            {
                _cachedIdleBg = ResolveBrush("SurfaceBrush", "#FFFFFF");
                _cachedHoverBg = ResolveBrush("SurfaceElevatedBrush", "#F4F7FB");
                _cachedFocusBg = ResolveBrush("SurfaceStrongBrush", "#E8EEF5");
            }
            else
            {
                // Was a hardcoded navy gradient from a superseded palette, which left entry
                // fields reading blue against neutral grey cards. Palette-driven like the
                // light branch, so every theme applies here for free.
                _cachedIdleBg = ResolveBrush("SurfaceInsetBrush", "#141619");
                _cachedHoverBg = ResolveBrush("SurfaceElevatedBrush", "#222529");
                _cachedFocusBg = ResolveBrush("SurfaceStrongBrush", "#292D31");
            }
        }

        _surface.Background = _isFocused
            ? _cachedFocusBg
            : _isHovered ? _cachedHoverBg : _cachedIdleBg;
        _surface.BorderBrush = ResolveBrush(_isFocused ? "AccentStrongBrush" : "SurfaceStrokeBrush",
            _isFocused ? (isLight ? "#04506E" : "#B7F7FF") : isLight ? "#C0CEDC" : "#27425E");
        // Constant thickness: varying it on hover/focus invalidated layout and nudged the
        // caret and text. Focus is signalled by the accent brush and glow instead, which is
        // just as visible and costs nothing.
        _surface.BorderThickness = new Thickness(1);
        Opacity = _isPressed ? 0.96 : _isHovered ? 0.99 : 1.0;
        _glow.Background = ResolveBrush("AccentHaloBrush", "#1B68DAFF");
        _glow.Opacity = _isFocused ? 0.06 : _isHovered ? 0.02 : 0;

        var displayText = _text;
        if (string.IsNullOrWhiteSpace(displayText))
        {
            _label.Text = _isFocused ? $"{_placeholderText} |" : _placeholderText;
            _label.Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6");
            AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(_placeholderText) ? "Input" : _placeholderText);
            return;
        }

        _label.Text = _isFocused ? $"{displayText} |" : displayText;
        _label.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(_placeholderText) ? displayText : _placeholderText);
    }

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static Brush CreateSurfaceGradient(string startHex, string endHex) => ThemeVisuals.CreateThemeResourceGradient(startHex, endHex);

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Windows.UI.Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);
}
