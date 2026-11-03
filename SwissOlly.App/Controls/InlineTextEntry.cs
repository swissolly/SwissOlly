using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace SwissOlly.App.Controls;

/// <summary>
/// Themed single-line text field, drawn by the app.
/// </summary>
/// <remarks>
/// Not a <see cref="TextBox"/>: this unpackaged build ships no WinUI control resources (the
/// app's .pri is ~1KB), and TextBox, or even <c>new XamlControlsResources()</c>, fails with
/// E_FAIL as soon as it enters the tree. So editing is implemented here: caret, pointer and
/// Shift selection, word navigation, clipboard, undo, a context menu, horizontal scrolling and
/// a UIA Value pattern. Text arrives through CharacterReceived, which delivers IME results once
/// committed, but there is no inline composition preview.
/// </remarks>
public sealed class InlineTextEntry : UserControl
{
    private const double FontSizePx = 13;
    private static readonly FontFamily s_font = new("Segoe UI Variable Text");

    private readonly Border _surface;
    private readonly Border _glow;
    private readonly TextBlock _placeholder;
    private readonly Canvas _viewport;
    private readonly RectangleGeometry _viewportClip = new();
    private readonly TextBlock _label;
    private readonly Rectangle _selection;
    private readonly Rectangle _caret;
    private readonly TextBlock _measure = new() { FontFamily = s_font, FontSize = FontSizePx };
    private readonly List<(string Text, int Caret)> _undo = [];
    private readonly List<(string Text, int Caret)> _redo = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _blinkTimer;
    private double[] _prefixWidths = [0];
    private double _scrollX;
    private double _pipeWidth = -1;
    private bool _isFocused;
    private bool _isHovered;
    private bool _isSelecting;
    private string _text = string.Empty;
    private string _placeholderText = string.Empty;
    private int _caretIndex;
    private int _anchorIndex;
    private Brush? _cachedIdleBg;
    private Brush? _cachedHoverBg;
    private Brush? _cachedFocusBg;
    private bool _brushCacheIsLight;

    public InlineTextEntry()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _placeholder = new TextBlock
        {
            FontFamily = s_font,
            FontSize = FontSizePx,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        _label = new TextBlock
        {
            FontFamily = s_font,
            FontSize = FontSizePx,
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false,
        };
        _selection = new Rectangle { Height = 18, Opacity = 0.35, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _caret = new Rectangle { Width = 1, Height = 17, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _viewport = new Canvas
        {
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Clip = _viewportClip,
            Children = { _selection, _label, _caret },
        };
        _viewport.SizeChanged += (_, e) =>
        {
            _viewportClip.Rect = new Windows.Foundation.Rect(0, -2, e.NewSize.Width, e.NewSize.Height + 4);
            EnsureCaretVisible();
            UpdateTextVisuals();
        };

        _surface = new Border
        {
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(UiMetrics.ControlRadius),
            Padding = new Thickness(12, 8, 12, 8),
            MinHeight = 40,
            // Transparent background keeps the padding hit-testable.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };

        _glow = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(UiMetrics.ControlRadius),
            Opacity = 0,
            IsHitTestVisible = false,
        };

        _surface.Child = new Grid { Children = { _glow, _placeholder, _viewport } };
        Content = _surface;
        ContextFlyout = BuildContextMenu();

        // Once, from the shared cursor, never from Loaded: see PointerCursors for the crash that caused.
        ProtectedCursor = PointerCursors.Get(InputSystemCursorShape.IBeam);

        PointerEntered += (_, _) => { _isHovered = true; UpdateVisualState(); };
        PointerExited += (_, _) => { _isHovered = false; UpdateVisualState(); };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _isSelecting = false;
        DoubleTapped += OnDoubleTapped;
        GotFocus += (_, _) => { _isFocused = true; RestartBlink(); UpdateVisualState(); };
        LostFocus += (_, _) => { _isFocused = false; _isSelecting = false; StopBlink(); UpdateVisualState(); };
        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;

        RebuildMetrics();
        UpdateVisualState();
    }

    public event EventHandler? TextChanged;

    /// <summary>Raised when Enter is pressed in the field.</summary>
    public event EventHandler? Submitted;

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

            // A programmatic set is a fresh value, not an edit to undo back through.
            _undo.Clear();
            _redo.Clear();
            SetTextCore(value, value.Length);
        }
    }

