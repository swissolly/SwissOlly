using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SwissOlly.App.ViewModels;

namespace SwissOlly.App.Controls;

/// <summary>
/// One theme in the Settings picker: a miniature of the app drawn in that theme's own palette,
/// with its name underneath. The frame around it follows the current theme; the miniature never
/// changes, so every card shows exactly what choosing it will look like.
/// </summary>
internal sealed class ThemePreviewCard : UserControl
{
    private static readonly FontFamily s_iconFont = new("Segoe Fluent Icons");

    /// <summary>The CPU series colour, which charts keep in every theme (darkened on light ones).</summary>
    private const string ChartStrokeHex = "#5DE6FF";

    private readonly Border _frame;
    private readonly Border _checkBadge;
    private readonly ScaleTransform _pressScale = new() { ScaleX = 1d, ScaleY = 1d };
    private bool _isSelected;
    private bool _isHovered;
    private bool _isFocused;

    public ThemePreviewCard(ThemeInfo theme)
    {
        Theme = theme;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        // Assigned once here, never from Loaded: see PointerCursors.
        ProtectedCursor = PointerCursors.Get(Microsoft.UI.Input.InputSystemCursorShape.Hand);

        var name = new TextBlock
        {
            Text = theme.Label,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeVisuals.ResolveBrush("TextPrimaryBrush", "#F4F5F7"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        var description = new TextBlock
        {
            Text = theme.Description,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 11,
            Foreground = ThemeVisuals.ResolveBrush("TextMutedBrush", "#90959D"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        _checkBadge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeVisuals.ResolveBrush("AccentBrush", "#79B8FF"),
            Child = new FontIcon
            {
                Glyph = "", // CheckMark
                FontFamily = s_iconFont,
                FontSize = 10,
                Foreground = ThemeVisuals.ResolveBrush("AppBackdropBrush", "#111315"),
                IsHitTestVisible = false,
            },
            Visibility = Visibility.Collapsed,
        };

        var labelRow = new Grid { Margin = new Thickness(2, 8, 2, 1), ColumnSpacing = 6 };
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelRow.Children.Add(new StackPanel { Spacing = 1, Children = { name, description } });
        labelRow.Children.Add(_checkBadge);
        Grid.SetColumn(_checkBadge, 1);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var preview = BuildMiniature(theme);
        layout.Children.Add(preview);
        layout.Children.Add(labelRow);
        Grid.SetRow(labelRow, 1);

        _frame = new Border
        {
            // Constant thickness: the ring is drawn by changing the brush, so selecting a card
            // never re-runs layout or nudges its contents.
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(UiMetrics.Enclosing(UiMetrics.ControlRadius, 4)),
            Padding = new Thickness(6),
            Child = layout,
            RenderTransform = _pressScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
        Content = _frame;

        AutomationProperties.SetName(this, $"{theme.Label} theme");
        AutomationProperties.SetHelpText(this, theme.Description);
        ToolTipService.SetToolTip(this, $"{theme.Label} · {theme.Description}");

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

    public ThemeInfo Theme { get; }

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

    /// <summary>Raises <see cref="Click"/>: a tap, Enter or Space, or an assistive-technology invoke.</summary>
    internal void RaiseClick() => Click?.Invoke(this, EventArgs.Empty);

    private void SetPressed(bool pressed) => _pressScale.ScaleX = _pressScale.ScaleY = pressed ? 0.97d : 1d;

    private void UpdateVisualState()
    {
        _frame.Background = ThemeVisuals.ResolveBrush(
            _isHovered ? "SurfaceStrongBrush" : "SurfaceElevatedBrush",
            _isHovered ? "#292D31" : "#222529");
        // Keyboard focus in the primary text colour, so it can't be mistaken for the selection.
        _frame.BorderBrush = _isFocused
            ? ThemeVisuals.ResolveBrush("TextPrimaryBrush", "#F4F5F7")
            : _isSelected
            ? ThemeVisuals.ResolveBrush("AccentStrongBrush", "#B6D9FF")
            : _isHovered
                ? ThemeVisuals.ResolveBrush("TextMutedBrush", "#90959D")
                : ThemeVisuals.ResolveBrush("SurfaceStrokeBrush", "#2C3035");
        _checkBadge.Visibility = _isSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A header with the Overview/Settings pill, a chart card and a meter card.</summary>
    private static Border BuildMiniature(ThemeInfo theme)
    {
        var palette = theme.Palette;
        Brush Fill(string hex) => BrushFactory.CreateBrush(hex);
        // The chart line as the theme will draw it: darkened on a light palette, as real charts are.
        var chartStroke = BrushFactory.ParseColor(ChartStrokeHex);
        if (theme.IsLight)
        {
            chartStroke = ThemeVisuals.AdaptSeriesColorForLightPlot(chartStroke, BrushFactory.ParseColor(palette.SurfaceInset));
        }

        Border Bar(double width, double height, string hex) => new()
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Background = Fill(hex),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Border Card(UIElement content) => new()
        {
            Background = Fill(palette.Surface),
            BorderBrush = Fill(palette.SurfaceStroke),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(UiMetrics.SmallRadius),
            Padding = new Thickness(5, 4, 5, 5),
            Child = content,
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Width = 6, Height = 6, Fill = Fill(palette.Accent) },
                Bar(24, 4, palette.TextPrimary),
            },
        });
        var navPill = new Border
        {
            Background = Fill(palette.Surface),
            BorderBrush = Fill(palette.SurfaceStroke),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(1.5),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    new Border
                    {
                        Width = 14,
                        Height = 6,
                        CornerRadius = new CornerRadius(2.5),
                        Background = Fill(palette.AccentSoft),
                        BorderBrush = Fill(palette.AccentStrong),
                        BorderThickness = new Thickness(UiMetrics.Hairline),
                    },
                    new Border { Width = 14, Height = 6 },
                },
            },
        };
        header.Children.Add(navPill);
        Grid.SetColumn(navPill, 1);

        var sparkline = new Polyline
        {
            Points =
            {
                new(0, 12), new(5, 9), new(10, 10), new(15, 5), new(20, 7), new(25, 3),
                new(30, 6), new(35, 4), new(40, 8), new(45, 2), new(50, 5),
            },
            Stretch = Stretch.Fill,
            Height = 13,
            Margin = new Thickness(0, 2, 0, 0),
            Stroke = new SolidColorBrush(chartStroke),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var chartCard = Card(new StackPanel
        {
            Spacing = 3,
            Children = { Bar(14, 3, palette.TextMuted), Bar(22, 5, palette.TextPrimary), sparkline },
        });

        var meterTrack = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0) };
        meterTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        meterTrack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(1.5), Background = Fill(palette.SurfaceStroke) };
        Grid.SetColumnSpan(track, 2);
        meterTrack.Children.Add(track);
        meterTrack.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = Fill(palette.Accent) });
        var meterCard = Card(new StackPanel
        {
            Spacing = 3,
            Children = { Bar(12, 3, palette.TextMuted), Bar(18, 5, palette.TextPrimary), meterTrack },
        });

        var cards = new Grid { ColumnSpacing = 5, Margin = new Thickness(0, 6, 0, 0) };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        cards.Children.Add(chartCard);
        cards.Children.Add(meterCard);
        Grid.SetColumn(meterCard, 1);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(header);
        content.Children.Add(cards);
        Grid.SetRow(cards, 1);

        return new Border
        {
            Background = Fill(palette.AppBackdrop),
            // The theme's own outline, so a light theme's preview still has an edge on a light card.
            BorderBrush = Fill(palette.ShellStroke),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(UiMetrics.ControlRadius - 2),
            Padding = new Thickness(8, 7, 8, 8),
            IsHitTestVisible = false,
            Child = content,
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ThemePreviewCardAutomationPeer(this);

    /// <summary>A button to assistive technology, announced with its theme name and selection.</summary>
    private sealed class ThemePreviewCardAutomationPeer(ThemePreviewCard owner)
        : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(ThemePreviewCard);

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        public void Invoke() => owner.RaiseClick();
    }
}
