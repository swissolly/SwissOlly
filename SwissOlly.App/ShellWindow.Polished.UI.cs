using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;
using SwissOlly.App.Controls;
using SwissOlly.App.ViewModels;
using SwissOlly.Core.Models;

namespace SwissOlly.App;

public sealed partial class ShellWindow
{
    private Grid BuildLoadingScreen()
    {
        // Ultra-lightweight — no resource lookups, no helper methods, renders on first frame
        var accentColor = ResolveBrush("AccentBrush", "#79B8FF");
        var ld1 = new Ellipse { Width = 8, Height = 8, Fill = accentColor };
        var ld2 = new Ellipse { Width = 8, Height = 8, Fill = accentColor, Opacity = 0.5 };
        var ld3 = new Ellipse { Width = 8, Height = 8, Fill = accentColor, Opacity = 0.25 };

        var loadingPulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        foreach (var (dot, delay) in new[] { (ld1, 0), (ld2, 200), (ld3, 400) })
        {
            var anim = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delay) };
            anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0.25 });
            anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), Value = 1.0 });
            anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), Value = 0.25 });
            anim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), Value = 0.25 });
            Storyboard.SetTarget(anim, dot);
            Storyboard.SetTargetProperty(anim, "Opacity");
            loadingPulse.Children.Add(anim);
        }

        _loadingOverlay = new Border
        {
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 14,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children = { ld1, ld2, ld3 },
                    },
                    new TextBlock
                    {
                        Text = "Starting SwissOlly",
                        FontSize = 13,
                        Foreground = ResolveBrush("TextSecondaryBrush", "#C4C8CE"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        _loadingOverlay.Loaded += (_, _) => loadingPulse.Begin();

        var root = new Grid
        {
            Background = ResolveBrush("AppBackdropBrush", "#111315"),
        };
        root.Children.Add(_loadingOverlay);

        // After this screen renders, build the full UI on the next frame
        root.Loaded += (_, _) =>
        {
            _ = _uiQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, BuildFullUi);
        };

        return root;
    }

    private Grid BuildRootLayout()
    {
        StartupTrace.Write("BuildRootLayout // polished-v19");

        var shellBorder = new Border
        {
            Background = BrushFactory.CreateBrush("#00FFFFFF"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0, 12, 0, 12),
            Child = BuildShellStack(),
        };

        var root = new Grid
        {
            Background = ResolveBrush("AppBackdropBrush", "#111315"),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(_titleBarDragHost);
        var appHeader = BuildHeader();
        appHeader.Margin = new Thickness(PageGutter, 4, PageGutter, 8);
        _appHeader = appHeader;
        root.Children.Add(appHeader);
        Grid.SetRow(appHeader, 1);

        _shellBorderRef = shellBorder;
        _scrollHost.Content = new Border
        {
            Margin = new Thickness(PageGutter, 4, PageGutter, 16),
            Child = shellBorder,
        };
        root.Children.Add(_scrollHost);
        Grid.SetRow(_scrollHost, 2);

        // Over the scroll host, so Apply floats above the page rather than scrolling with it.
        var applyBar = BuildSettingsApplyBar();
        root.Children.Add(applyBar);
        Grid.SetRow(applyBar, 2);

        root.Loaded += OnRootLoaded;
        root.SizeChanged += OnRootLayoutSizeChanged;
        return root;
    }

    private StackPanel BuildShellStack()
    {
        StartupTrace.Write("BuildShellStack // two-view-shell");
        _overviewView.Children.Add(BuildSummarySurface());
        _overviewView.Children.Add(BuildBoardSectionBand());
        _overviewView.Children.Add(_dashboardGrid);

        return new StackPanel
        {
            Spacing = SectionSpacing,
            Children =
            {
                _overviewView,
                _settingsView,
            },
        };
    }

    private const double StatusColumnWidth = 320;

    /// <summary>Brand, title and the Overview/Settings pill need about this much beside the status.</summary>
    private const double HeaderFixedContentWidth = 470;

    private ColumnDefinition? _statusColumn;

    private void UpdateHeaderStatusWidth()
    {
        if (_statusColumn is null || _rootLayout.ActualWidth <= 0)
        {
            return;
        }

        var available = _rootLayout.ActualWidth - (PageGutter * 2) - HeaderFixedContentWidth;
        var width = Math.Clamp(available, 0, StatusColumnWidth);
        if (Math.Abs(_statusColumn.Width.Value - width) > 0.5)
        {
            _statusColumn.Width = new GridLength(width);
            _statusText.MaxWidth = Math.Max(0, width - 20);
        }
    }

    private FrameworkElement BuildHeader()
    {
        StartupTrace.Write("BuildHeader // two-view-shell");
        var heroGrid = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(0, 0, 0, 2),
        };
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Fixed, not Auto: the status line changes length constantly ("Applying theme", "Saving
        // settings", "Last sample ..."), and as an Auto column every change shoved the
        // Overview/Settings pill sideways - the jitter seen when pressing Apply. Narrow windows
        // shrink it (see UpdateHeaderStatusWidth) so it trims instead of running off the edge.
        _statusColumn = new ColumnDefinition { Width = new GridLength(StatusColumnWidth) };
        heroGrid.ColumnDefinitions.Add(_statusColumn);
        heroGrid.Children.Add(_brandHost);

        var titleText = new TextBlock
        {
            Text = "SwissOlly",
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
        };
        var titleStack = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                titleText,
            },
        };
        heroGrid.Children.Add(titleStack);
        Grid.SetColumn(titleStack, 1);

        var navigationSurface = new Border
        {
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            BorderThickness = new Thickness(1),
            // Concentric with the 8px chips it wraps.
            CornerRadius = new CornerRadius(UiMetrics.Enclosing(UiMetrics.ControlRadius, 3)),
            Padding = new Thickness(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    _overviewNavigationButton,
                    _settingsNavigationButton,
                },
            },
        };
        ToolTipService.SetToolTip(_overviewNavigationButton, "View live system telemetry (Ctrl+1)");
        ToolTipService.SetToolTip(_settingsNavigationButton, "Configure SwissOlly (Ctrl+2)");
        Grid.SetColumn(navigationSurface, 2);
        heroGrid.Children.Add(navigationSurface);

        var statusSurface = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            // Wider than the TextBlock's own MaxWidth so the text is the constraining element.
            // When the panel was the narrower of the two it clipped mid-glyph ("41 metr") instead
            // of letting TextTrimming produce an ellipsis.
            MaxWidth = StatusColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _statusIndicator,
                _statusText,
            },
        };
        Grid.SetColumn(statusSurface, 3);
        heroGrid.Children.Add(statusSurface);
        return heroGrid;
    }

    private Border BuildControlsSurface()
    {
        StartupTrace.Write("BuildControlsSurface // two-view-shell");
        var root = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                _controlsBodyHost,
            },
        };

        var surface = new Border
        {
            Background = BrushFactory.CreateBrush("#00FFFFFF"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Child = root,
        };

        _controlsSurfaceRef = surface;
        return surface;
    }

    private FrameworkElement BuildThemePicker()
    {
        // Dark themes, then light, each a grid of previews. Both grids get the same width, so they
        // pick the same column count and the light cards line up under the dark ones.
        _themeCards.Clear();
        var groups = new StackPanel { Spacing = 8 };
        foreach (var (title, isLight) in new[] { ("Dark", false), ("Light", true) })
        {
            var cards = new UniformWrapPanel
            {
                MinItemWidth = 150,
                MaxColumns = 8,
                ColumnSpacing = 10,
                RowSpacing = 10,
                Margin = new Thickness(0, 0, 0, isLight ? 0 : 8),
                // One Tab stop per group; the arrow keys move between themes.
                TabFocusNavigation = KeyboardNavigationMode.Once,
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
            };
            foreach (var theme in ThemeCatalog.All.Where(theme => theme.IsLight == isLight))
            {
                var card = new ThemePreviewCard(theme);
                card.Click += OnThemeCardClick;
                _themeCards[theme.Mode] = card;
                _settingsBindings.Add(() => card.IsSelected = _draftThemeMode == theme.Mode);
                cards.Children.Add(card);
            }

            groups.Children.Add(CreateFieldLabel(title));
            groups.Children.Add(cards);
        }

        return groups;
    }

    /// <summary>Themes whose surfaces are light, so XAML controls need the Light base theme.</summary>
    internal static bool IsLightTheme(ThemeMode mode) => ThemeCatalog.Get(mode).IsLight;

    // ── Settings page ──────────────────────────────────────────────────────

    /// <summary>Updates for the Settings controls on screen, registered as the page is built.</summary>
    private readonly List<Action> _settingsBindings = [];
    private readonly Dictionary<ThemeMode, ThemePreviewCard> _themeCards = [];
    private bool _refreshingSettingsDeck;
    private ThemeMode? _refocusThemeCard;

    /// <summary>
    /// Builds the Settings page. Only a new theme or a new column count rebuilds it; every other
    /// change goes through <see cref="RefreshSettingsDeck"/>.
    /// </summary>
    private void RenderEditableControlDeck()
    {
        StartupTrace.Write("RenderEditableControlDeck // polished-v19");
        _settingsBindings.Clear();
        var settingsColumns = DetermineSettingsColumns();
        _lastSettingsColumnCount = settingsColumns;

        var themeCard = CreateControlEditorCard(
            "appearance",
            "THEME",
            () => ThemeCatalog.Get(_draftThemeMode).Label,
            () => ThemeCatalog.Get(_draftThemeMode).Description,
            BuildThemePicker());

        var scrapeBox = CreateBoundEntry(
            () => _draftScrapeIntervalInput, value => _draftScrapeIntervalInput = value, "2", "Sample every, in seconds");
        scrapeBox.Width = 160;
        scrapeBox.HorizontalAlignment = HorizontalAlignment.Left;
        var collectionCard = CreateControlEditorCard(
            "collection",
            "COLLECTION",
            // An invalid entry says so; it used to show the 2-second default as if that were set.
            () => IsScrapeDraftInvalid() ? "Not a valid interval" : FormatScrapeInterval(GetDraftScrapeIntervalSeconds()),
            () => IsScrapeDraftInvalid() ? "Use whole seconds from 1 to 60"
                : string.IsNullOrWhiteSpace(_draftScrapeIntervalInput) ? "Default" : "Custom interval",
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    CreateLabeledField("Sample every (seconds)", scrapeBox, "1–60 seconds. Leave blank for the default 2s."),
                    CreateChipWrapRow(
                        CreateBoundChip("1s", () => IsDraftScrapeInterval(1), () => SetScrapeInterval(1)),
                        CreateBoundChip("2s", () => IsDraftScrapeInterval(2), ResetScrapeInterval),
                        CreateBoundChip("5s", () => IsDraftScrapeInterval(5), () => SetScrapeInterval(5)),
                        CreateBoundChip("10s", () => IsDraftScrapeInterval(10), () => SetScrapeInterval(10)),
                        CreateBoundChip("15s", () => IsDraftScrapeInterval(15), () => SetScrapeInterval(15))),
                },
            });

        var retentionBox = CreateBoundEntry(
            () => _draftRetentionInput, value => _draftRetentionInput = value, "24h", "Keep history for");
        retentionBox.Width = 160;
        retentionBox.HorizontalAlignment = HorizontalAlignment.Left;
        // Spelled out like the collection card's "2 seconds"; "1d" sat oddly above a field and a
        // chip that both said 24h.
        var retentionCard = CreateControlEditorCard(
            "retention",
            "RETENTION",
            () => SwissOllyConfig.TryParseRetentionWindow(_draftRetentionInput, out var draftRetentionWindow, out _)
                ? FormatDuration(draftRetentionWindow)
                : string.IsNullOrWhiteSpace(_draftRetentionInput)
                    ? FormatDuration(TimeSpan.FromHours(SwissOllyConfig.DefaultMaxRetentionHours))
                    : "Not a valid length",
            () => !string.IsNullOrWhiteSpace(_draftRetentionInput) && !SwissOllyConfig.TryParseRetentionWindow(_draftRetentionInput, out _, out _)
                ? "Use m, h or d, like 30m, 24h or 7d"
                : string.IsNullOrWhiteSpace(_draftRetentionInput) ? "Default · older history is deleted" : "Older history is deleted",
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    CreateLabeledField("Keep history for", retentionBox, "Like 30m, 24h or 7d, up to 10 years."),
                    CreateChipWrapRow(
                        CreateBoundChip("6h", () => IsDraftRetentionHours(6), () => SetRetentionInput("6h")),
                        CreateBoundChip("12h", () => IsDraftRetentionHours(12), () => SetRetentionInput("12h")),
                        CreateBoundChip("24h", () => IsDraftRetentionHours(24), ResetRetentionHours),
                        CreateBoundChip("7d", () => IsDraftRetentionHours(168), () => SetRetentionInput("7d")),
                        CreateBoundChip("30d", () => IsDraftRetentionHours(720), () => SetRetentionInput("30d")),
                        CreateBoundChip("90d", () => IsDraftRetentionHours(2160), () => SetRetentionInput("90d")),
                        CreateBoundChip("1y", () => IsDraftRetentionHours(8760), () => SetRetentionInput("365d"))),
                },
            });

        var storageBox = CreateBoundEntry(
            () => _draftStorageDirectory, value => _draftStorageDirectory = value, SwissOllyConfig.DefaultStorageDirectory, "History folder");
        var storageField = CreateLabeledField(
            "Folder",
            storageBox,
            "Leave blank for the default location. A different folder starts its own history; nothing is moved.");
        storageField.AllowDrop = true;
        storageField.DragOver += OnStoragePathDragOver;
        storageField.Drop += OnStoragePathDrop;
        var storageCard = CreateControlEditorCard(
            "storage",
            "HISTORY FOLDER",
            () => GetStorageFieldTitle(_draftStorageDirectory),
            () => string.IsNullOrWhiteSpace(_draftStorageDirectory) ? "Kept on this PC, in your app data" : "Kept in the folder below",
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    storageField,
                    CreateChipWrapRow(
                        CreateActionChip("Browse folder", OnBrowseStorageClick, filled: true),
                        CreateActionChip("Use default", (_, _) => ResetStorageDirectory())),
                },
            });

        // Cards flow left to right; the last one stretches across what is left of its row.
        var dataGrid = new Grid { ColumnSpacing = PanelSpacing, RowSpacing = PanelSpacing };
        for (var column = 0; column < settingsColumns; column++)
        {
            dataGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var dataCards = new[] { collectionCard, retentionCard, storageCard };
        for (var index = 0; index < dataCards.Length; index++)
        {
            var column = index % settingsColumns;
            var span = index == dataCards.Length - 1 ? settingsColumns - column : 1;
            AddCardToGrid(dataGrid, index / settingsColumns, column, dataCards[index], columnSpan: span);
        }

        StartupTrace.Write("RenderEditableControlDeck // assign host");
        // Grouped by what the settings do, and each group says when its changes take effect: a
        // theme or the window behaviour applies on click, the rest waits for Apply.
        _controlsBodyHost.Child = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateSettingsSectionHeader("Appearance", "Applies as soon as you pick a theme"),
                themeCard,
                CreateSettingsSectionHeader("Data and history", "Saved when you press Apply"),
                dataGrid,
                CreateSettingsSectionHeader("In-game overlay", "Saved when you press Apply"),
                BuildOverlayEditorCard(wide: settingsColumns >= 2),
                CreateSettingsSectionHeader("Startup and closing", "Applies as soon as you choose"),
                BuildWindowBehaviorCard(wide: settingsColumns >= 2),
            },
        };

        RefreshSettingsDeck();
        RestoreThemeCardFocus();
        StartupTrace.Write("RenderEditableControlDeck // complete");
    }

    /// <summary>
    /// Pushes the drafts into the Settings controls already on screen: chip selections, headlines,
    /// field text, the overlay preview. Every Settings click comes through here. Each click used to
    /// rebuild the whole page instead, 80-130ms in a Debug build, which made the chips feel slow
    /// and dropped hover and keyboard focus every time.
    /// </summary>
    private void RefreshSettingsDeck()
    {
        // Setting a field's text raises its TextChanged, which lands back here mid-pass.
        if (_refreshingSettingsDeck)
        {
            return;
        }

        _refreshingSettingsDeck = true;
        try
        {
            foreach (var binding in _settingsBindings)
            {
                binding();
            }
        }
        finally
        {
            _refreshingSettingsDeck = false;
        }

        UpdateSettingsActionState();
    }

    /// <summary>A preset chip whose selection follows the drafts.</summary>
    private ActionChip CreateBoundChip(string text, Func<bool> isSelected, Action select)
    {
        var chip = CreatePresetChip(text, isSelected(), (_, _) =>
        {
            select();
            // An edit answers a rejected Apply; its message stayed up until Apply was pressed again.
            _settingsProblem = null;
            RefreshSettingsDeck();
        });
        _settingsBindings.Add(() => chip.IsActive = isSelected());
        return chip;
    }

    private TextBlock BindText(TextBlock block, Func<string> text)
    {
        block.Text = text();
        _settingsBindings.Add(() =>
        {
            var value = text();
            if (!string.Equals(block.Text, value, StringComparison.Ordinal))
            {
                block.Text = value;
            }
        });
        return block;
    }

    /// <summary>
    /// A field showing its draft. Typing updates the draft (and the chips and headline with it); a
    /// chip, Browse or Revert that changes the draft updates the field.
    /// </summary>
    private InlineTextEntry CreateBoundEntry(Func<string> draft, Action<string> setDraft, string placeholder, string accessibleLabel)
    {
        var entry = CreateDeckInlineEntry(draft(), placeholder);
        entry.AccessibleLabel = accessibleLabel;
        entry.TextChanged += (_, _) =>
        {
            // Only a real edit clears a rejection; the deck setting the text back is not one.
            if (!_refreshingSettingsDeck)
            {
                _settingsProblem = null;
            }

            setDraft(entry.Text);
            RefreshSettingsDeck();
        };
        entry.Submitted += OnSettingsEntrySubmitted;
        _settingsBindings.Add(() =>
        {
            var value = draft() ?? string.Empty;
            if (!string.Equals(entry.Text, value, StringComparison.Ordinal))
            {
                entry.Text = value;
            }
        });
        return entry;
    }

    private static StackPanel CreateLabeledField(string label, FrameworkElement field, string hint) =>
        new()
        {
            Spacing = 8,
            Children = { CreateFieldLabel(label), field, CreateMutedText(hint, 12) },
        };

    /// <summary>A Settings group title with, beside it, when that group's changes take effect.</summary>
    private static Grid CreateSettingsSectionHeader(string title, string hint)
    {
        // Baseline-aligned like the Performance band on the overview: both trimmed to their
        // baseline and bottom-aligned, so 20px and 12px text share one line. Larger than the card
        // headlines below it (18px), which outranked their own section at 16px.
        var titleText = CreatePrimaryText(title, 20, true);
        titleText.TextLineBounds = TextLineBounds.TrimToBaseline;
        titleText.VerticalAlignment = VerticalAlignment.Bottom;
        var hintText = CreateMutedText(hint, 12);
        hintText.TextLineBounds = TextLineBounds.TrimToBaseline;
        hintText.VerticalAlignment = VerticalAlignment.Bottom;
        hintText.TextWrapping = TextWrapping.NoWrap;
        hintText.TextTrimming = TextTrimming.CharacterEllipsis;

        var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(2, 12, 0, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(titleText);
        header.Children.Add(hintText);
        Grid.SetColumn(hintText, 1);
        return header;
    }

    private bool IsScrapeDraftInvalid() =>
        !string.IsNullOrWhiteSpace(_draftScrapeIntervalInput) && !TryNormalizeScrapeInput(_draftScrapeIntervalInput, out _);

    private bool IsDraftScrapeInterval(int seconds) =>
        string.IsNullOrWhiteSpace(_draftScrapeIntervalInput)
            ? seconds == SwissOllyConfig.DefaultScrapeIntervalSeconds
            : int.TryParse(_draftScrapeIntervalInput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed == seconds;

    private bool IsDraftRetentionHours(int hours) =>
        string.IsNullOrWhiteSpace(_draftRetentionInput)
            ? hours == SwissOllyConfig.DefaultMaxRetentionHours
            : MainViewModel.TryParseRetentionInput(_draftRetentionInput, out var parsed, out _) && parsed == hours;

    /// <summary>
    /// A theme rebuilds this page in the new palette. A keyboard user's focus moves to the same
    /// theme's new card instead of dropping back to the window.
    /// </summary>
    private void RestoreThemeCardFocus()
    {
        if (_refocusThemeCard is not { } mode)
        {
            return;
        }

        _refocusThemeCard = null;
        if (_themeCards.TryGetValue(mode, out var card))
        {
            _ = _uiQueue.TryEnqueue(DispatcherQueuePriority.Low, () => card.Focus(FocusState.Keyboard));
        }
    }

    private void OnThemeCardClick(object? sender, EventArgs e)
    {
        if (sender is not ThemePreviewCard card || card.Theme.Mode == _draftThemeMode)
        {
            return;
        }

        // A theme has nothing to validate, so it applies and saves on click. As a draft it
        // looked applied, then silently reverted on the next launch unless you also found Apply.
        var mode = card.Theme.Mode;
        _draftThemeMode = mode;
        _viewModel.SelectedTheme = mode;
        RefreshSettingsDeck();
        _refocusThemeCard = card.FocusState == FocusState.Keyboard ? mode : null;
        App.CurrentApp.ApplyTheme(mode);
        // The bar says "Saved" once the save lands: with no Apply to press, a theme change looked
        // like it might not stick.
        _announceSettingsSave = true;
        DebouncePersistLayout();
    }

    private UIElement BuildSummarySurface()
    {
        StartupTrace.Write("BuildSummarySurface // quiet-strip");
        var surface = new Border
        {
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Child = _summaryHost,
        };
        _summarySurfaceRef = surface;
        return surface;
    }

    private static void AddCardToGrid(Grid grid, int row, int column, FrameworkElement element, int columnSpan = 1)
    {
        while (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        grid.Children.Add(element);
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private Border CreateSettingFieldCard(string title, string value, string caption, bool isActive, EventHandler onClick)
    {
        var token = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            BorderBrush = ResolveBrush(isActive ? "AccentStrongBrush" : "SurfaceStrokeBrush", isActive ? "#B7F7FF" : "#27425E"),
            BorderThickness = new Thickness(1),
            Background = ResolveBrush("SurfaceInsetBrush", "#141619"),
            Child = new TextBlock
            {
                Text = title.Length >= 3 ? title[..3].ToUpperInvariant() : title.ToUpperInvariant(),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveBrush(isActive ? "AccentStrongBrush" : "AccentBrush", isActive ? "#B7F7FF" : "#66E7FF"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.Children.Add(token);
        var titleStack = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            Spacing = 6,
            Children =
            {
                CreateLabelText(title, 11),
                CreatePrimaryText(value, 17, true),
            },
        };
        topGrid.Children.Add(titleStack);
        Grid.SetColumn(titleStack, 1);

        var border = new Border
        {
            Background = ResolveBrush(isActive ? "SurfaceStrongBrush" : "SurfaceElevatedBrush", isActive ? "#292D31" : "#222529"),
            BorderBrush = ResolveBrush(isActive ? "AccentStrongBrush" : "SurfaceStrokeBrush", isActive ? "#B7F7FF" : "#27425E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    topGrid,
                    CreateSecondaryText(caption, 12),
                },
            },
        };

        border.PointerEntered += (_, _) =>
        {
            if (!isActive)
            {
                border.Background = ResolveBrush("SurfaceStrongBrush", "#292D31");
                border.BorderBrush = ResolveBrush("AccentHaloBrush", "#1B68DAFF");
            }
        };
        border.PointerExited += (_, _) =>
        {
            if (!isActive)
            {
                border.Background = ResolveBrush("SurfaceElevatedBrush", "#222529");
                border.BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E");
            }
        };
        border.Tapped += (_, _) => onClick(border, EventArgs.Empty);
        return border;
    }

    /// <summary>A row of chips that wraps onto a second line in a narrow card instead of clipping.</summary>
    private static FlowPanel CreateChipWrapRow(params ActionChip[] chips)
    {
        var row = new FlowPanel
        {
            Spacing = 8,
            RowSpacing = 8,
        };

        foreach (var chip in chips)
        {
            row.Children.Add(DetachFromParent(chip));
        }

        return row;
    }

    /// <summary>
    /// Removes an element from whatever currently owns it so it can be added somewhere new.
    ///
    /// The time-range editor is rebuilt on every open but reuses the same persistent chips. Closing
    /// it only detaches the editor from the window - the chips stay children of the old, now
    /// orphaned rows until the GC happens to collect them. Re-adding a chip that still has a parent
    /// throws COMException 0x800F1000 ("No installed components were detected"), so reopening the
    /// editor crashed or not depending purely on GC timing.
    /// </summary>
    private static T DetachFromParent<T>(T element) where T : FrameworkElement
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Border border when ReferenceEquals(border.Child, element):
                border.Child = null;
                break;
            case ContentControl control when ReferenceEquals(control.Content, element):
                control.Content = null;
                break;
        }

        return element;
    }

    private static ActionChip CreatePresetChip(string text, bool isActive, EventHandler onClick)
    {
        // Selection is IsActive alone. Passing it as "filled" too, as this once did, left the
        // first selection lit for good once chips updated in place instead of being rebuilt: two
        // chips in one group showed as chosen.
        var chip = CreateActionChip(text, onClick);
        chip.IsActive = isActive;
        chip.MinWidth = 44;
        return chip;
    }

    private async void OnStoragePathDrop(object sender, DragEventArgs e)
    {
        try
        {
            string? path = null;

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var item = items.FirstOrDefault();
                if (item is not null)
                {
                    path = item.Path;
                }
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                path = text.Trim().Trim('"');
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                StartupTrace.Write($"Storage path dropped: {path}");
                SetStorageDirectory(path);
            }
        }
        catch (Exception ex)
        {
            StartupTrace.WriteException("StoragePathDrop", ex);
            _viewModel.SetStatus($"Storage path issue: {ex.Message}", StatusSeverity.Issue);
            UpdateStatusText();
        }
    }

    private static void OnStoragePathDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private static StackPanel CreateChipRow(params FrameworkElement[] chips)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        foreach (var chip in chips)
        {
            stack.Children.Add(DetachFromParent(chip));
        }

        return stack;
    }

    private static ActionChip CreateActionChip(string text, EventHandler onClick, bool filled = false)
    {
        var chip = new ActionChip
        {
            Text = text,
            IsFilled = filled,
            MinHeight = 36,
            MinWidth = filled ? 112 : 0,
        };
        chip.Click += onClick;
        return chip;
    }

    private ActionChip CreateGlobalRangeChip(string text, int minutes)
    {
        var chip = CreateActionChip(text, OnGlobalWindowRangeClick);
        chip.Tag = minutes;
        chip.MinWidth = 48;
        return chip;
    }

    private static InlineTextEntry CreateDeckInlineEntry(string text, string placeholderText) =>
        new()
        {
            Text = text,
            PlaceholderText = placeholderText,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

    private static TextBlock CreateInlineLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
        };

    private static Grid CreateBrandPlaceholder() =>
        new()
        {
            Width = 44,
            Height = 44,
        };

    private static Border CreatePlaceholderCard(string title, string text) =>
        new()
        {
            Background = ResolveBrush("SurfaceElevatedBrush", "#222529"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    CreateLabelText(title, 11),
                    CreatePrimaryText(text, 15, true),
                },
            },
        };

    private static Border CreateSectionHeader(string eyebrow, string text) =>
        new()
        {
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    CreatePrimaryText(eyebrow, 16, true),
                    CreateSecondaryText(text, 12.5),
                },
            },
        };

    private StackPanel BuildBoardSectionBand()
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 2, 0, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The edit-mode instructions live beside the title they describe. They used to go to the
        // status line, a 300px caption at the far end of the window that truncated them.
        _editModeHint = CreateSecondaryText(BrowseModeHintText, 12);
        // Baseline-aligned with the title: both trimmed to their baseline and bottom-aligned, so
        // 12px and 18px text share one line. Centring them vertically put the hint visibly low.
        _editModeHint.VerticalAlignment = VerticalAlignment.Bottom;
        _editModeHint.TextLineBounds = TextLineBounds.TrimToBaseline;
        _editModeHint.TextTrimming = TextTrimming.CharacterEllipsis;
        _editModeHint.TextWrapping = TextWrapping.NoWrap;
        _editModeHint.IsHitTestVisible = false;
        var boardTitle = CreatePrimaryText("Performance", 18, true);
        boardTitle.VerticalAlignment = VerticalAlignment.Bottom;
        boardTitle.TextLineBounds = TextLineBounds.TrimToBaseline;
        // A grid, not a horizontal StackPanel: a StackPanel measures its children at infinite
        // width, so the hint never trimmed and ran underneath the range and layout buttons on a
        // narrow window. In a star column it ellipsizes, and the full text is in the tooltip.
        var titleRow = new Grid
        {
            ColumnSpacing = 16,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.Children.Add(boardTitle);
        titleRow.Children.Add(_editModeHint);
        Grid.SetColumn(_editModeHint, 1);
        _editModeHint.IsTextTrimmedChanged += (sender, _) =>
            ToolTipService.SetToolTip(sender, sender.IsTextTrimmed ? sender.Text : null);
        _editModeHint.IsHitTestVisible = true;
        grid.Children.Add(titleRow);

        // Edit/Done is the trailing control so the mode toggle never moves: Done appears exactly
        // where Edit was, with Cancel beside it.
        var actions = CreateChipRow(
            _globalResetZoomButton,
            _globalRangeButton,
            _cancelLayoutButton,
            _editLayoutButton);
        actions.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(actions);
        Grid.SetColumn(actions, 1);

        return new StackPanel
        {
            Spacing = 0,
            Children =
            {
                grid,
            },
        };
    }

    private static Grid CreateSectionBand(string title, string subtitle)
    {
        return new Grid
        {
            Margin = new Thickness(0, 2, 0, 0),
            Children =
            {
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        CreatePrimaryText(title, 17, true),
                        CreateSecondaryText(subtitle, 12.5),
                    },
                },
            },
        };
    }

    /// <summary>A Settings card: icon, eyebrow, a headline and caption that follow the drafts, then its controls.</summary>
    private Border CreateControlEditorCard(string iconKey, string eyebrow, Func<string> headline, Func<string> caption, FrameworkElement body)
    {
        // The palette accent: per-card hexes were legacy cyans that ignored the theme, so Ember,
        // Violet and Sandstone still showed cyan icons in Settings.
        var accentBrush = ResolveBrush("AccentBrush", "#79B8FF");
        var token = IconFactory.CreateTile(iconKey, accentBrush, 40, 16);

        var headerGrid = new Grid
        {
            ColumnSpacing = 11,
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.Children.Add(token);

        var labelStack = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                CreateLabelText(eyebrow, 11),
                BindText(CreatePrimaryText(string.Empty, 18, true), headline),
            },
        };
        headerGrid.Children.Add(labelStack);
        Grid.SetColumn(labelStack, 1);

        var contentStack = new Grid();
        contentStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        contentStack.Children.Add(headerGrid);
        var captionText = BindText(CreateSecondaryText(string.Empty, 11), caption);
        captionText.Margin = new Thickness(0, 12, 0, 0);
        contentStack.Children.Add(captionText);
        Grid.SetRow(captionText, 1);

        var bodyContainer = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = body,
        };
        contentStack.Children.Add(bodyContainer);
        Grid.SetRow(bodyContainer, 2);

        return new Border
        {
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = contentStack,
        };
    }

    private static Border CreateTopStatusPill(UIElement content) =>
        new()
        {
            Background = ResolveBrush("PanelOverlayBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2A4662"),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            Child = new Grid
            {
                Children =
                {
                    content,
                },
            },
        };

    private static Border CreateMiniBrandMark()
    {
        var host = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(12),
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#315274"),
            BorderThickness = new Thickness(UiMetrics.Hairline),
        };

        var imagePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "swissolly-256.png");
        if (!File.Exists(imagePath))
        {
            host.Child = new Grid
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "S",
                        FontFamily = new FontFamily("Segoe UI Variable Display"),
                        FontSize = 15,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ResolveBrush("AccentBrush", "#66E7FF"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            return host;
        }

        host.Padding = new Thickness(5);
        host.Child = new Grid
        {
            Children =
            {
                new Border
                {
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(9),
                    Background = ResolveBrush("AppBackdropBrush", "#061018"),
                    BorderBrush = ResolveBrush("AccentSoftBrush", "#163B60"),
                    BorderThickness = new Thickness(1),
                },
                new Microsoft.UI.Xaml.Controls.Image
                {
                    Stretch = Stretch.Uniform,
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { UriSource = new Uri(imagePath) },
                    Margin = new Thickness(7),
                },
            },
        };
        return host;
    }

    private static Border CreateInfoPanel(string title, string value) =>
        new()
        {
            Background = ResolveBrush("SurfaceInsetBrush", "#091321"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(UiMetrics.Hairline),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    CreateMutedText(title, 11),
                    CreatePrimaryText(value, 13, true),
                },
            },
        };

    private static TextBlock CreatePrimaryText(string text, double fontSize, bool semiBold) =>
        new()
        {
            Text = text,
            FontFamily = new FontFamily(fontSize >= 28 ? "Segoe UI Variable Display" : "Segoe UI Variable Text"),
            FontSize = fontSize,
            FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
            TextWrapping = TextWrapping.WrapWholeWords,
        };

    private static TextBlock CreateSecondaryText(string text, double fontSize = 14) =>
        new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = fontSize,
            Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1"),
            TextWrapping = TextWrapping.WrapWholeWords,
        };

    private static TextBlock CreateMutedText(string text, double fontSize = 12) =>
        new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = fontSize,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
            TextWrapping = TextWrapping.WrapWholeWords,
        };

    private static TextBlock CreateLabelText(string text, double fontSize) =>
        new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontWeight = FontWeights.Medium,
            FontSize = fontSize,
            Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1"),
        };

    private static TextBlock CreateFieldLabel(string text)
    {
        return CreateMutedText(text, 11);
    }

    private static Brush ResolveBrush(string key, string fallbackHex) => ThemeVisuals.ResolveBrush(key, fallbackHex);

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);

    private static Color ParseColor(string hex) => ThemeVisuals.ParseColor(hex);

    /// <summary>
    /// Start with Windows and what closing the window does. Both apply on click, like the theme:
    /// there is nothing to validate, and a draft here would only look applied.
    /// </summary>
    private Border BuildWindowBehaviorCard(bool wide)
    {
        var startupGroup = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("Start with Windows"),
                CreateChipWrapRow(
                    CreateBoundChip("Off", () => !_viewModel.LaunchOnStartup, () => SetStartWithWindows(false)),
                    CreateBoundChip("On", () => _viewModel.LaunchOnStartup, () => SetStartWithWindows(true))),
                BindText(CreateMutedText(string.Empty, 12), () => _viewModel.MinimizeToTray
                    ? "When on, SwissOlly starts in the notification area as you sign in."
                    : "When on, SwissOlly opens as you sign in."),
            },
        };

        var closeGroup = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("When you close the window"),
                CreateChipWrapRow(
                    CreateBoundChip("Keep running", () => _viewModel.MinimizeToTray, () => SetKeepRunningInTray(true)),
                    CreateBoundChip("Exit", () => !_viewModel.MinimizeToTray, () => SetKeepRunningInTray(false))),
                BindText(CreateMutedText(string.Empty, 12), () => _viewModel.MinimizeToTray
                    ? "Keeps recording history from the notification area."
                    : "History stops recording until you open SwissOlly again."),
            },
        };

        FrameworkElement body;
        if (wide)
        {
            var grid = new Grid { ColumnSpacing = 32 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(startupGroup);
            grid.Children.Add(closeGroup);
            Grid.SetColumn(closeGroup, 1);
            body = grid;
        }
        else
        {
            body = new StackPanel { Spacing = 14, Children = { startupGroup, closeGroup } };
        }

        return CreateControlEditorCard(
            "window",
            "WINDOW",
            () => _viewModel.LaunchOnStartup ? "Starts with Windows" : "Starts when you open it",
            () => _viewModel.MinimizeToTray ? "Closing keeps it running in the tray" : "Closing exits SwissOlly",
            body);
    }

    // ── Overlay card ───────────────────────────────────────────────────────

    private const string DefaultOverlayHotkey = "Ctrl+Shift+F11";
    private const string DefaultOverlayColor = "#66E7FF";

    /// <summary>
    /// The overlay's metrics in the order it draws them. Samples are padded with figure spaces
    /// exactly as OverlayController formats them, and left-aligned in one column as the renderer
    /// draws them, so "62°C" sticks out past "23%" here as it does in a game.
    /// </summary>
    private static readonly (string Key, string Chip, string Label, string Sample)[] OverlayMetricChoices =
    [
        ("fps", "FPS", "FPS", " 144"),
        ("cpu", "CPU %", "CPU", " 23%"),
        // Over the 90% line on purpose: the preview shows the red a hot reading turns.
        ("gpu", "GPU %", "GPU", " 92%"),
        ("gpu-temp", "GPU temp", "GPU Temp", " 62°C"),
        ("memory", "Memory %", "MEM", " 48%"),
    ];

    private static readonly (string Name, string Hex)[] OverlayClassicColors =
    [
        ("Ice", DefaultOverlayColor),
        ("Mint", "#7CE38B"),
        ("Amber", "#F5B54A"),
        ("Coral", "#FF6B6B"),
        ("Lavender", "#B9A3FF"),
        ("White", "#FFFFFF"),
    ];

    /// <summary>Fully saturated colours that stay readable over bright, busy game scenes.</summary>
    private static readonly (string Name, string Hex)[] OverlayNeonColors =
    [
        ("Neon cyan", "#00F0FF"),
        ("Electric blue", "#2F7BFF"),
        ("Ultraviolet", "#9D4DFF"),
        ("Neon magenta", "#FF2BD6"),
        ("Hot pink", "#FF3B8D"),
        ("Neon red", "#FF2D4A"),
        ("Neon orange", "#FF7B1C"),
        ("Laser yellow", "#FFF01F"),
        ("Electric lime", "#BFFF00"),
        ("Neon green", "#39FF14"),
    ];

    /// <summary>
    /// What the overlay shows at the top; where it sits and how big it is on the left; how it
    /// looks on the right, beside a live preview of the overlay over a stand-in game scene.
    /// </summary>
    private Border BuildOverlayEditorCard(bool wide)
    {
        var showGroup = CreateSettingGroup("Overlay", CreateChipWrapRow(
            CreateBoundChip("Off", () => !_draftOverlayEnabled, () => _draftOverlayEnabled = false),
            CreateBoundChip("On", () => _draftOverlayEnabled, () => _draftOverlayEnabled = true)));

        var contentGroup = CreateSettingGroup("Shows", CreateChipWrapRow(
            CreateBoundChip("FPS only", () => _draftOverlayMode == OverlayMode.Simple, () => _draftOverlayMode = OverlayMode.Simple),
            CreateBoundChip("All metrics", () => _draftOverlayMode == OverlayMode.Advanced, () => _draftOverlayMode = OverlayMode.Advanced),
            CreateBoundChip("Custom", () => _draftOverlayMode == OverlayMode.Custom, () => _draftOverlayMode = OverlayMode.Custom)));

        var metricsGroup = CreateSettingGroup(
            "Metrics",
            CreateChipWrapRow([.. OverlayMetricChoices.Select(choice => CreateBoundChip(
                choice.Chip,
                () => _draftOverlayCustomMetrics.Contains(choice.Key, StringComparer.OrdinalIgnoreCase),
                () => ToggleOverlayMetric(choice.Key)))]),
            "Pick at least one. The overlay lists them in this order.");
        _settingsBindings.Add(() => metricsGroup.Visibility =
            _draftOverlayMode == OverlayMode.Custom ? Visibility.Visible : Visibility.Collapsed);

        var positionGroup = CreateSettingGroup("Position", CreateChipWrapRow(
            CreateBoundChip("Top left", () => _draftOverlayCorner == OverlayCorner.TopLeft, () => _draftOverlayCorner = OverlayCorner.TopLeft),
            CreateBoundChip("Top right", () => _draftOverlayCorner == OverlayCorner.TopRight, () => _draftOverlayCorner = OverlayCorner.TopRight),
            CreateBoundChip("Bottom left", () => _draftOverlayCorner == OverlayCorner.BottomLeft, () => _draftOverlayCorner = OverlayCorner.BottomLeft),
            CreateBoundChip("Bottom right", () => _draftOverlayCorner == OverlayCorner.BottomRight, () => _draftOverlayCorner = OverlayCorner.BottomRight)));

        var fontBox = CreateBoundEntry(() => _draftOverlayFontSize, value => _draftOverlayFontSize = value, "14", "Overlay text size in pixels");
        fontBox.Width = 120;
        fontBox.HorizontalAlignment = HorizontalAlignment.Left;
        var sizeGroup = CreateSettingGroup("Text size (10–32 px)", fontBox, CreateChipWrapRow(
            CreateBoundChip("12", () => IsDraftNumber(_draftOverlayFontSize, "px", 12), () => _draftOverlayFontSize = "12"),
            CreateBoundChip("14", () => IsDraftNumber(_draftOverlayFontSize, "px", 14), () => _draftOverlayFontSize = "14"),
            CreateBoundChip("16", () => IsDraftNumber(_draftOverlayFontSize, "px", 16), () => _draftOverlayFontSize = "16"),
            CreateBoundChip("20", () => IsDraftNumber(_draftOverlayFontSize, "px", 20), () => _draftOverlayFontSize = "20")));

        var opacityBox = CreateBoundEntry(() => _draftOverlayOpacity, value => _draftOverlayOpacity = value, "90", "Overlay opacity in percent");
        opacityBox.Width = 120;
        opacityBox.HorizontalAlignment = HorizontalAlignment.Left;
        var opacityGroup = CreateSettingGroup("Opacity (30–100%)", opacityBox, CreateChipWrapRow(
            CreateBoundChip("60%", () => IsDraftNumber(_draftOverlayOpacity, "%", 60), () => _draftOverlayOpacity = "60"),
            CreateBoundChip("75%", () => IsDraftNumber(_draftOverlayOpacity, "%", 75), () => _draftOverlayOpacity = "75"),
            CreateBoundChip("90%", () => IsDraftNumber(_draftOverlayOpacity, "%", 90), () => _draftOverlayOpacity = "90"),
            CreateBoundChip("100%", () => IsDraftNumber(_draftOverlayOpacity, "%", 100), () => _draftOverlayOpacity = "100")));

        var hotkeyBox = CreateBoundEntry(() => _draftOverlayHotkey, value => _draftOverlayHotkey = value, DefaultOverlayHotkey, "Overlay show or hide hotkey");
        hotkeyBox.Width = 220;
        var hotkeyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                hotkeyBox,
                CreateActionChip("Default", (_, _) =>
                {
                    _draftOverlayHotkey = DefaultOverlayHotkey;
                    RefreshSettingsDeck();
                }),
            },
        };
        var hotkeyGroup = CreateSettingGroup(
            "Show or hide hotkey",
            hotkeyRow,
            CreateMutedText("Ctrl, Alt, Shift or Win, plus a letter, digit or F1–F24.", 12));

        // Size and opacity side by side: they are read together, and each is short.
        var sizeAndOpacity = new Grid { ColumnSpacing = 24 };
        sizeAndOpacity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeAndOpacity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeAndOpacity.Children.Add(sizeGroup);
        sizeAndOpacity.Children.Add(opacityGroup);
        Grid.SetColumn(opacityGroup, 1);

        var placement = new StackPanel { Spacing = 18, Children = { positionGroup, sizeAndOpacity, hotkeyGroup } };
        var looks = new StackPanel { Spacing = 18, Children = { BuildOverlayPreview(), BuildOverlayColorGroup() } };

        FrameworkElement details;
        if (wide)
        {
            var columns = new Grid { ColumnSpacing = 32 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.Children.Add(placement);
            columns.Children.Add(looks);
            Grid.SetColumn(looks, 1);
            details = columns;
        }
        else
        {
            details = new StackPanel { Spacing = 18, Children = { looks, placement } };
        }

        var body = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                new FlowPanel { Spacing = 32, RowSpacing = 14, Children = { showGroup, contentGroup } },
                metricsGroup,
                details,
            },
        };

        return CreateControlEditorCard(
            "overlay",
            "OVERLAY",
            () => !_draftOverlayEnabled
                ? "Off"
                : _draftOverlayMode switch
                {
                    OverlayMode.Simple => "FPS",
                    OverlayMode.Custom => DescribeCustomMetrics(),
                    _ => "FPS + 4 metrics",
                },
            () => _draftOverlayEnabled ? "Shows while a game has focus" : "FPS and usage over your games",
            body);
    }

    /// <summary>A label above its controls, with an optional hint below.</summary>
    private static StackPanel CreateSettingGroup(string label, FrameworkElement control, FrameworkElement? below = null)
    {
        var group = new StackPanel { Spacing = 8, Children = { CreateFieldLabel(label), control } };
        if (below is not null)
        {
            group.Children.Add(below);
        }

        return group;
    }

    private static StackPanel CreateSettingGroup(string label, FrameworkElement control, string hint) =>
        CreateSettingGroup(label, control, CreateMutedText(hint, 12));

    /// <summary>"FPS + 2 metrics", in the same terms as "FPS + 4 metrics" for all of them.</summary>
    private string DescribeCustomMetrics()
    {
        var count = _draftOverlayCustomMetrics.Count;
        var hasFps = _draftOverlayCustomMetrics.Contains("fps", StringComparer.OrdinalIgnoreCase);
        var others = hasFps ? count - 1 : count;
        return (hasFps, others) switch
        {
            (_, 0) when !hasFps => "No metrics",
            (true, 0) => "FPS",
            (true, 1) => "FPS + 1 metric",
            (true, _) => $"FPS + {others} metrics",
            (false, 1) => "1 metric",
            _ => $"{others} metrics",
        };
    }

    private static bool IsDraftNumber(string? draft, string unit, int value) =>
        TryParseDraftNumber(draft, unit, out var parsed) && parsed == value;

    private static bool IsDraftText(string? draft, string value) =>
        string.Equals(draft?.Trim(), value, StringComparison.OrdinalIgnoreCase);

    private void ToggleOverlayMetric(string key)
    {
        var selected = new HashSet<string>(_draftOverlayCustomMetrics, StringComparer.OrdinalIgnoreCase);
        if (!selected.Remove(key))
        {
            selected.Add(key);
        }

        // Kept in the chips' order, which is the order the overlay draws them. Appending put
        // them in click order, so the hint that promised this order was wrong.
        _draftOverlayCustomMetrics = [.. OverlayMetricChoices.Select(choice => choice.Key).Where(selected.Contains)];
    }

    /// <summary>The colour being drafted, or the default while the field holds something else.</summary>
    private Color GetDraftOverlayColor() =>
        SwissOlly.Core.Models.OverlayConfig.NormalizeHexColor(_draftOverlayColor) is { } hex
            ? ParseColor(hex)
            : ParseColor(DefaultOverlayColor);

    private StackPanel BuildOverlayColorGroup()
    {
        var current = new Ellipse
        {
            Width = 22,
            Height = 22,
            Stroke = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            StrokeThickness = UiMetrics.Hairline,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var currentBrush = new SolidColorBrush(GetDraftOverlayColor());
        current.Fill = currentBrush;
        _settingsBindings.Add(() => currentBrush.Color = GetDraftOverlayColor());

        // A hex field, not WinUI's ColorPicker: its template needs control resources this build
        // doesn't ship (TabViewScrollButtonBackground), and opening it fail-fast crashed the app.
        var hexBox = CreateBoundEntry(() => _draftOverlayColor, value => _draftOverlayColor = value, DefaultOverlayColor, "Overlay text color as a hex value");
        hexBox.Width = 120;
        var currentRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { current, hexBox },
        };

        FlowPanel SwatchRow((string Name, string Hex)[] colors)
        {
            var row = new FlowPanel { Spacing = 2, RowSpacing = 2 };
            foreach (var (name, hex) in colors)
            {
                var swatch = new ColorSwatchButton(name, hex);
                swatch.Click += (_, _) =>
                {
                    _draftOverlayColor = hex;
                    RefreshSettingsDeck();
                };
                _settingsBindings.Add(() => swatch.IsSelected = IsDraftText(_draftOverlayColor, hex));
                row.Children.Add(swatch);
            }

            return row;
        }

        var classic = SwatchRow(OverlayClassicColors);
        var neon = SwatchRow(OverlayNeonColors);

        // Usage readings recolour themselves past these thresholds, so a warm pick hides the
        // warning. Said here, and louder when it applies, rather than discovered mid-game.
        var thresholdHint = CreateMutedText(string.Empty, 12);
        _settingsBindings.Add(() =>
        {
            var clashes = IsWarningLikeColor(GetDraftOverlayColor());
            SetTextIfChanged(thresholdHint, clashes
                ? "With a red, orange or yellow text color, the amber and red warnings for high usage and GPU temperature won't stand out."
                : "CPU, GPU and memory turn amber above 75% and red above 90%, like GPU in the preview; GPU temperature does the same above 75 °C and 85 °C.");
            thresholdHint.Foreground = clashes
                ? ResolveBrush("WarningBrush", "#F2BF66")
                : ResolveBrush("TextMutedBrush", "#90959D");
        });
        // One Tab stop per row; the arrow keys move between colours.
        foreach (var row in new[] { classic, neon })
        {
            row.TabFocusNavigation = KeyboardNavigationMode.Once;
            row.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        }

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("Text color"),
                currentRow,
                CreateFieldLabel("Classic"),
                classic,
                CreateFieldLabel("Neon"),
                neon,
                thresholdHint,
            },
        };
    }

    /// <summary>Red, orange or yellow: a text colour the overlay's amber and red warnings would blend into.</summary>
    private static bool IsWarningLikeColor(Color color)
    {
        var (hue, saturation, _) = ThemeVisuals.ToHsl(color);
        var degrees = hue * 360;
        return saturation >= 0.45 && (degrees <= 65 || degrees >= 345);
    }

    /// <summary>
    /// The overlay as it will look: the drafted metrics, colour, size, opacity and corner over a
    /// stand-in game scene. Values are fixed samples; labels sit back at 72% like the real thing.
    /// </summary>
    private FrameworkElement BuildOverlayPreview()
    {
        Brush Vertical(params (string Hex, double Offset)[] stops)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
            };
            foreach (var (hex, offset) in stops)
            {
                brush.GradientStops.Add(new GradientStop { Color = ParseColor(hex), Offset = offset });
            }

            return brush;
        }

        var horizonGlow = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        horizonGlow.GradientStops.Add(new GradientStop { Color = ParseColor("#007C93C4"), Offset = 0 });
        horizonGlow.GradientStops.Add(new GradientStop { Color = ParseColor("#AA9FB4E0"), Offset = 0.5 });
        horizonGlow.GradientStops.Add(new GradientStop { Color = ParseColor("#007C93C4"), Offset = 1 });

        var text = new Grid { Margin = new Thickness(10), IsHitTestVisible = false };
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Fixed colours, like the scene: it is a game, not part of the theme.
        var offBadge = new Border
        {
            Background = BrushFactory.CreateBrush("#B0080A0C"),
            CornerRadius = new CornerRadius(UiMetrics.SmallRadius),
            Padding = new Thickness(8, 3, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Overlay is off",
                FontSize = 12,
                Foreground = BrushFactory.CreateBrush("#E8ECF4"),
            },
        };

        var scene = new Grid
        {
            // A minimum, not a height: five lines at 32px need more, and a fixed height cut off
            // the last of them.
            MinHeight = 148,
            Children =
            {
                // Sky, then ground below a soft horizon: enough of a scene to judge colours on.
                new Border { CornerRadius = new CornerRadius(UiMetrics.ControlRadius), Background = Vertical(("#56709E", 0), ("#2C3A58", 0.6), ("#1A2234", 1)) },
                new Border
                {
                    Height = 54,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(0, 0, UiMetrics.ControlRadius, UiMetrics.ControlRadius),
                    Background = Vertical(("#1A2130", 0), ("#0C0F15", 1)),
                },
                new Border { Height = 1, Margin = new Thickness(0, 0, 0, 54), VerticalAlignment = VerticalAlignment.Bottom, Background = horizonGlow },
                text,
                offBadge,
            },
        };
        AutomationProperties.SetName(scene, "Overlay preview");

        string? lastSignature = null;
        var lastValidFontPx = 14;
        var lastValidOpacity = 0.9;
        _settingsBindings.Add(() =>
        {
            List<string> keys = _draftOverlayMode switch
            {
                OverlayMode.Simple => ["fps"],
                OverlayMode.Custom => _draftOverlayCustomMetrics.Count > 0 ? _draftOverlayCustomMetrics : ["fps"],
                _ => [.. OverlayMetricChoices.Select(choice => choice.Key)],
            };
            var color = GetDraftOverlayColor();
            // The overlay sizes its text in physical pixels; at 150% scaling, 14px is 9.3 DIPs.
            var scale = _rootLayout.XamlRoot?.RasterizationScale ?? 1d;
            if (scale <= 0)
            {
                scale = 1d;
            }

            // While a field holds something invalid, the preview keeps the last valid value rather
            // than quietly falling back to the default.
            if (TryParseDraftNumber(_draftOverlayFontSize, "px", out var px) && px is >= 10 and <= 32)
            {
                lastValidFontPx = px;
            }

            if (TryParseDraftNumber(_draftOverlayOpacity, "%", out var pct) && pct is >= 30 and <= 100)
            {
                lastValidOpacity = pct / 100d;
            }

            var fontPx = lastValidFontPx;
            var opacity = lastValidOpacity;
            var signature = $"{string.Join(',', keys)}|{color}|{fontPx}|{scale}|{opacity}|{_draftOverlayCorner}|{_draftOverlayEnabled}";
            if (signature == lastSignature)
            {
                return;
            }

            lastSignature = signature;
            text.HorizontalAlignment = _draftOverlayCorner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
            text.VerticalAlignment = _draftOverlayCorner is OverlayCorner.TopLeft or OverlayCorner.TopRight
                ? VerticalAlignment.Top
                : VerticalAlignment.Bottom;
            text.Opacity = _draftOverlayEnabled ? 1 : 0.3;
            offBadge.Visibility = _draftOverlayEnabled ? Visibility.Collapsed : Visibility.Visible;
            text.ColumnSpacing = 8 / scale;
            text.RowSpacing = 4 / scale;

            text.Children.Clear();
            text.RowDefinitions.Clear();
            var valueBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B));
            // The overlay's over-90% red (OverlayController.CriticalColor).
            var criticalBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(255 * opacity), 0xFF, 0x5A, 0x4F));
            var labelBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(255 * opacity * 0.72), color.R, color.G, color.B));
            var outlineBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(220 * opacity), 6, 10, 16));
            var row = 0;
            foreach (var key in keys)
            {
                var metric = Array.Find(OverlayMetricChoices, choice => string.Equals(choice.Key, key, StringComparison.OrdinalIgnoreCase));
                if (metric.Label is null)
                {
                    continue;
                }

                text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddPreviewText(text, metric.Label, row, 0, fontPx / scale, labelBrush, outlineBrush);
                AddPreviewText(text, metric.Sample, row, 1, fontPx / scale, metric.Key == "gpu" ? criticalBrush : valueBrush, outlineBrush);
                row++;
            }
        });

        return new StackPanel { Spacing = 8, Children = { CreateFieldLabel("Preview"), scene } };
    }

    /// <summary>One overlay word: a dark copy a pixel down and right, the overlay's outline in miniature, then the text.</summary>
    private static void AddPreviewText(Grid host, string value, int row, int column, double fontSize, Brush foreground, Brush outline)
    {
        foreach (var (brush, offset) in new[] { (outline, 1d), (foreground, 0d) })
        {
            var block = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = fontSize,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = offset == 0 ? null : new TranslateTransform { X = offset, Y = offset },
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            host.Children.Add(block);
        }
    }

    private string GetStorageSummaryText()
    {
        return string.IsNullOrWhiteSpace(_viewModel.StorageDirectory)
            ? "%LocalAppData%\\SwissOlly\\Data"
            : _viewModel.StorageDirectory.Trim();
    }

    private string GetStorageFieldTitle()
    {
        return GetStorageFieldTitle(_viewModel.StorageDirectory);
    }

    private static string GetStorageFieldTitle(string? storageDirectory)
    {
        return string.IsNullOrWhiteSpace(storageDirectory)
            ? "Local app data"
            : "Custom folder";
    }

    private string GetRetentionFieldCaption()
    {
        if (string.IsNullOrWhiteSpace(_viewModel.RetentionHoursInput))
        {
            return "Type 30m, 24h, or 7d";
        }

        return _viewModel.HasValidRetentionInput
            ? "Custom retention active"
            : "Use m, h, or d only";
    }

    private string GetRetentionFieldValue()
    {
        return GetRetentionFieldValue(_viewModel.RetentionHoursInput);
    }

    private string GetRetentionFieldValue(string? retentionInput)
    {
        if (string.IsNullOrWhiteSpace(retentionInput))
        {
            return MainViewModel.FormatRetentionInput(SwissOllyConfig.DefaultMaxRetentionHours);
        }

        return MainViewModel.TryParseRetentionInput(retentionInput, out _, out var normalizedText)
            ? normalizedText
            : retentionInput.Trim();
    }

    private static string FormatScrapeInterval(int seconds) =>
        seconds == 1 ? "1 second" : $"{seconds} seconds";

    private static string FormatRetention(int hours)
    {
        if (hours % 24 == 0)
        {
            var days = hours / 24;
            return days == 1 ? "1 day" : $"{days} days";
        }

        return hours == 1 ? "1 hour" : $"{hours} hours";
    }

    private string GetBehaviorSummaryText()
    {
        var startup = _viewModel.LaunchOnStartup ? "Auto-start" : "Manual start";
        var tray = _viewModel.MinimizeToTray ? "Close to tray" : "Close to exit";
        return $"{startup} · {tray}";
    }

}