    private string _accessibleLabel = string.Empty;

    /// <summary>What assistive technology announces the field as; the visible label beside it.</summary>
    public string AccessibleLabel
    {
        get => _accessibleLabel;
        set
        {
            _accessibleLabel = value ?? string.Empty;
            UpdateVisualState();
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

    private int SelectionStart => Math.Min(_caretIndex, _anchorIndex);

    private int SelectionLength => Math.Abs(_caretIndex - _anchorIndex);

    public void RefreshThemeResources()
    {
        _cachedIdleBg = null;
        _cachedHoverBg = null;
        _cachedFocusBg = null;
        UpdateVisualState();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new InlineTextEntryAutomationPeer(this);

    // ---- Editing ------------------------------------------------------------------------------

    private void SetTextCore(string text, int caret)
    {
        _text = text;
        _caretIndex = _anchorIndex = Math.Clamp(caret, 0, text.Length);
        RebuildMetrics();
        EnsureCaretVisible();
        RestartBlink();
        UpdateVisualState();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Edit(string replacement)
    {
        var start = SelectionStart;
        var next = _text[..start] + replacement + _text[(start + SelectionLength)..];
        if (string.Equals(next, _text, StringComparison.Ordinal))
        {
            return;
        }

        _undo.Add((_text, _caretIndex));
        if (_undo.Count > 100)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        SetTextCore(next, start + replacement.Length);
    }

    private void MoveCaret(int index, bool extend)
    {
        _caretIndex = Math.Clamp(index, 0, _text.Length);
        if (!extend)
        {
            _anchorIndex = _caretIndex;
        }

        EnsureCaretVisible();
        RestartBlink();
        UpdateTextVisuals();
    }

    private void SelectAll()
    {
        _anchorIndex = 0;
        _caretIndex = _text.Length;
        EnsureCaretVisible();
        UpdateTextVisuals();
    }

    private int WordBoundary(int from, int direction)
    {
        var i = from;
        if (direction < 0)
        {
            while (i > 0 && !char.IsLetterOrDigit(_text[i - 1])) i--;
            while (i > 0 && char.IsLetterOrDigit(_text[i - 1])) i--;
        }
        else
        {
            while (i < _text.Length && char.IsLetterOrDigit(_text[i])) i++;
            while (i < _text.Length && !char.IsLetterOrDigit(_text[i])) i++;
        }

        return i;
    }

    private void Copy()
    {
        if (SelectionLength == 0)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_text.Substring(SelectionStart, SelectionLength));
        Clipboard.SetContent(package);
    }

    private async void Paste()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var pasted = await content.GetTextAsync();
            // Single line: newlines from a multi-line copy become spaces.
            Edit(pasted.ReplaceLineEndings(" ").Replace('\t', ' '));
        }
        catch
        {
            // Clipboard can be locked by another process; a failed paste is a no-op.
        }
    }

    private void Undo(List<(string Text, int Caret)> from, List<(string Text, int Caret)> to)
    {
        if (from.Count == 0)
        {
            return;
        }

        var (text, caret) = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add((_text, _caretIndex));
        SetTextCore(text, caret);
    }

    // ---- Input --------------------------------------------------------------------------------

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var handled = true;
        switch (e.Key)
        {
            case VirtualKey.Left:
                MoveCaret(!shift && SelectionLength > 0 && !ctrl ? SelectionStart
                    : ctrl ? WordBoundary(_caretIndex, -1) : _caretIndex - 1, shift);
                break;
            case VirtualKey.Right:
                MoveCaret(!shift && SelectionLength > 0 && !ctrl ? SelectionStart + SelectionLength
                    : ctrl ? WordBoundary(_caretIndex, 1) : _caretIndex + 1, shift);
                break;
            case VirtualKey.Home:
                MoveCaret(0, shift);
                break;
            case VirtualKey.End:
                MoveCaret(_text.Length, shift);
                break;
            case VirtualKey.Back:
                if (SelectionLength == 0)
                {
                    _anchorIndex = ctrl ? WordBoundary(_caretIndex, -1) : Math.Max(0, _caretIndex - 1);
                }

                Edit(string.Empty);
                break;
            case VirtualKey.Delete:
                if (SelectionLength == 0)
                {
                    _anchorIndex = ctrl ? WordBoundary(_caretIndex, 1) : Math.Min(_text.Length, _caretIndex + 1);
                }

                Edit(string.Empty);
                break;
            case VirtualKey.A when ctrl:
                SelectAll();
                break;
            case VirtualKey.C when ctrl:
                Copy();
                break;
            case VirtualKey.X when ctrl:
                Copy();
                Edit(string.Empty);
                break;
            case VirtualKey.V when ctrl:
                Paste();
                break;
            case VirtualKey.Z when ctrl:
                if (shift) Undo(_redo, _undo); else Undo(_undo, _redo);
                break;
            case VirtualKey.Y when ctrl:
                Undo(_redo, _undo);
                break;
            case VirtualKey.Enter:
                Submitted?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.Escape when SelectionLength > 0:
                // First Esc collapses a selection; a second one bubbles (closes a flyout, etc.).
                MoveCaret(_caretIndex, extend: false);
                break;
            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (char.IsControl(args.Character))
        {
            return;
        }

        Edit(args.Character.ToString());
        args.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(FocusState.Pointer);
        MoveCaret(IndexAt(point.Position.X), extend: IsDown(VirtualKey.Shift));
        _isSelecting = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isSelecting)
        {
            MoveCaret(IndexAt(e.GetCurrentPoint(_viewport).Position.X), extend: true);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isSelecting = false;
        ReleasePointerCapture(e.Pointer);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var index = IndexAt(e.GetPosition(_viewport).X);
        var start = index;
        var end = index;
        while (start > 0 && char.IsLetterOrDigit(_text[start - 1])) start--;
        while (end < _text.Length && char.IsLetterOrDigit(_text[end])) end++;
        if (start == end)
        {
            SelectAll();
            return;
        }

        _anchorIndex = start;
        MoveCaret(end, extend: true);
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Cut", () => { Copy(); Edit(string.Empty); });
        Add("Copy", Copy);
        Add("Paste", Paste);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Select all", SelectAll);
        menu.Opening += (_, _) =>
        {
            ((MenuFlyoutItem)menu.Items[0]).IsEnabled = SelectionLength > 0;
            ((MenuFlyoutItem)menu.Items[1]).IsEnabled = SelectionLength > 0;
        };
        return menu;
    }

    // ---- Layout -------------------------------------------------------------------------------

    private double MeasureWidth(string text)
    {
        // TextBlock measurement drops trailing spaces, so measure against a sentinel character.
        if (_pipeWidth < 0)
        {
            _measure.Text = "|";
            _measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _pipeWidth = _measure.DesiredSize.Width;
        }

        _measure.Text = text + "|";
        _measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(0, _measure.DesiredSize.Width - _pipeWidth);
    }

    private void RebuildMetrics()
    {
        _prefixWidths = new double[_text.Length + 1];
        for (var i = 1; i <= _text.Length; i++)
        {
            _prefixWidths[i] = MeasureWidth(_text[..i]);
        }
    }

    private int IndexAt(double viewportX)
    {
        var x = viewportX + _scrollX;
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _prefixWidths.Length; i++)
        {
            var distance = Math.Abs(_prefixWidths[i] - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void EnsureCaretVisible()
    {
        var width = _viewport.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var caretX = _prefixWidths[Math.Min(_caretIndex, _prefixWidths.Length - 1)];
        var textWidth = _prefixWidths[^1];
        if (caretX - _scrollX > width - 2)
        {
            _scrollX = caretX - width + 2;
        }
        else if (caretX - _scrollX < 0)
        {
            _scrollX = caretX;
        }

        // Never scroll past the end of the text once it fits again.
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, textWidth - width + 2));
    }

    private void UpdateTextVisuals()
    {
        _label.Text = _text;
        Canvas.SetLeft(_label, -_scrollX);

        var caretX = _prefixWidths[Math.Min(_caretIndex, _prefixWidths.Length - 1)] - _scrollX;
        Canvas.SetLeft(_caret, Math.Round(caretX));
        Canvas.SetTop(_caret, 0.5);
        _caret.Visibility = _isFocused ? Visibility.Visible : Visibility.Collapsed;

        if (_isFocused && SelectionLength > 0)
        {
            var left = _prefixWidths[SelectionStart] - _scrollX;
            _selection.Width = Math.Max(0, _prefixWidths[SelectionStart + SelectionLength] - _prefixWidths[SelectionStart]);
            Canvas.SetLeft(_selection, left);
            _selection.Visibility = Visibility.Visible;
        }
        else
        {
            _selection.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Caret blink --------------------------------------------------------------------------

    private void RestartBlink()
    {
        _caret.Opacity = 1;
        if (!_isFocused)
        {
            return;
        }

        if (_blinkTimer is null)
        {
            _blinkTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(530);
            _blinkTimer.Tick += (_, _) => _caret.Opacity = _caret.Opacity > 0 ? 0 : 1;
        }

        _blinkTimer.Stop();
        // Reduced motion: a steady caret.
        if (Motion.AnimationsEnabled)
        {
            _blinkTimer.Start();
        }
    }

    private void StopBlink() => _blinkTimer?.Stop();

    // ---- Visual state -------------------------------------------------------------------------

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
                _cachedIdleBg = ResolveBrush("SurfaceInsetBrush", "#141619");
                _cachedHoverBg = ResolveBrush("SurfaceElevatedBrush", "#222529");
                _cachedFocusBg = ResolveBrush("SurfaceStrongBrush", "#292D31");
            }
        }

        _surface.Background = _isFocused
            ? _cachedFocusBg
            : _isHovered ? _cachedHoverBg : _cachedIdleBg;
        // Constant thickness: varying it on hover/focus invalidates layout and nudges the text.
        // Focus is signalled by the accent brush and glow instead.
        _surface.BorderBrush = ResolveBrush(_isFocused ? "AccentStrongBrush" : "SurfaceStrokeBrush",
            _isFocused ? (isLight ? "#04506E" : "#B7F7FF") : isLight ? "#C0CEDC" : "#27425E");
        _glow.Background = ResolveBrush("AccentHaloBrush", "#1B68DAFF");
        _glow.Opacity = _isFocused ? 0.06 : _isHovered ? 0.02 : 0;

        _label.Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        _caret.Fill = ResolveBrush("TextPrimaryBrush", "#F2F8FF");
        _selection.Fill = ResolveBrush("AccentBrush", "#79B8FF");
        _placeholder.Text = _placeholderText;
        _placeholder.Foreground = ResolveBrush("TextMutedBrush", "#90959D");
        _placeholder.Visibility = _text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Announced by its label ("Retention window"), with the placeholder as the example; it used
        // to be announced as the placeholder alone, so Narrator read "24h, edit".
        AutomationProperties.SetName(this, !string.IsNullOrWhiteSpace(_accessibleLabel)
            ? _accessibleLabel
            : string.IsNullOrWhiteSpace(_placeholderText) ? "Input" : _placeholderText);
        AutomationProperties.SetHelpText(this, !string.IsNullOrWhiteSpace(_accessibleLabel) && !string.IsNullOrWhiteSpace(_placeholderText)
            ? $"For example {_placeholderText}"
            : string.Empty);
        UpdateTextVisuals();
    }

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    /// <summary>Exposes the field to screen readers as an editable text box.</summary>
    private sealed class InlineTextEntryAutomationPeer(InlineTextEntry owner)
        : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => false;

        public string Value => owner.Text;

        public void SetValue(string value) => owner.Text = value;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override string GetClassNameCore() => "TextBox";

        protected override object GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
    }
}
