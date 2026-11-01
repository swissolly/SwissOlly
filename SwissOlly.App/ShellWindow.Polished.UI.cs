using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Data;
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
        // Overview/Settings pill sideways - the jitter seen when pressing Apply.
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatusColumnWidth) });
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
            CornerRadius = new CornerRadius(10),
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
        // All eight themes on one row when the section spans the full window (it does at the
        // three-column breakpoint); four per row below that, where eight would not fit. On one
        // row the choice reads as a single strip instead of a block filling a quarter of a card.
        //
        // Deliberately rows of StackPanels rather than a Grid with star-width columns: this
        // picker is measured by a parent that offers infinite width, and star columns resolve to
        // zero under an infinite constraint — which collapsed the entire settings view to a
        // ~195px sliver. Content-sized panels measure correctly under any constraint.
        var columns = DetermineSettingsColumns() >= 3 ? 8 : 4;
        var modes = Enum.GetValues<ThemeMode>();
        var options = new StackPanel { Spacing = PanelSpacing };
        StackPanel? currentRow = null;

        var slot = 0;
        foreach (var mode in modes)
        {
            if (slot % columns == 0)
            {
                currentRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = PanelSpacing,
                };
                options.Children.Add(currentRow);
            }

            var swatchColors = GetThemeSwatch(mode);
            var swatches = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 3,
                Children =
                {
                    new Border { Width = 18, Height = 16, CornerRadius = new CornerRadius(3), Background = BrushFactory.CreateBrush(swatchColors[0]), BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"), BorderThickness = new Thickness(0.6) },
                    new Border { Width = 18, Height = 16, CornerRadius = new CornerRadius(3), Background = BrushFactory.CreateBrush(swatchColors[1]), BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"), BorderThickness = new Thickness(0.6) },
                    new Border { Width = 18, Height = 16, CornerRadius = new CornerRadius(3), Background = BrushFactory.CreateBrush(swatchColors[2]), BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"), BorderThickness = new Thickness(0.6) },
                },
            };
            var choice = new ActionChip
            {
                Text = GetThemeLabel(mode),
                Tag = mode,
                MinHeight = 36,
                MinWidth = 104,
                IsActive = _draftThemeMode == mode,
                IsFilled = _draftThemeMode == mode,
            };
            choice.Click += OnThemeChoiceClick;
            ToolTipService.SetToolTip(choice, $"Preview {GetThemeLabel(mode)} theme");

            currentRow!.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children = { swatches, choice },
            });
            slot++;
        }

        return options;
    }

    private static string GetThemeLabel(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "Light",
        ThemeMode.Midnight => "Midnight",
        ThemeMode.Ember => "Ember",
        ThemeMode.Forest => "Forest",
        ThemeMode.Violet => "Violet",
        ThemeMode.Nord => "Nord",
        ThemeMode.Sandstone => "Sandstone",
        _ => "Dark",
    };

    // Backdrop / card / accent, mirroring each palette in App.ApplyThemeResources so the
    // swatch previews what the theme actually looks like.
    private static string[] GetThemeSwatch(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ["#F2F3F5", "#FFFFFF", "#1769C2"],
        ThemeMode.Midnight => ["#070B14", "#101728", "#5CC8F5"],
        ThemeMode.Ember => ["#14100E", "#1F1915", "#F0A04B"],
        ThemeMode.Forest => ["#0D120F", "#161D19", "#6FD8A4"],
        ThemeMode.Violet => ["#110E16", "#1C1726", "#B18AF0"],
        ThemeMode.Nord => ["#2E3440", "#3B4252", "#88C0D0"],
        ThemeMode.Sandstone => ["#EDE9E3", "#FAF8F5", "#3F6B5C"],
        _ => ["#111315", "#1C1F22", "#79B8FF"],
    };

    /// <summary>Themes whose surfaces are light, so XAML controls need the Light base theme.</summary>
    internal static bool IsLightTheme(ThemeMode mode)
        => mode is ThemeMode.Light or ThemeMode.Sandstone;

    private void RenderEditableControlDeck()
    {
        StartupTrace.Write("RenderEditableControlDeck // polished-v19");
        _controlDeckEditableActive = true;

        var scrapeBox = CreateDeckInlineEntry(
            string.IsNullOrWhiteSpace(_draftScrapeIntervalInput) ? string.Empty : _draftScrapeIntervalInput,
            "2");
        scrapeBox.Width = 160;
        scrapeBox.HorizontalAlignment = HorizontalAlignment.Left;
        scrapeBox.TextChanged += OnScrapeInputChanged;

        var retentionBox = CreateDeckInlineEntry(_draftRetentionInput, "24h");
        retentionBox.Width = 160;
        retentionBox.HorizontalAlignment = HorizontalAlignment.Left;
        retentionBox.TextChanged += OnRetentionInputChanged;

        var storageBox = CreateDeckInlineEntry(
            string.IsNullOrWhiteSpace(_draftStorageDirectory) ? string.Empty : _draftStorageDirectory.Trim(),
            SwissOllyConfig.DefaultStorageDirectory);
        storageBox.TextChanged += OnStorageInputChanged;

        var storageFieldStack = new StackPanel
        {
            Spacing = 8,
            AllowDrop = true,
            Children =
            {
                CreateFieldLabel("Storage directory"),
                storageBox,
                CreateMutedText("Leave blank for the default location.", 12),
            },
        };
        storageFieldStack.DragOver += OnStoragePathDragOver;
        storageFieldStack.Drop += OnStoragePathDrop;

        var fieldGrid = new Grid
        {
            ColumnSpacing = PanelSpacing,
            RowSpacing = PanelSpacing,
        };
        var settingsColumns = DetermineSettingsColumns();
        _lastSettingsColumnCount = settingsColumns;
        for (var column = 0; column < settingsColumns; column++)
        {
            fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        AddCardToGrid(fieldGrid, 0, 0, CreateControlEditorCard(
            "appearance",
            "APPEARANCE",
            GetThemeLabel(_draftThemeMode),
            "Application appearance",
            "#79DCE5",
            BuildThemePicker()), columnSpan: settingsColumns);

        AddCardToGrid(fieldGrid, 1, 0, CreateControlEditorCard(
            "collection",
            "COLLECTION",
            FormatScrapeInterval(GetDraftScrapeIntervalSeconds()),
            string.IsNullOrWhiteSpace(_draftScrapeIntervalInput) ? "Live default" : "Custom cadence",
            "#5DE6FF",
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            CreateFieldLabel("Scrape interval (seconds)"),
                            scrapeBox,
                            CreateMutedText("1–60 seconds. Leave blank for the default 2s.", 12),
                        },
                    },
                    CreateChipWrapRow(
                        CreatePresetChip("1s", GetDraftScrapeIntervalSeconds() == 1, (_, _) => SetScrapeInterval(1)),
                        CreatePresetChip("2s", string.IsNullOrWhiteSpace(_draftScrapeIntervalInput), (_, _) => ResetScrapeInterval()),
                        CreatePresetChip("5s", GetDraftScrapeIntervalSeconds() == 5, (_, _) => SetScrapeInterval(5)),
                        CreatePresetChip("10s", GetDraftScrapeIntervalSeconds() == 10, (_, _) => SetScrapeInterval(10)),
                        CreatePresetChip("15s", GetDraftScrapeIntervalSeconds() == 15, (_, _) => SetScrapeInterval(15))),
                },
            }));

        AddCardToGrid(fieldGrid, 1 + (1 / settingsColumns), 1 % settingsColumns, CreateControlEditorCard(
            "retention",
            "RETENTION",
            GetRetentionFieldValue(_draftRetentionInput),
            string.IsNullOrWhiteSpace(_draftRetentionInput) ? "Compact local history" : "Custom retention active",
            "#67B7FF",
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            CreateFieldLabel("Retention window"),
                            retentionBox,
                            CreateMutedText("Format: 30m, 24h, or 7d.", 12),
                        },
                    },
                    CreateChipWrapRow(
                        CreatePresetChip("6h", GetDraftRetentionHours() == 6, (_, _) => SetRetentionInput("6h")),
                        CreatePresetChip("12h", GetDraftRetentionHours() == 12, (_, _) => SetRetentionInput("12h")),
                        CreatePresetChip("24h", GetDraftRetentionHours() == 24, (_, _) => ResetRetentionHours()),
                        CreatePresetChip("7d", GetDraftRetentionHours() == 168, (_, _) => SetRetentionInput("7d")),
                        CreatePresetChip("30d", GetDraftRetentionHours() == 720, (_, _) => SetRetentionInput("30d")),
                        CreatePresetChip("90d", GetDraftRetentionHours() == 2160, (_, _) => SetRetentionInput("90d"))),
                },
            }));

        AddCardToGrid(fieldGrid, 1 + (2 / settingsColumns), 2 % settingsColumns, CreateControlEditorCard(
            "storage",
            "STORAGE PATH",
            GetStorageFieldTitle(_draftStorageDirectory),
            "Stored locally by default",
            "#6EE7C8",
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    storageFieldStack,
                    CreateChipWrapRow(
                        CreateActionChip("Browse folder", OnBrowseStorageClick, filled: true),
                        CreateActionChip("Use default", (_, _) => ResetStorageDirectory())),
                },
            }));

        // ── Row 1: full-width overlay card ────────────────────────────────
        var overlayRow = 1 + ((3 + settingsColumns - 1) / settingsColumns);
        AddCardToGrid(fieldGrid, overlayRow, 0, BuildOverlayEditorCard(), columnSpan: settingsColumns);

        var actionRow = new Grid
        {
            ColumnSpacing = PanelSpacing,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actionChips = CreateChipRow(
            CreateActionChip("Revert", OnCancelSettingsClick),
            CreateActionChip("Apply", OnSaveSettingsClick, filled: true));
        actionRow.Children.Add(actionChips);
        Grid.SetColumn(actionChips, 1);

        StartupTrace.Write("RenderEditableControlDeck // assign host");
        _controlsBodyHost.Child = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                fieldGrid,
                actionRow,
            },
        };
        StartupTrace.Write("RenderEditableControlDeck // complete");
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

    private static StackPanel CreateChipWrapRow(params ActionChip[] chips)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
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
        var chip = CreateActionChip(text, onClick, isActive);
        chip.IsActive = isActive;
        // Width must not depend on selection. CreateActionChip widens filled chips for primary
        // buttons, so a preset grew 60px the moment it was chosen and shoved its row sideways.
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
        chip.MinHeight = 0;
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
        grid.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                boardTitle,
                _editModeHint,
            },
        });

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

    private static Border CreateControlEditorCard(string iconKey, string eyebrow, string headline, string caption, string accentHex, FrameworkElement body)
    {
        // The palette accent, not the per-card hex: those were legacy cyans that ignored the
        // theme, so Ember, Violet and Sandstone still showed cyan icons in Settings.
        var accentBrush = ResolveBrush("AccentBrush", accentHex);
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
                CreatePrimaryText(headline, 18, true),
            },
        };
        headerGrid.Children.Add(labelStack);
        Grid.SetColumn(labelStack, 1);

        var contentStack = new Grid();
        contentStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        contentStack.Children.Add(headerGrid);
        var captionText = CreateSecondaryText(caption, 11);
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
            BorderThickness = new Thickness(0.9),
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
            BorderThickness = new Thickness(0.8),
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
            BorderThickness = new Thickness(0.9),
        };

        var imagePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "swissolly.png");
        if (!File.Exists(imagePath))
        {
            host.Child = new Grid
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "V",
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
            BorderThickness = new Thickness(0.9),
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

    // ── Overlay card helpers ───────────────────────────────────────────────

    private Border BuildOverlayEditorCard()
    {
        // Row 1: enable toggle + mode segmented chips
        var enableChips = CreateChipWrapRow(
            CreatePresetChip("Off", !_draftOverlayEnabled,
                (_, _) => { _draftOverlayEnabled = false; QueueEditableControlDeckRender(); }),
            CreatePresetChip("On", _draftOverlayEnabled,
                (_, _) => { _draftOverlayEnabled = true; QueueEditableControlDeckRender(); }));

        var modeChips = CreateChipWrapRow(
            CreatePresetChip("Simple", _draftOverlayMode == OverlayMode.Simple,
                (_, _) => { _draftOverlayMode = OverlayMode.Simple; QueueEditableControlDeckRender(); }),
            CreatePresetChip("Advanced", _draftOverlayMode == OverlayMode.Advanced,
                (_, _) => { _draftOverlayMode = OverlayMode.Advanced; QueueEditableControlDeckRender(); }),
            CreatePresetChip("Custom", _draftOverlayMode == OverlayMode.Custom,
                (_, _) => { _draftOverlayMode = OverlayMode.Custom; QueueEditableControlDeckRender(); }));

        var row1 = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { enableChips, modeChips },
                },
                CreateMutedText("Simple: FPS only  ·  Advanced: all five metrics  ·  Custom: pick your own", 11),
            },
        };

        // Row 2: custom metric picker (only when mode = Custom)
        var contentStack = new StackPanel { Spacing = 14, Children = { row1 } };

        if (_draftOverlayMode == OverlayMode.Custom)
        {
            var allMetrics = new[] { ("fps", "FPS"), ("cpu", "CPU%"), ("gpu", "GPU%"), ("gpu-temp", "GPU temp"), ("memory", "Memory%") };
            var metricChips = allMetrics.Select(kv =>
            {
                var key = kv.Item1;
                var isActive = _draftOverlayCustomMetrics.Contains(key, StringComparer.OrdinalIgnoreCase);
                return CreatePresetChip(kv.Item2, isActive, (_, _) =>
                {
                    if (_draftOverlayCustomMetrics.Contains(key, StringComparer.OrdinalIgnoreCase))
                        _draftOverlayCustomMetrics.Remove(key);
                    else
                        _draftOverlayCustomMetrics.Add(key);
                    QueueEditableControlDeckRender();
                });
            }).ToArray();

            contentStack.Children.Add(new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    CreateChipWrapRow(metricChips),
                    CreateMutedText("Pick at least one. Order in overlay matches order shown here.", 11),
                },
            });
        }

        // Row 3: corner + color
        var swatchFill = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(ParseColor(_draftOverlayColor)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hexLabel = new TextBlock
        {
            Text = _draftOverlayColor.ToUpperInvariant(),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveBrush("TextSecondaryBrush", "#B7CCE1"),
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
        };
        var colorPicker = new ColorPicker
        {
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            IsAlphaEnabled = false,
            IsHexInputVisible = true,
            IsColorPreviewVisible = true,
            IsColorSliderVisible = true,
            Color = ParseColor(_draftOverlayColor),
        };
        colorPicker.ColorChanged += (_, e) =>
        {
            _draftOverlayColor = $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}";
            swatchFill.Background = new SolidColorBrush(e.NewColor);
            hexLabel.Text = _draftOverlayColor.ToUpperInvariant();
        };
        var swatchButton = new Button
        {
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(8),
            Flyout = new Flyout { Content = colorPicker },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { swatchFill, hexLabel },
            },
        };

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var presetHex in OverlayColorPresets)
        {
            var presetColor = ParseColor(presetHex);
            var presetButton = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(presetColor),
                BorderThickness = new Thickness(1),
                BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            };
            presetButton.Click += (_, _) =>
            {
                _draftOverlayColor = presetHex;
                swatchFill.Background = new SolidColorBrush(presetColor);
                hexLabel.Text = presetHex.ToUpperInvariant();
                colorPicker.Color = presetColor;
            };
            presetRow.Children.Add(presetButton);
        }

        var cornerChips = CreateChipWrapRow(
            CreatePresetChip("↖ TL", _draftOverlayCorner == OverlayCorner.TopLeft,
                (_, _) => { _draftOverlayCorner = OverlayCorner.TopLeft; QueueEditableControlDeckRender(); }),
            CreatePresetChip("↗ TR", _draftOverlayCorner == OverlayCorner.TopRight,
                (_, _) => { _draftOverlayCorner = OverlayCorner.TopRight; QueueEditableControlDeckRender(); }),
            CreatePresetChip("↙ BL", _draftOverlayCorner == OverlayCorner.BottomLeft,
                (_, _) => { _draftOverlayCorner = OverlayCorner.BottomLeft; QueueEditableControlDeckRender(); }),
            CreatePresetChip("↘ BR", _draftOverlayCorner == OverlayCorner.BottomRight,
                (_, _) => { _draftOverlayCorner = OverlayCorner.BottomRight; QueueEditableControlDeckRender(); }));

        var row3Grid = new Grid { ColumnSpacing = PanelSpacing };
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row3Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cornerStack = new StackPanel { Spacing = 8, Children = { CreateFieldLabel("Position"), cornerChips } };
        row3Grid.Children.Add(cornerStack);

        var colorStack = new StackPanel { Spacing = 8, Children = { CreateFieldLabel("Text color"), swatchButton, presetRow } };
        row3Grid.Children.Add(colorStack);
        Grid.SetColumn(colorStack, 1);

        contentStack.Children.Add(row3Grid);

        // Row 4: font size + opacity
        var fontBox = CreateDeckInlineEntry(_draftOverlayFontSize, "14");
        fontBox.Width = 120;
        fontBox.HorizontalAlignment = HorizontalAlignment.Left;
        fontBox.TextChanged += (_, _) => { _draftOverlayFontSize = fontBox.Text; };

        var opacityBox = CreateDeckInlineEntry(_draftOverlayOpacity, "90");
        opacityBox.Width = 120;
        opacityBox.HorizontalAlignment = HorizontalAlignment.Left;
        opacityBox.TextChanged += (_, _) => { _draftOverlayOpacity = opacityBox.Text; };

        var row4Grid = new Grid { ColumnSpacing = PanelSpacing };
        row4Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row4Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fontStack = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("Font size (px)"),
                fontBox,
                CreateChipWrapRow(
                    CreatePresetChip("12", _draftOverlayFontSize == "12", (_, _) => { _draftOverlayFontSize = "12"; QueueEditableControlDeckRender(); }),
                    CreatePresetChip("14", _draftOverlayFontSize == "14", (_, _) => { _draftOverlayFontSize = "14"; QueueEditableControlDeckRender(); }),
                    CreatePresetChip("16", _draftOverlayFontSize == "16", (_, _) => { _draftOverlayFontSize = "16"; QueueEditableControlDeckRender(); }),
                    CreatePresetChip("20", _draftOverlayFontSize == "20", (_, _) => { _draftOverlayFontSize = "20"; QueueEditableControlDeckRender(); })),
            },
        };
        row4Grid.Children.Add(fontStack);

        var opacityStack = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("Opacity (%)"),
                opacityBox,
                CreateChipWrapRow(
                    CreatePresetChip("60%", _draftOverlayOpacity == "60", (_, _) => { _draftOverlayOpacity = "60"; QueueEditableControlDeckRender(); }),
                    CreatePresetChip("75%", _draftOverlayOpacity == "75", (_, _) => { _draftOverlayOpacity = "75"; QueueEditableControlDeckRender(); }),
                    CreatePresetChip("90%", _draftOverlayOpacity == "90", (_, _) => { _draftOverlayOpacity = "90"; QueueEditableControlDeckRender(); }),
                    CreatePresetChip("100%", _draftOverlayOpacity == "100", (_, _) => { _draftOverlayOpacity = "100"; QueueEditableControlDeckRender(); })),
            },
        };
        row4Grid.Children.Add(opacityStack);
        Grid.SetColumn(opacityStack, 1);

        contentStack.Children.Add(row4Grid);

        // Row 5: hotkey
        var hotkeyBox = CreateDeckInlineEntry(_draftOverlayHotkey, "Ctrl+Shift+F11");
        hotkeyBox.Width = 280;
        hotkeyBox.HorizontalAlignment = HorizontalAlignment.Left;
        hotkeyBox.TextChanged += (_, _) => { _draftOverlayHotkey = hotkeyBox.Text; };

        var hotkeyRow = new Grid { ColumnSpacing = 12 };
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hotkeyStack = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                CreateFieldLabel("Toggle hotkey"),
                hotkeyBox,
                CreateMutedText("Modifiers: Ctrl, Alt, Shift, Win  ·  Key: letter, F1–F24. Default: Ctrl+Shift+F11", 11),
            },
        };
        hotkeyRow.Children.Add(hotkeyStack);

        var resetChip = CreateActionChip("Reset", (_, _) => { _draftOverlayHotkey = "Ctrl+Shift+F11"; QueueEditableControlDeckRender(); });
        resetChip.VerticalAlignment = VerticalAlignment.Bottom;
        hotkeyRow.Children.Add(resetChip);
        Grid.SetColumn(resetChip, 1);

        contentStack.Children.Add(hotkeyRow);

        var summaryValue = _draftOverlayEnabled
            ? _draftOverlayMode switch
            {
                OverlayMode.Simple => "FPS",
                OverlayMode.Advanced => "FPS + 4 metrics",
                OverlayMode.Custom => $"{_draftOverlayCustomMetrics.Count} of 5",
                _ => "On",
            }
            : "Off";

        var caption = _draftOverlayEnabled ? "Active when game has focus" : "In-game telemetry";

        return CreateControlEditorCard("overlay", "OVERLAY", summaryValue, caption, "#7CE38B", contentStack);
    }

    private static Border CreateColorSwatch(string hex)
    {
        Color swatchColor;
        try { swatchColor = ParseColor(hex); }
        catch { swatchColor = ParseColor("#66E7FF"); }

        return new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(swatchColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
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
