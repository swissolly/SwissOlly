using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace SwissOlly.App.Controls;

/// <summary>
/// A round colour choice: a 22px dot in a 32px target, with a ring for the selected colour.
/// The ring's space is always reserved, so selecting a swatch never moves the row.
/// </summary>
internal sealed class ColorSwatchButton : UserControl
{
    private readonly Ellipse _ring;
    private readonly ScaleTransform _pressScale = new() { ScaleX = 1d, ScaleY = 1d };
    private bool _isSelected;
    private bool _isHovered;
    private bool _isFocused;

    public ColorSwatchButton(string name, string hex)
    {
        ColorName = name;
        Hex = hex;
        Width = UiMetrics.MinHitTarget;
        Height = UiMetrics.MinHitTarget;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        ProtectedCursor = PointerCursors.Get(Microsoft.UI.Input.InputSystemCursorShape.Hand);

        _ring = new Ellipse { Width = 30, Height = 30, StrokeThickness = 2, IsHitTestVisible = false };
        var dot = new Ellipse
        {
            Width = 22,
            Height = 22,
            Fill = new SolidColorBrush(ThemeVisuals.ParseColor(hex)),
            // A hairline keeps white on a light theme, or black on Obsidian, from vanishing.
            Stroke = ThemeVisuals.ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            StrokeThickness = UiMetrics.Hairline,
            RenderTransform = _pressScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            IsHitTestVisible = false,
        };
        Content = new Grid
        {
            // Transparent, not null: the whole 32px square takes the pointer, not just the dot.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { _ring, dot },
        };

        AutomationProperties.SetName(this, name);
        ToolTipService.SetToolTip(this, $"{name} · {hex.ToUpperInvariant()}");

        PointerEntered += (_, _) => { _isHovered = true; UpdateVisualState(); };
        PointerExited += (_, _) => { _isHovered = false; SetPressed(false); UpdateVisualState(); };
        PointerPressed += (_, _) => SetPressed(true);
        PointerReleased += (_, _) => SetPressed(false);
        PointerCanceled += (_, _) => SetPressed(false);
        Tapped += (_, e) =>
        {
            e.Handled = true;
            RaiseClick();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            {
                e.Handled = true;
                RaiseClick();
            }
        };
        GotFocus += (_, _) => { _isFocused = FocusState == FocusState.Keyboard; UpdateVisualState(); };
        LostFocus += (_, _) => { _isFocused = false; UpdateVisualState(); };

        UpdateVisualState();
    }

    public event EventHandler? Click;

    public string ColorName { get; }

    public string Hex { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            AutomationProperties.SetItemStatus(this, value ? "Selected" : string.Empty);
            UpdateVisualState();
        }
    }

    internal void RaiseClick() => Click?.Invoke(this, EventArgs.Empty);

    private void SetPressed(bool pressed) => _pressScale.ScaleX = _pressScale.ScaleY = pressed ? 0.88d : 1d;

    private void UpdateVisualState()
    {
        // Keyboard focus in the primary text colour, so it can't be mistaken for the selection.
        _ring.Stroke = _isFocused
            ? ThemeVisuals.ResolveBrush("TextPrimaryBrush", "#F4F5F7")
            : _isSelected
                ? ThemeVisuals.ResolveBrush("AccentStrongBrush", "#B6D9FF")
                : _isHovered
                    ? ThemeVisuals.ResolveBrush("TextMutedBrush", "#90959D")
                    : null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ColorSwatchAutomationPeer(this);

    private sealed class ColorSwatchAutomationPeer(ColorSwatchButton owner)
        : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(ColorSwatchButton);

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        public void Invoke() => owner.RaiseClick();
    }
}
