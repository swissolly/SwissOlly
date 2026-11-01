using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using SwissOlly.App.Controls;
using SwissOlly.Core.Models;
using Windows.Foundation;
using Windows.System;

namespace SwissOlly.App;

// Dashboard layout editing: the move/resize gesture, live reflow, and the commit.
//
// Modelled on react-grid-layout (the grid behind Grafana's dashboards), adapted to WinUI:
//
//  - The panel under the pointer floats out of grid layout and follows the pointer 1:1 at its
//    real size. Its content reflows the way a window's does; nothing is scaled.
//  - The board is re-resolved through GridLayoutEngine only when the *snapped* cell changes.
//    Every other panel then springs toward its resolved cell by a translate - no layout pass.
//  - A placeholder glides to exactly where the panel will land.
//  - Release commits once. The floating panel springs into its cell, and the other panels'
//    springs are re-based onto the committed layout, so they carry on from where they are
//    instead of jumping.
//
// Pointer input is coalesced to one update per rendered frame. A 1000 Hz mouse otherwise
// drives the whole pipeline many times between two frames.
public sealed partial class ShellWindow
{
    private sealed class LayoutGestureState
    {
        public required TelemetryPanelCard Card { get; init; }
        public required string Key { get; init; }
        public required TelemetryPanelCard.ResizeEdge Edge { get; init; }
        public required PanelLayout OriginDisplay { get; init; }
        public required PanelLayout OriginSaved { get; init; }
        public required List<PanelPlacement> SavedPlacements { get; init; }
        public required Rect OriginRect { get; init; }
        public required Point PressBoardPoint { get; init; }
        public required int MinimumWidthUnits { get; init; }
        public required int LowestOtherBottom { get; init; }
        public Point LatestWindowPoint { get; set; }
        public Rect LiveRect { get; set; }
        public PanelLayout? Target { get; set; }
        public List<PanelPlacement>? ResolvedSaved { get; set; }
        public bool IsMove => Edge == TelemetryPanelCard.ResizeEdge.None;
    }

    /// <summary>A released panel springing from where the pointer left it into its cell.</summary>
    private sealed class FloatSettle
    {
        public required TelemetryPanelCard Card { get; init; }
        public required string Key { get; init; }
        public required PanelLayout Cell { get; init; }
        public required SpringValue X { get; init; }
        public required SpringValue Y { get; init; }
        public required SpringValue W { get; init; }
        public required SpringValue H { get; init; }
    }

    private LayoutGestureState? _layoutGesture;
    private FloatSettle? _floatSettle;
    private readonly Dictionary<string, (SpringValue X, SpringValue Y)> _reflowSprings = new(StringComparer.OrdinalIgnoreCase);
    private Border? _dropPlaceholder;
    private readonly TranslateTransform _dropPlaceholderOffset = new();
    private readonly SpringValue _placeholderX = new(0, 0.18);
    private readonly SpringValue _placeholderY = new(0, 0.18);
    private readonly SpringValue _placeholderW = new(0, 0.18);
    private readonly SpringValue _placeholderH = new(0, 0.18);
    private Action<double>? _layoutTick;
    private bool _layoutTickerAttached;
    private TextBlock? _editModeHint;
    private const string EditModeHintText = "Drag a panel to move it · drag an edge or corner to resize · Esc when done";
    private const string BrowseModeHintText = "Drag across a chart to zoom · click to pin a reading · double-click or Esc to reset";

    /// <summary>True while a panel is being dragged or is still settling from a drop.</summary>
    private bool IsLayoutMotionActive => _layoutGesture is not null || _floatSettle is not null;

    // ---- Board geometry -----------------------------------------------------------------------
    //
    // Grid spacing belongs to the pitch: a 48px row with a 14px gap advances 62px. Converting
    // pixels with the bare 48px unit made the snapped target race 29% ahead of the pointer.

    private double BoardColumnPitch => (_dashboardGrid.ActualWidth + _dashboardGrid.ColumnSpacing) / PanelLayout.Columns;

    private double BoardRowPitch => DashboardRowUnitPx + _dashboardGrid.RowSpacing;

