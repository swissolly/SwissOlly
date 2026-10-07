using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;
using Vaktr.App.Controls;
using Vaktr.App.ViewModels;
using Vaktr.Core.Models;

namespace Vaktr.App;

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
                        Text = "Starting Vaktr",
                        FontSize = 13,
                        Foreground = BrushFactory.CreateBrush("#7D9AB6"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
        _loadingOverlay.Loaded += (_, _) => loadingPulse.Begin();

        var root = new Grid
        {
            Background = BrushFactory.CreateBrush("#111315"),
        };
        root.Children.Add(_loadingOverlay);

        // After this screen renders, build the full UI on the next frame
        root.Loaded += (_, _) =>
        {
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, BuildFullUi);
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
            Padding = new Thickness(12, 12, 12, 18),
            Child = BuildShellStack(),
        };

        var root = new Grid
        {
            Background = ResolveBrush("AppBackdropBrush", "#111315"),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(_titleBarDragHost);

        _shellBorderRef = shellBorder;
        _scrollHost.Content = new Border
        {
            Margin = new Thickness(16, 4, 16, 16),
            Child = shellBorder,
        };
        root.Children.Add(_scrollHost);
        Grid.SetRow(_scrollHost, 1);

        root.Loaded += OnRootLoaded;
        root.SizeChanged += OnRootLayoutSizeChanged;
        return root;
    }

    private StackPanel BuildShellStack()
    {
        StartupTrace.Write("BuildShellStack // polished-v19");
        return new StackPanel
        {
            Spacing = 22,
            Children =
            {
                BuildHeader(),
                CreateSectionBand("System overview", "Current usage across this PC."),
                BuildSummarySurface(),
                BuildBoardSectionBand(),
                _dashboardGrid,
                BuildControlsSurface(),
            },
        };
    }

    private FrameworkElement BuildHeader()
    {
        StartupTrace.Write("BuildHeader // polished-v19");
        var heroGrid = new Grid
        {
            ColumnSpacing = 14,
            Margin = new Thickness(0, 0, 0, 2),
        };
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroGrid.Children.Add(_brandHost);

        var titleText = new TextBlock
        {
            Text = "Vaktr",
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveBrush("TextPrimaryBrush", "#F2F8FF"),
        };
        var subtitleText = new TextBlock
        {
            Text = "Local system monitor",
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
            Foreground = ResolveBrush("TextMutedBrush", "#7D9AB6"),
            Margin = new Thickness(1, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var titleStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                titleText,
                subtitleText,
            },
        };
        heroGrid.Children.Add(titleStack);
        Grid.SetColumn(titleStack, 1);

        var statusSurface = new Border
        {
            Background = ResolveBrush("SurfaceElevatedBrush", "#222529"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#2C3035"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 7, 13, 7),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    _statusIndicator,
                    _statusText,
                },
            },
        };
        Grid.SetColumn(statusSurface, 2);
        heroGrid.Children.Add(statusSurface);
        return heroGrid;
    }

    private Border BuildControlsSurface()
    {
        StartupTrace.Write("BuildControlsSurface // polished-v19");
        var root = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateSectionHeader("Settings", "Collection, history, storage, and overlay."),
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

    private void RenderControlDeckSummary()
    {
        StartupTrace.Write("RenderControlDeckSummary // polished-v19");
        _controlDeckEditableActive = false;
        var settingsGrid = new Grid
        {
            ColumnSpacing = 18,
            RowSpacing = 12,
        };
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddStatusField(settingsGrid, 0, 0, "Interval", FormatScrapeInterval(_viewModel.EffectiveScrapeIntervalSeconds), "Sample cadence");
        AddStatusField(settingsGrid, 0, 1, "Retention", GetRetentionFieldValue(), "History window");
        AddStatusField(settingsGrid, 0, 2, "Storage", GetStorageFieldTitle(), GetStorageFieldCaption());

        var ov = _viewModel.OverlayConfig;
        var overlaySummary = BuildOverlaySummaryCard(ov);
        AddCardToGrid(settingsGrid, 1, 0, overlaySummary, columnSpan: 3);

        var actionRow = new Grid
        {
            ColumnSpacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var editButton = CreateChipRow(
            CreateActionChip("Edit", OnEditSettingsClick, filled: true));
        actionRow.Children.Add(editButton);
        Grid.SetColumn(editButton, 1);

        _controlsBodyHost.Child = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                settingsGrid,
                actionRow,
            },
        };
    }

    private void RenderEditableControlDeck()
    {
        StartupTrace.Write("RenderEditableControlDeck // polished-v19");
        _controlDeckEditableActive = true;

        var scrapeBox = CreateDeckInlineEntry(
            string.IsNullOrWhiteSpace(_draftScrapeIntervalInput) ? string.Empty : _draftScrapeIntervalInput,
            "2");
        scrapeBox.TextChanged += OnScrapeInputChanged;

        var retentionBox = CreateDeckInlineEntry(_draftRetentionInput, "24h");
        retentionBox.TextChanged += OnRetentionInputChanged;

        var storageBox = CreateDeckInlineEntry(
            string.IsNullOrWhiteSpace(_draftStorageDirectory) ? string.Empty : _draftStorageDirectory.Trim(),
            VaktrConfig.DefaultStorageDirectory);
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
            ColumnSpacing = 14,
            RowSpacing = 12,
        };
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddCardToGrid(fieldGrid, 0, 0, CreateControlEditorCard(
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

        AddCardToGrid(fieldGrid, 0, 1, CreateControlEditorCard(
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

        AddCardToGrid(fieldGrid, 0, 2, CreateControlEditorCard(
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
        AddCardToGrid(fieldGrid, 1, 0, BuildOverlayEditorCard(), columnSpan: 3);

        var actionRow = new Grid
        {
            ColumnSpacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actionChips = CreateChipRow(
            CreateActionChip(
                _draftThemeMode == ThemeMode.Dark ? "Light mode" : "Dark mode",
                OnThemeQuickToggle),
            CreateActionChip("Cancel", OnCancelSettingsClick),
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
        StartupTrace.Write("BuildSummarySurface // polished-v19");
        return new Grid
        {
            Children =
            {
                _summaryHost,
            },
        };
    }

    private static void AddControlField(Grid grid, int row, int column, string label, FrameworkElement control, int columnSpan = 1)
    {
        while (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        FrameworkElement element;
        if (string.IsNullOrWhiteSpace(label))
        {
            element = control;
        }
        else
        {
            element = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    CreateFieldLabel(label),
                    control,
                },
            };
        }

        grid.Children.Add(element);
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
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

    private static void AddStatusField(Grid grid, int row, int column, string label, string value, string caption)
    {
        while (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var accentHex = label switch
        {
            "Collection" => "#5DE6FF",
            "Retention" => "#67B7FF",
            "Storage path" => "#6EE7C8",
            _ => "#66E7FF",
        };

        var accentBrush = BrushFactory.CreateBrush(accentHex);
        var card = new Border
        {
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.9),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            MinHeight = 88,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    IconFactory.CreateTile(label, accentBrush, 42, 16),
                    new StackPanel
                    {
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            CreateMutedText(label, 10),
                            CreatePrimaryText(value, 18, true),
                            CreateSecondaryText(caption, 11),
                        },
                    },
                },
            },
        };

        grid.Children.Add(card);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
    }

    private UIElement CreateActiveDeckEditorSurface()
    {
        return _activeDeckEditor switch
        {
            DeckEditorMode.Retention => CreateRetentionEditorSurface(),
            DeckEditorMode.Storage => CreateStorageEditorSurface(),
            _ => CreateScrapeEditorSurface(),
        };
    }

    private Border CreateScrapeEditorSurface()
    {
        var scrapeBox = CreateDeckInlineEntry(
            string.IsNullOrWhiteSpace(_viewModel.ScrapeIntervalInput)
                ? string.Empty
                : _viewModel.ScrapeIntervalInput,
            "2");
        scrapeBox.TextChanged += OnScrapeInputChanged;

        return CreateFocusedEditorSurface(
            "SCRAPE",
            "Choose how often Vaktr samples the local node. Type seconds directly or snap to a preset.",
            new StackPanel
            {
                Spacing = 12,
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
                        CreatePresetChip("1s", _viewModel.EffectiveScrapeIntervalSeconds == 1, (_, _) => SetScrapeInterval(1)),
                        CreatePresetChip("2s", string.IsNullOrWhiteSpace(_viewModel.ScrapeIntervalInput), (_, _) => ResetScrapeInterval()),
                        CreatePresetChip("5s", _viewModel.EffectiveScrapeIntervalSeconds == 5, (_, _) => SetScrapeInterval(5)),
                        CreatePresetChip("10s", _viewModel.EffectiveScrapeIntervalSeconds == 10, (_, _) => SetScrapeInterval(10)),
                        CreatePresetChip("15s", _viewModel.EffectiveScrapeIntervalSeconds == 15, (_, _) => SetScrapeInterval(15)),
                        CreatePresetChip("30s", _viewModel.EffectiveScrapeIntervalSeconds == 30, (_, _) => SetScrapeInterval(30)),
                        CreatePresetChip("60s", _viewModel.EffectiveScrapeIntervalSeconds == 60, (_, _) => SetScrapeInterval(60))),
                },
            },
            "2 seconds is the default because it feels live without making the collector noisy.");
    }

    private Border CreateRetentionEditorSurface()
    {
        var retentionBox = CreateDeckInlineEntry(_viewModel.RetentionHoursInput, "24h");
        retentionBox.TextChanged += OnRetentionInputChanged;

        return CreateFocusedEditorSurface(
            "RETENTION",
            "Type retention with m, h, or d units, then keep it simple with a preset when that is faster.",
            new StackPanel
            {
                Spacing = 12,
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
                        CreatePresetChip("6h", _viewModel.EffectiveRetentionHours == 6, (_, _) => SetRetentionInput("6h")),
                        CreatePresetChip("12h", _viewModel.EffectiveRetentionHours == 12, (_, _) => SetRetentionInput("12h")),
                        CreatePresetChip("24h", _viewModel.EffectiveRetentionHours == 24, (_, _) => ResetRetentionHours()),
                        CreatePresetChip("7d", _viewModel.EffectiveRetentionHours == 168, (_, _) => SetRetentionInput("7d")),
                        CreatePresetChip("30d", _viewModel.EffectiveRetentionHours == 720, (_, _) => SetRetentionInput("30d")),
                        CreatePresetChip("90d", _viewModel.EffectiveRetentionHours == 2160, (_, _) => SetRetentionInput("90d"))),
                },
            },
            "Blank keeps the smart 24h default. Older samples are pruned to the exact retention window you apply.");
    }

    private Border CreateStorageEditorSurface()
    {
        var storageBox = CreateDeckInlineEntry(
            string.IsNullOrWhiteSpace(_viewModel.StorageDirectory)
                ? string.Empty
                : _viewModel.StorageDirectory.Trim(),
            VaktrConfig.DefaultStorageDirectory);
        storageBox.TextChanged += OnStorageInputChanged;

        var dropSurface = new Border
        {
            Background = ResolveBrush("SurfaceInsetBrush", "#141619"),
            BorderBrush = ResolveBrush("AccentStrongBrush", "#B7F7FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            AllowDrop = true,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    CreateFieldLabel("Storage directory"),
                    storageBox,
                    CreateMutedText("Browse, drop a folder, or type a path. Blank uses the default.", 12),
                },
            },
        };
        dropSurface.DragOver += OnStoragePathDragOver;
        dropSurface.Drop += OnStoragePathDrop;

        return new Border
        {
            Background = ResolveBrush("SurfaceStrongBrush", "#292D31"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    CreateSectionHeader("STORAGE", "Vaktr keeps telemetry local, so Local App Data is the sensible default instead of Roaming."),
                    dropSurface,
                    CreateChipWrapRow(
                        CreateActionChip("Browse folder", OnBrowseStorageClick, filled: true),
                        CreateActionChip("Use default", (_, _) => ResetStorageDirectory())),
                    CreateSecondaryText("Default: %LocalAppData%\\Vaktr\\Data", 12),
                },
            },
        };
    }

    private Border CreateFocusedEditorSurface(string eyebrow, string description, Panel chips, string footer)
    {
        return new Border
        {
            Background = ResolveBrush("SurfaceStrongBrush", "#292D31"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    CreateSectionHeader(eyebrow, description),
                    chips,
                    CreateSecondaryText(footer, 12),
                },
            },
        };
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
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 10,
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
            row.Children.Add(chip);
        }

        return row;
    }

    private static ActionChip CreatePresetChip(string text, bool isActive, EventHandler onClick)
    {
        var chip = CreateActionChip(text, onClick, isActive);
        chip.IsActive = isActive;
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
            _viewModel.StatusText = $"Storage path issue: {ex.Message}";
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
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        foreach (var chip in chips)
        {
            stack.Children.Add(chip);
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
            Padding = new Thickness(16, 14, 16, 14),
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

        grid.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                CreatePrimaryText("Live telemetry", 17, true),
                CreateSecondaryText("Performance and activity over time", 12.5),
            },
        });

        var actions = CreateChipRow(
            _globalRangeButton,
            _globalResetZoomButton);
        actions.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(actions);
        Grid.SetColumn(actions, 1);

        return new StackPanel
        {
            Spacing = 0,
            Children =
            {
                grid,
                _globalRangeEditorHost,
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
        var accentBrush = BrushFactory.CreateBrush(accentHex);
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
            Padding = new Thickness(16, 14, 16, 14),
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

        var imagePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "vaktr.png");
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

        var row3Grid = new Grid { ColumnSpacing = 16 };
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
        fontBox.TextChanged += (_, _) => { _draftOverlayFontSize = fontBox.Text; };

        var opacityBox = CreateDeckInlineEntry(_draftOverlayOpacity, "90");
        opacityBox.TextChanged += (_, _) => { _draftOverlayOpacity = opacityBox.Text; };

        var row4Grid = new Grid { ColumnSpacing = 16 };
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

    private static Border BuildOverlaySummaryCard(OverlayConfig ov)
    {
        var statusLine = ov.Enabled
            ? $"On — {ov.Mode} @ {ov.Corner switch { OverlayCorner.TopLeft => "Top Left", OverlayCorner.TopRight => "Top Right", OverlayCorner.BottomLeft => "Bottom Left", _ => "Bottom Right" }}"
            : "Off";

        var accentBrush = BrushFactory.CreateBrush("#7CE38B");
        var icon = IconFactory.CreateTile("overlay", accentBrush, 42, 16);

        var colorSwatch = CreateColorSwatch(ov.TextColor);

        var info = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                CreateMutedText("Overlay", 10),
                CreatePrimaryText(statusLine, 16, true),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        colorSwatch,
                        CreateSecondaryText($"Toggle: {ov.HotkeyGesture}", 11),
                    },
                },
            },
        };

        return new Border
        {
            Background = ResolveBrush("SurfaceBrush", "#1C1F22"),
            BorderBrush = ResolveBrush("SurfaceStrokeBrush", "#27425E"),
            BorderThickness = new Thickness(0.9),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            MinHeight = 72,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, info },
            },
        };
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
            ? "%LocalAppData%\\Vaktr\\Data"
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

    private string GetStorageFieldCaption()
    {
        return GetStorageFieldCaption(_viewModel.StorageDirectory);
    }

    private static string GetStorageFieldCaption(string? storageDirectory)
    {
        if (!string.IsNullOrWhiteSpace(storageDirectory))
        {
            return storageDirectory.Trim();
        }

        try
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vaktr", "Data");
        }
        catch
        {
            return "%LocalAppData%\\Vaktr\\Data";
        }
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
            return MainViewModel.FormatRetentionInput(VaktrConfig.DefaultMaxRetentionHours);
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