    private Rect CellRect(PanelLayout layout)
    {
        var columnPitch = BoardColumnPitch;
        var rowPitch = BoardRowPitch;
        return new Rect(
            layout.X * columnPitch,
            layout.Y * rowPitch,
            Math.Max(0, (layout.W * columnPitch) - _dashboardGrid.ColumnSpacing),
            Math.Max(0, (layout.H * rowPitch) - _dashboardGrid.RowSpacing));
    }

    private static double SpanPixels(int units, double pitch, double spacing) => Math.Max(0, (units * pitch) - spacing);

    private static int SnapUnits(double pixels, double pitch)
        => pitch > 0 ? (int)Math.Round(pixels / pitch, MidpointRounding.AwayFromZero) : 0;

    /// <summary>
    /// Window-content point to board coordinates, using the board's position *now*. Converting at
    /// use rather than at input time is what lets auto-scroll move the board under a still pointer.
    /// </summary>
    private Point WindowToBoard(Point windowPoint)
    {
        var origin = _dashboardGrid.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new Point(windowPoint.X - origin.X, windowPoint.Y - origin.Y);
    }

    // ---- Edit mode ----------------------------------------------------------------------------

    private void OnToggleLayoutEditClick(object? sender, EventArgs e)
        => SetLayoutEditMode(!_isLayoutEditMode, commit: true);

    private void OnCancelLayoutEditClick(object? sender, EventArgs e)
    {
        CancelActiveLayoutGesture();
        if (_layoutSnapshot is { } snapshot)
        {
            // Panels glide back rather than teleport, so Cancel reads as "undo", not "reload".
            ApplyLayoutChangeAnimated(() =>
            {
                _viewModel.RestorePanelLayouts(snapshot);
                ReorderDashboardPanelsInPlace();
            });
        }

        SetLayoutEditMode(false, commit: false);
    }

    /// <summary>
    /// Entering edit mode snapshots geometry so Cancel is a true revert. Edits are a draft until
    /// Done; leaving with <paramref name="commit"/> persists through the debounced save path.
    /// </summary>
    private void SetLayoutEditMode(bool editing, bool commit)
    {
        if (_isLayoutEditMode == editing)
        {
            return;
        }

        if (!editing)
        {
            CancelActiveLayoutGesture();
            CompleteFloatSettle();
        }
        else
        {
            HideGlobalRangeEditor();
        }

        _isLayoutEditMode = editing;
        if (editing)
        {
            _layoutSnapshot = _viewModel.SnapshotPanelLayouts();
        }
        else
        {
            _layoutSnapshot = null;
            if (commit)
            {
                DebouncePersistLayout();
            }
        }

        // Done takes Edit's exact place; Cancel appears beside it. The range controls step aside
        // while arranging - they don't apply to it, and fewer choices keep the mode focused.
        _editLayoutButton.Text = editing ? "Done" : "Edit layout";
        _editLayoutButton.IsActive = editing;
        _editLayoutButton.IsFilled = editing;
        _cancelLayoutButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        _globalRangeButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        RefreshGlobalRangeControls();

        foreach (var card in _panelCards.Values)
        {
            card.SetLayoutEditMode(editing);
        }

        if (_editModeHint is not null)
        {
            // One slot, two modes: arranging instructions while editing, chart gestures otherwise
            // (drag-zoom, pin and reset had no affordance at all).
            _editModeHint.Text = editing ? EditModeHintText : BrowseModeHintText;
            _editModeHint.Opacity = 0;
            FadeElementTo(_editModeHint, 1d, MotionTokens.Quick);
        }

        if (editing)
        {
            // Keyboard focus inside the board scope, so Esc reaches the accelerator.
            _editLayoutButton.Focus(FocusState.Programmatic);
        }
    }

    private void InstallLayoutKeyboardShortcuts()
    {
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            // Esc backs out one level: an in-flight drag first, then edit mode itself. Leaving
            // edit mode with Esc keeps the edits - they were deliberate; Cancel discards.
            if (_layoutGesture is not null)
            {
                CancelActiveLayoutGesture();
                args.Handled = true;
            }
            else if (_isLayoutEditMode)
            {
                SetLayoutEditMode(false, commit: true);
                args.Handled = true;
            }
            else if (!_settingsViewActive && _globalResetZoomButton.Visibility == Visibility.Visible)
            {
                // Then a chart zoom, back to the live window - the Grafana habit.
                OnResetAllZoomClick(null, EventArgs.Empty);
                args.Handled = true;
            }
        };

        _rootLayout.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        _rootLayout.KeyboardAccelerators.Add(escape);

        AddControlShortcut(VirtualKey.Number1, () => SetActiveShellView(false));
        AddControlShortcut(VirtualKey.Number2, () => SetActiveShellView(true));
        AddControlShortcut(VirtualKey.E, () =>
        {
            if (_settingsViewActive)
            {
                SetActiveShellView(false);
            }

            OnToggleLayoutEditClick(null, EventArgs.Empty);
        });
        // Ctrl+[ / Ctrl+] narrow and widen the board's time range one preset at a time.
        AddControlShortcut((VirtualKey)219, () => StepGlobalWindowRange(-1));
        AddControlShortcut((VirtualKey)221, () => StepGlobalWindowRange(+1));
    }

    private static readonly int[] s_rangeStepsMinutes = [1, 5, 15, 30, 60, 720, 1440, 2880, 7200, 10080, 43200, 129600, 525600];

    private void AddControlShortcut(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            // A mid-gesture shortcut would rebuild the board under the pointer.
            if (_layoutGesture is not null)
            {
                return;
            }

            action();
            args.Handled = true;
        };
        _rootLayout.KeyboardAccelerators.Add(accelerator);
    }

    private void StepGlobalWindowRange(int direction)
    {
        if (_settingsViewActive)
        {
            return;
        }

        var current = _viewModel.SelectedWindowMinutes;
        var next = direction > 0
            ? s_rangeStepsMinutes.FirstOrDefault(minutes => minutes > current, current)
            : s_rangeStepsMinutes.LastOrDefault(minutes => minutes < current, current);
        if (next != current)
        {
            RunWithDeferredPanelRendering(() => ApplyGlobalWindowRange(next));
        }
    }

    // ---- Gesture lifecycle --------------------------------------------------------------------

    private void OnCardLayoutGestureStarted(object? sender, LayoutGestureEventArgs e)
    {
        if (sender is not TelemetryPanelCard card)
        {
            return;
        }

        // A drop still settling is finished instantly; two floating panels would fight.
        CompleteFloatSettle();

        if (!_isLayoutEditMode
            || _layoutGesture is not null
            || card.Panel?.PanelKey is not { Length: > 0 } key
            || !_displayPanelLayouts.TryGetValue(key, out var display)
            || _viewModel.GetPanelLayout(key) is not { } saved
            || _dashboardGrid.ActualWidth <= 0)
        {
            card.AbortLayoutGesture();
            return;
        }

        var origin = CellRect(display);
        var lowestOther = _displayPanelLayouts
            .Where(pair => !string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value.Bottom)
            .DefaultIfEmpty(0)
            .Max();

        _layoutGesture = new LayoutGestureState
        {
            Card = card,
            Key = key,
            Edge = e.Edge,
            OriginDisplay = display.Clone(),
            OriginSaved = saved.Clone(),
            SavedPlacements = _viewModel.PanelLayouts
                .Select(pair => new PanelPlacement(pair.Key, pair.Value.Clone()))
                .ToList(),
            OriginRect = origin,
            PressBoardPoint = WindowToBoard(e.Point),
            MinimumWidthUnits = Math.Max(PanelLayout.MinWidth, ResponsiveMinimumWidth()),
            LowestOtherBottom = lowestOther,
            LatestWindowPoint = e.Point,
            LiveRect = origin,
            Target = display.Clone(),
        };

        EnsureBoardRows(origin.Bottom);
        // A resizing card changes every frame, so caching it would only add a re-raster.
        SetBoardCached(true, except: e.Edge == TelemetryPanelCard.ResizeEdge.None ? null : card);
        FloatCard(card, origin, lift: e.Edge == TelemetryPanelCard.ResizeEdge.None);
        ShowDropPlaceholder(origin);
        StartLayoutTicker();
    }

    private void OnCardLayoutGestureMoved(object? sender, LayoutGestureEventArgs e)
    {
        // Store only; the frame tick does the work, at most once per rendered frame.
        if (_layoutGesture is { } gesture && ReferenceEquals(sender, gesture.Card))
        {
            gesture.LatestWindowPoint = e.Point;
        }
    }

    private void OnCardLayoutGestureEnded(object? sender, LayoutGestureEndedEventArgs e)
    {
        if (_layoutGesture is not { } gesture || !ReferenceEquals(sender, gesture.Card))
        {
            return;
        }

        // The release position may not have had a frame of its own yet.
        UpdateLayoutGesture(gesture);
        FinishLayoutGesture(commit: !e.Cancelled);
    }

    private void CancelActiveLayoutGesture()
    {
        if (_layoutGesture is { } gesture)
        {
            gesture.Card.AbortLayoutGesture();
            FinishLayoutGesture(commit: false);
        }
    }

    private void FinishLayoutGesture(bool commit)
    {
        if (_layoutGesture is not { } gesture)
        {
            return;
        }

        _layoutGesture = null;

        var changed = commit
            && gesture.ResolvedSaved is not null
            && gesture.Target is not null
            && !LayoutsEqual(gesture.Target, gesture.OriginDisplay);
        if (changed)
        {
            var resolved = gesture.ResolvedSaved!;
            ApplyLayoutChangeAnimated(() =>
            {
                _viewModel.ApplyPlacements(resolved);
                ReorderDashboardPanelsInPlace();
            });
        }
        else
        {
            // Nothing changed, or the gesture was cancelled: everyone goes home.
            foreach (var springs in _reflowSprings.Values)
            {
                springs.X.SetTarget(0);
                springs.Y.SetTarget(0);
            }
        }

        var cell = _displayPanelLayouts.TryGetValue(gesture.Key, out var landed) ? landed : gesture.OriginDisplay;
        var destination = CellRect(cell);
        var live = gesture.LiveRect;
        var settle = new FloatSettle
        {
            Card = gesture.Card,
            Key = gesture.Key,
            Cell = cell.Clone(),
            X = new SpringValue(live.X, MotionTokens.GestureResponse),
            Y = new SpringValue(live.Y, MotionTokens.GestureResponse),
            W = new SpringValue(live.Width, MotionTokens.GestureResponse),
            H = new SpringValue(live.Height, MotionTokens.GestureResponse),
        };
        settle.X.SetTarget(destination.X);
        settle.Y.SetTarget(destination.Y);
        settle.W.SetTarget(destination.Width);
        settle.H.SetTarget(destination.Height);
        _floatSettle = settle;

        HideDropPlaceholder();
        StartLayoutTicker();
    }

    /// <summary>Applies the latest pointer position. Runs once per frame.</summary>
    private void UpdateLayoutGesture(LayoutGestureState gesture)
    {
        var columnPitch = BoardColumnPitch;
        var rowPitch = BoardRowPitch;
        var boardWidth = _dashboardGrid.ActualWidth;
        if (columnPitch <= 0 || boardWidth <= 0)
        {
            return;
        }

        var pointer = WindowToBoard(gesture.LatestWindowPoint);
        var dx = pointer.X - gesture.PressBoardPoint.X;
        var dy = pointer.Y - gesture.PressBoardPoint.Y;
        var origin = gesture.OriginRect;

        Rect live;
        PanelLayout target;
        if (gesture.IsMove)
        {
            // Bounded to the board's width, and to just below the lowest other panel - dragging
            // further down would only grow empty board and run auto-scroll away with it.
            var left = Math.Clamp(origin.X + dx, 0, Math.Max(0, boardWidth - origin.Width));
            var top = Math.Clamp(origin.Y + dy, 0, Math.Max(0, gesture.LowestOtherBottom * rowPitch));
            live = new Rect(left, top, origin.Width, origin.Height);
            target = new PanelLayout(
                Math.Clamp(SnapUnits(left, columnPitch), 0, PanelLayout.Columns - gesture.OriginDisplay.W),
                Math.Max(0, SnapUnits(top, rowPitch)),
                gesture.OriginDisplay.W,
                gesture.OriginDisplay.H);
        }
        else
        {
            var edge = gesture.Edge;
            var hasLeft = edge.HasFlag(TelemetryPanelCard.ResizeEdge.Left);
            var hasRight = edge.HasFlag(TelemetryPanelCard.ResizeEdge.Right);
            var hasTop = edge.HasFlag(TelemetryPanelCard.ResizeEdge.Top);
            var hasBottom = edge.HasFlag(TelemetryPanelCard.ResizeEdge.Bottom);

            // The live frame is clamped to exactly the sizes the grid can represent, so the
            // panel stops under the pointer at the same limit the snapped target stops at.
            var minWidth = SpanPixels(gesture.MinimumWidthUnits, columnPitch, _dashboardGrid.ColumnSpacing);
            var minHeight = SpanPixels(PanelLayout.MinHeight, rowPitch, _dashboardGrid.RowSpacing);
            var maxHeight = SpanPixels(PanelLayout.MaxHeight, rowPitch, _dashboardGrid.RowSpacing);

            double left = origin.Left, right = origin.Right, top = origin.Top, bottom = origin.Bottom;
            if (hasLeft)
            {
                left = Math.Clamp(origin.Left + dx, 0, origin.Right - minWidth);
            }
            else if (hasRight)
            {
                right = Math.Clamp(origin.Right + dx, origin.Left + minWidth, boardWidth);
            }

            if (hasTop)
            {
                top = Math.Clamp(origin.Top + dy, Math.Max(0, origin.Bottom - maxHeight), origin.Bottom - minHeight);
            }
            else if (hasBottom)
            {
                bottom = Math.Clamp(origin.Bottom + dy, origin.Top + minHeight, origin.Top + maxHeight);
            }

            live = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            var deltaColumns = hasLeft ? SnapUnits(left - origin.Left, columnPitch)
                : hasRight ? SnapUnits(right - origin.Right, columnPitch) : 0;
            var deltaRows = hasTop ? SnapUnits(top - origin.Top, rowPitch)
                : hasBottom ? SnapUnits(bottom - origin.Bottom, rowPitch) : 0;
            target = GridLayoutEngine.ResizeByDelta(
                gesture.OriginDisplay, hasLeft, hasRight, hasTop, hasBottom,
                deltaColumns, deltaRows, gesture.MinimumWidthUnits);
        }

        gesture.LiveRect = live;
        EnsureBoardRows(live.Bottom);
        gesture.Card.SetFloatGeometry(live.X, live.Y, live.Width, live.Height);

        if (gesture.Target is null || !LayoutsEqual(gesture.Target, target))
        {
            gesture.Target = target;
            ResolveLayoutPreview(gesture);
        }
    }

    /// <summary>
    /// Resolves the board for the current snapped target and retargets every other panel and
    /// the placeholder. Runs only when the snapped cell changes, not per pixel.
    /// </summary>
    private void ResolveLayoutPreview(LayoutGestureState gesture)
    {
        var savedTarget = MapDisplayTargetToSaved(gesture.OriginDisplay, gesture.OriginSaved, gesture.Target!);
        var resolvedSaved = GridLayoutEngine.Apply(
            gesture.SavedPlacements.Select(placement => new PanelPlacement(placement.Key, placement.Layout.Clone())),
            gesture.Key,
            savedTarget);
        gesture.ResolvedSaved = resolvedSaved;

        var resolvedDisplay = GridLayoutEngine.ResolveResponsive(resolvedSaved, ResponsiveMinimumWidth())
            .ToDictionary(placement => placement.Key, placement => placement.Layout, StringComparer.OrdinalIgnoreCase);

        var lowest = 0;
        foreach (var (key, card) in _panelCards)
        {
            if (ReferenceEquals(card, gesture.Card)
                || !_displayPanelLayouts.TryGetValue(key, out var current)
                || !resolvedDisplay.TryGetValue(key, out var next))
            {
                continue;
            }

            var from = CellRect(current);
            var to = CellRect(next);
            var springs = ReflowSprings(key);
            springs.X.SetTarget(to.X - from.X);
            springs.Y.SetTarget(to.Y - from.Y);
            lowest = Math.Max(lowest, next.Bottom);
        }

        if (resolvedDisplay.TryGetValue(gesture.Key, out var landing))
        {
            SetPlaceholderTarget(CellRect(landing));
            lowest = Math.Max(lowest, landing.Bottom);
        }

        // Panels pushed below the current last row need board under them, or they slide out of
        // the scrollable extent and can't be reached.
        EnsureBoardRows(lowest * BoardRowPitch);
    }

    /// <summary>
    /// FLIP for the whole board: records where every panel is, applies <paramref name="change"/>,
    /// then re-bases each panel's spring so it continues from its old position to the new cell.
    /// </summary>
    private void ApplyLayoutChangeAnimated(Action change)
    {
        var before = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, layout) in _displayPanelLayouts)
        {
            before[key] = CellRect(layout);
        }

        change();

        foreach (var (key, card) in _panelCards)
        {
            if (card.IsFloating
                || !before.TryGetValue(key, out var from)
                || !_displayPanelLayouts.TryGetValue(key, out var layout))
            {
                continue;
            }

            var to = CellRect(layout);
            var springs = ReflowSprings(key);
            springs.X.Shift(from.X - to.X);
            springs.Y.Shift(from.Y - to.Y);
            springs.X.SetTarget(0);
            springs.Y.SetTarget(0);
            card.SetReflowOffset(springs.X.Value, springs.Y.Value);
        }

        SetBoardCached(true, except: null);
        StartLayoutTicker();
    }

    private (SpringValue X, SpringValue Y) ReflowSprings(string key)
    {
        if (!_reflowSprings.TryGetValue(key, out var springs))
        {
            springs = (new SpringValue(0, MotionTokens.ReflowResponse), new SpringValue(0, MotionTokens.ReflowResponse));
            _reflowSprings[key] = springs;
        }

        return springs;
    }

    // ---- Floating card ------------------------------------------------------------------------

    /// <summary>
    /// Spans the card across the whole board so its slot can never clip it, then hands its
    /// geometry to the frame tick. Left in its own cell, a card resized larger than that cell
    /// was cut off at the cell's edge - which is why growing a panel used to look broken.
    /// </summary>
    private void FloatCard(TelemetryPanelCard card, Rect rect, bool lift)
    {
        Grid.SetRow(card, 0);
        Grid.SetColumn(card, 0);
        Grid.SetColumnSpan(card, PanelLayout.Columns);
        Grid.SetRowSpan(card, Math.Max(1, _dashboardGrid.RowDefinitions.Count));
        card.BeginFloat(lift);
        card.SetFloatGeometry(rect.X, rect.Y, rect.Width, rect.Height);
    }

    private void CompleteFloatSettle()
    {
        if (_floatSettle is not { } settle)
        {
            return;
        }

        _floatSettle = null;

        // The latest committed cell, in case the board changed while the card was settling.
        var cell = _displayPanelLayouts.TryGetValue(settle.Key, out var current) ? current : settle.Cell;
        Grid.SetColumn(settle.Card, cell.X);
        Grid.SetRow(settle.Card, cell.Y);
        Grid.SetColumnSpan(settle.Card, cell.W);
        Grid.SetRowSpan(settle.Card, cell.H);
        settle.Card.EndFloat();
        TrimBoardRows();
        ResumeDeferredSnapshot();
    }

    // ---- Placeholder --------------------------------------------------------------------------

    private Border EnsureDropPlaceholder()
    {
        _dropPlaceholder ??= new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = _dropPlaceholderOffset,
            IsHitTestVisible = false,
            Opacity = 0,
        };

        if (_dropPlaceholder.Parent is null)
        {
            _dashboardGrid.Children.Add(_dropPlaceholder);
        }

        // Above resting panels, beneath the floating one.
        Canvas.SetZIndex(_dropPlaceholder, 50);
        Grid.SetRow(_dropPlaceholder, 0);
        Grid.SetColumn(_dropPlaceholder, 0);
        Grid.SetColumnSpan(_dropPlaceholder, PanelLayout.Columns);
        Grid.SetRowSpan(_dropPlaceholder, Math.Max(1, _dashboardGrid.RowDefinitions.Count));
        return _dropPlaceholder;
    }

    private void ShowDropPlaceholder(Rect rect)
    {
        var placeholder = EnsureDropPlaceholder();
        placeholder.Background = ResolveBrush("AccentSoftBrush", "#294A6B");
        placeholder.BorderBrush = ResolveBrush("AccentBrush", "#79B8FF");
        _placeholderX.Snap(rect.X);
        _placeholderY.Snap(rect.Y);
        _placeholderW.Snap(rect.Width);
        _placeholderH.Snap(rect.Height);
        ApplyPlaceholderGeometry();
        FadeElementTo(placeholder, 0.5, MotionTokens.Quick);
    }

    private void SetPlaceholderTarget(Rect rect)
    {
        _placeholderX.SetTarget(rect.X);
        _placeholderY.SetTarget(rect.Y);
        _placeholderW.SetTarget(rect.Width);
        _placeholderH.SetTarget(rect.Height);
        StartLayoutTicker();
    }

    private void HideDropPlaceholder()
    {
        if (_dropPlaceholder is not null)
        {
            FadeElementTo(_dropPlaceholder, 0d, MotionTokens.Standard);
        }
    }

    private void ApplyPlaceholderGeometry()
    {
        if (_dropPlaceholder is null)
        {
            return;
        }

        _dropPlaceholderOffset.X = _placeholderX.Value;
        _dropPlaceholderOffset.Y = _placeholderY.Value;
        var width = Math.Max(0, _placeholderW.Value);
        var height = Math.Max(0, _placeholderH.Value);
        if (!(Math.Abs(_dropPlaceholder.Width - width) < 0.01))
        {
            _dropPlaceholder.Width = width;
        }

        if (!(Math.Abs(_dropPlaceholder.Height - height) < 0.01))
        {
            _dropPlaceholder.Height = height;
        }
    }

    // ---- Board rows ---------------------------------------------------------------------------

    /// <summary>Grows the board so <paramref name="bottomPixels"/> is inside it.</summary>
    private void EnsureBoardRows(double bottomPixels)
    {
        var rowPitch = BoardRowPitch;
        if (rowPitch <= 0)
        {
            return;
        }

        var needed = (int)Math.Ceiling((bottomPixels + _dashboardGrid.RowSpacing) / rowPitch);
        if (_dashboardGrid.RowDefinitions.Count >= needed)
        {
            return;
        }

        while (_dashboardGrid.RowDefinitions.Count < needed)
        {
            _dashboardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DashboardRowUnitPx) });
        }

        SpanBoardOverlays();
    }

    /// <summary>Drops rows the committed layout no longer needs, once nothing is floating.</summary>
    private void TrimBoardRows()
    {
        if (IsLayoutMotionActive)
        {
            return;
        }

        var needed = _displayPanelLayouts.Count == 0 ? 0 : _displayPanelLayouts.Values.Max(layout => layout.Bottom);
        while (_dashboardGrid.RowDefinitions.Count > Math.Max(1, needed))
        {
            _dashboardGrid.RowDefinitions.RemoveAt(_dashboardGrid.RowDefinitions.Count - 1);
        }

        SpanBoardOverlays();
    }

    /// <summary>Keeps every whole-board element spanning every row.</summary>
    private void SpanBoardOverlays()
    {
        var rows = Math.Max(1, _dashboardGrid.RowDefinitions.Count);
        if (_dropPlaceholder?.Parent is not null)
        {
            Grid.SetRowSpan(_dropPlaceholder, rows);
        }

        foreach (var card in _panelCards.Values)
        {
            if (card.IsFloating)
            {
                Grid.SetRowSpan(card, rows);
            }
        }
    }

    // ---- Frame tick ---------------------------------------------------------------------------

    private void StartLayoutTicker()
    {
        if (_layoutTickerAttached)
        {
            return;
        }

        _layoutTickerAttached = true;
        _layoutTick ??= OnLayoutTick;
        MotionTicker.Add(_layoutTick);
    }

    private void OnLayoutTick(double dt)
    {
        var busy = false;
        if (_layoutGesture is { } gesture)
        {
            AutoScrollForGesture(gesture, dt);
            UpdateLayoutGesture(gesture);
            busy = true;
        }

        busy |= StepReflowSprings(dt);
        busy |= StepPlaceholder(dt);
        busy |= StepFloatSettle(dt);

        if (!busy && _layoutTick is not null)
        {
            MotionTicker.Remove(_layoutTick);
            _layoutTickerAttached = false;
            SetBoardCached(false, except: null);
        }
    }

    private bool _boardCached;

    /// <summary>
    /// Rasterises each panel once while the board is in motion. A moving panel otherwise
    /// re-renders every text run, grid line and chart path each frame - thousands of primitives
    /// for a full board - and SwissOlly is usually open beside a game that has the GPU at 90%, where
    /// each of those draws queues behind the game's. Cached, a panel in motion is one quad.
    /// Released the moment motion ends so live updates render normally again.
    /// </summary>
    private void SetBoardCached(bool cached, TelemetryPanelCard? except)
    {
        if (!cached && !_boardCached)
        {
            return;
        }

        _boardCached = cached;
        foreach (var card in _panelCards.Values)
        {
            var wanted = cached && !ReferenceEquals(card, except);
            if (wanted && card.CacheMode is null)
            {
                card.CacheMode = new BitmapCache();
            }
            else if (!wanted && card.CacheMode is not null)
            {
                card.CacheMode = null;
            }
        }
    }

    private bool StepReflowSprings(double dt)
    {
        var moving = false;
        foreach (var (key, springs) in _reflowSprings)
        {
            if (springs.X.IsSettled && springs.Y.IsSettled)
            {
                continue;
            }

            springs.X.Step(dt);
            springs.Y.Step(dt);
            if (_panelCards.TryGetValue(key, out var card) && !card.IsFloating)
            {
                card.SetReflowOffset(springs.X.Value, springs.Y.Value);
            }

            moving |= !(springs.X.IsSettled && springs.Y.IsSettled);
        }

        return moving;
    }

    private bool StepPlaceholder(double dt)
    {
        if (_placeholderX.IsSettled && _placeholderY.IsSettled && _placeholderW.IsSettled && _placeholderH.IsSettled)
        {
            return false;
        }

        _placeholderX.Step(dt);
        _placeholderY.Step(dt);
        _placeholderW.Step(dt);
        _placeholderH.Step(dt);
        ApplyPlaceholderGeometry();
        return true;
    }

    private bool StepFloatSettle(double dt)
    {
        if (_floatSettle is not { } settle)
        {
            return false;
        }

        settle.X.Step(dt);
        settle.Y.Step(dt);
        settle.W.Step(dt);
        settle.H.Step(dt);
        settle.Card.SetFloatGeometry(settle.X.Value, settle.Y.Value, settle.W.Value, settle.H.Value);

        if (settle.X.IsSettled && settle.Y.IsSettled && settle.W.IsSettled && settle.H.IsSettled)
        {
            CompleteFloatSettle();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Scrolls when the pointer nears the top or bottom of the viewport during a drag, faster
    /// the closer it gets - without it, a panel could never be moved further than one screen.
    /// </summary>
    private void AutoScrollForGesture(LayoutGestureState gesture, double dt)
    {
        const double zone = 56;
        const double maxSpeed = 1400;

        var viewportTop = _scrollHost.TransformToVisual(null).TransformPoint(new Point(0, 0)).Y;
        var height = _scrollHost.ViewportHeight;
        if (height <= zone * 2)
        {
            return;
        }

        var y = gesture.LatestWindowPoint.Y - viewportTop;
        double speed = 0;
        if (y < zone)
        {
            var depth = Math.Clamp((zone - y) / zone, 0, 1);
            speed = -maxSpeed * depth * depth;
        }
        else if (y > height - zone)
        {
            var depth = Math.Clamp((y - (height - zone)) / zone, 0, 1);
            speed = maxSpeed * depth * depth;
        }

        if (speed == 0)
        {
            return;
        }

        var next = Math.Clamp(_scrollHost.VerticalOffset + (speed * dt), 0, _scrollHost.ScrollableHeight);
        if (Math.Abs(next - _scrollHost.VerticalOffset) >= 0.5)
        {
            _scrollHost.ChangeView(null, next, null, disableAnimation: true);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static PanelLayout MapDisplayTargetToSaved(
        PanelLayout displayOrigin,
        PanelLayout savedOrigin,
        PanelLayout displayTarget)
        => new PanelLayout(
            savedOrigin.X + displayTarget.X - displayOrigin.X,
            savedOrigin.Y + displayTarget.Y - displayOrigin.Y,
            savedOrigin.W + displayTarget.W - displayOrigin.W,
            savedOrigin.H + displayTarget.H - displayOrigin.H).Normalize();

    private static bool LayoutsEqual(PanelLayout? left, PanelLayout? right)
        => left is not null
            && right is not null
            && left.X == right.X
            && left.Y == right.Y
            && left.W == right.W
            && left.H == right.H;

    private static void FadeElementTo(UIElement element, double to, double milliseconds)
    {
        if (!Motion.AnimationsEnabled)
        {
            element.Opacity = to;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }
}
