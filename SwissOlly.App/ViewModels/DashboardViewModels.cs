using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using SwissOlly.App.Controls;
using SwissOlly.Core.Models;

namespace SwissOlly.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, MetricPanelViewModel> _panelLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _panelOrder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PanelSizePreset> _panelSizePresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PanelLayout> _panelLayouts = new(StringComparer.OrdinalIgnoreCase);
    // Saved geometry and visibility for panels not on the board yet (or hidden), kept for saving.
    private readonly Dictionary<string, PanelLayout> _parkedPanelLayouts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> _savedPanelVisibility = new(StringComparer.OrdinalIgnoreCase);
    private bool _dashboardPanelsDirty = true;
    private bool _panelTogglesDirty = true;
    private bool _panelLayoutsNeedResolution;

    private string _storageDirectory = string.Empty;
    private string _scrapeIntervalInput = string.Empty;
    private string _retentionHoursInput = string.Empty;
    private int _selectedIntervalSeconds = SwissOllyConfig.DefaultScrapeIntervalSeconds;
    private int _selectedWindowMinutes = 15;
    private ThemeMode _selectedTheme = ThemeMode.Dark;
    private OverlayConfig _overlayConfig = new OverlayConfig().Normalize();
    private bool _launchOnStartup;
    private bool _minimizeToTray = true;
    private bool _isSettingsOpen;
    private string _statusText = "Arming telemetry deck";
    private MetricPanelViewModel? _expandedPanel;
    private TimeSpan _retentionWindow = TimeSpan.FromHours(SwissOllyConfig.DefaultMaxRetentionHours);

    public MainViewModel(SwissOllyConfig config)
    {
        IntervalOptions =
        [
            new SelectionOption(1, "1 second"),
            new SelectionOption(2, "2 seconds"),
            new SelectionOption(5, "5 seconds"),
            new SelectionOption(10, "10 seconds"),
            new SelectionOption(30, "30 seconds"),
        ];

        WindowOptions =
        [
            new SelectionOption(5, "5 minutes"),
            new SelectionOption(30, "30 minutes"),
            new SelectionOption(60, "1 hour"),
            new SelectionOption(720, "12 hours"),
            new SelectionOption(1440, "24 hours"),
            new SelectionOption(10080, "7 days"),
            new SelectionOption(43200, "30 days"),
        ];

        RetentionOptions =
        [
            new SelectionOption(1, "1 day"),
            new SelectionOption(7, "7 days"),
            new SelectionOption(30, "30 days"),
            new SelectionOption(90, "90 days"),
            new SelectionOption(0, "Forever"),
        ];

        SummaryCards =
        [
            new SummaryCardViewModel("CPU", "CPU", BrushFactory.CreateBrush("#5DE6FF")),
            new SummaryCardViewModel("GPU", "GPU", BrushFactory.CreateBrush("#7FB4FF")),
            new SummaryCardViewModel("RAM", "Memory", BrushFactory.CreateBrush("#7BF7D0")),
            new SummaryCardViewModel("Drive", "Drives", BrushFactory.CreateBrush("#B08BF0")),
        ];

        EnsureBaselinePanels();
        ApplyConfig(config);
    }

    public ObservableCollection<MetricPanelViewModel> DashboardPanels { get; } = [];

    public ObservableCollection<PanelToggleViewModel> PanelToggles { get; } = [];

    public IReadOnlyList<SummaryCardViewModel> SummaryCards { get; }

    public IReadOnlyList<SelectionOption> IntervalOptions { get; }

    public IReadOnlyList<SelectionOption> WindowOptions { get; }

    public IReadOnlyList<SelectionOption> RetentionOptions { get; }

    /// <summary>After a theme change: chart lines and summary accents follow the new palette.</summary>
    public void RefreshSeriesColors()
    {
        MetricPanelViewModel.ClearDimmedBrushCache();
        foreach (var panel in _panelLookup.Values)
        {
            panel.RefreshSeriesColors();
        }

        foreach (var card in SummaryCards)
        {
            card.RefreshColors();
        }
    }

    public OverlayConfig OverlayConfig
    {
        get => _overlayConfig;
        set => SetProperty(ref _overlayConfig, value);
    }

    public string StorageDirectory
    {
        get => _storageDirectory;
        set => SetProperty(ref _storageDirectory, value);
    }

    public string ScrapeIntervalInput
    {
        get => _scrapeIntervalInput;
        set => SetProperty(ref _scrapeIntervalInput, value);
    }

    public int SelectedIntervalSeconds
    {
        get => _selectedIntervalSeconds;
        set => SetProperty(ref _selectedIntervalSeconds, value);
    }

    public int SelectedWindowMinutes
    {
        get => _selectedWindowMinutes;
        set
        {
            if (!SetProperty(ref _selectedWindowMinutes, value))
            {
                return;
            }

            foreach (var panel in _panelLookup.Values)
            {
                panel.SelectedRange = MapToRangePreset(value);
            }
        }
    }

    public void ApplyGlobalWindowRange(int minutes)
    {
        if (_selectedWindowMinutes != minutes)
        {
            _selectedWindowMinutes = minutes;
            RaisePropertyChanged(nameof(SelectedWindowMinutes));
        }

        var preset = MapToRangePreset(minutes);
        foreach (var panel in _panelLookup.Values)
        {
            // Only refresh visible panels — hidden ones refresh when shown
            if (panel.IsVisible)
            {
                panel.ApplyRangePreset(preset);
            }
            else
            {
                panel.SelectedRange = preset;
            }
        }
    }

    public void ApplyGlobalAbsoluteRange(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        foreach (var panel in _panelLookup.Values)
        {
            if (panel.IsVisible)
            {
                panel.ZoomToWindow(startUtc, endUtc);
            }
        }
    }

    public void ResetGlobalZoom()
    {
        foreach (var panel in _panelLookup.Values)
        {
            panel.ResetZoom();
        }
    }

    public string RetentionHoursInput
    {
        get => _retentionHoursInput;
        set => SetProperty(ref _retentionHoursInput, value);
    }

    public ThemeMode SelectedTheme
    {
        get => _selectedTheme;
        set => SetProperty(ref _selectedTheme, value);
    }

    public bool LaunchOnStartup
    {
        get => _launchOnStartup;
        set => SetProperty(ref _launchOnStartup, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool HasShownTrayNotice { get; set; }

    private string? _historyWriteIssue;

    /// <summary>
    /// Why history is not being saved, or null when writes succeed. While set, the status line
    /// keeps saying so instead of reverting to "Last sample" on the next snapshot.
    /// </summary>
    public void SetHistoryWriteIssue(string? issue)
    {
        _historyWriteIssue = issue;
        if (issue is not null)
        {
            SetStatus(issue, StatusSeverity.Issue);
        }
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set => SetProperty(ref _isSettingsOpen, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Drives the status dot colour. Explicit, so wording changes never recolour it.</summary>
    public StatusSeverity StatusSeverity { get; private set; } = StatusSeverity.Pending;

    private DateTimeOffset _statusHeldUntilUtc;

    /// <summary>
    /// Shows a status line. Problems stay up for eight seconds, and <paramref name="holdFor"/>
    /// keeps any other message up as long, before the next snapshot's "Last sample" replaces it:
    /// snapshots arrive every two seconds, which wiped validation errors before they were read.
    /// </summary>
    public void SetStatus(string text, StatusSeverity severity, TimeSpan? holdFor = null)
    {
        StatusSeverity = severity;
        StatusText = text;
        _statusHeldUntilUtc = DateTimeOffset.UtcNow +
            (holdFor ?? (severity == StatusSeverity.Issue ? TimeSpan.FromSeconds(8) : TimeSpan.Zero));
    }

    /// <summary>Lets the next snapshot replace a held message that no longer applies.</summary>
    public void ReleaseStatusHold() => _statusHeldUntilUtc = DateTimeOffset.MinValue;

    public MetricPanelViewModel? ExpandedPanel
    {
        get => _expandedPanel;
        set => SetProperty(ref _expandedPanel, value);
    }

    public SwissOllyConfig BuildConfig()
    {
        // Panels this run has not met yet keep their saved entries: a save made before history
        // loads (a sign-in launch hides to the tray and saves at once) wiped them otherwise.
        var visibility = _savedPanelVisibility
            .Where(pair => !_panelLookup.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var toggle in PanelToggles)
        {
            visibility[toggle.PanelKey] = toggle.IsVisible;
        }

        return new SwissOllyConfig
        {
            ScrapeIntervalSeconds = EffectiveScrapeIntervalSeconds,
            GraphWindowMinutes = SelectedWindowMinutes,
            MaxRetentionHours = EffectiveRetentionHours,
            RetentionInputText = NormalizeRetentionInputForPersistence(RetentionHoursInput),
            Theme = SelectedTheme,
            StorageDirectory = EffectiveStorageDirectory,
            LaunchOnStartup = LaunchOnStartup,
            MinimizeToTray = MinimizeToTray,
            HasShownTrayNotice = HasShownTrayNotice,
            PanelVisibility = visibility,
            PanelOrder = BuildPersistedPanelOrder().ToList(),
            PanelSizes = BuildPersistedPanelSizes(),
            PanelLayouts = PanelBoard.ForSaving(_panelLayouts, _parkedPanelLayouts),
            Overlay = _overlayConfig,
        }.Normalize();
    }

    public void ApplyConfig(SwissOllyConfig config)
    {
        var normalized = config.Normalize();
        EnsureBaselinePanels();
        _retentionWindow = normalized.GetRetentionWindow();
        StorageDirectory = string.Equals(normalized.StorageDirectory, SwissOllyConfig.DefaultStorageDirectory, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized.StorageDirectory;
        ScrapeIntervalInput = normalized.ScrapeIntervalSeconds == SwissOllyConfig.DefaultScrapeIntervalSeconds
            ? string.Empty
            : normalized.ScrapeIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RetentionHoursInput = !string.IsNullOrWhiteSpace(normalized.RetentionInputText)
            ? normalized.RetentionInputText
            : normalized.MaxRetentionHours == SwissOllyConfig.DefaultMaxRetentionHours
                ? string.Empty
                : FormatRetentionInput(normalized.MaxRetentionHours);
        SelectedIntervalSeconds = normalized.ScrapeIntervalSeconds;
        SelectedWindowMinutes = normalized.GraphWindowMinutes;
        SelectedTheme = normalized.Theme;
        LaunchOnStartup = normalized.LaunchOnStartup;
        MinimizeToTray = normalized.MinimizeToTray;
        HasShownTrayNotice = normalized.HasShownTrayNotice;
        _savedPanelVisibility = new Dictionary<string, bool>(normalized.PanelVisibility, StringComparer.OrdinalIgnoreCase);
        ApplyPanelOrder(normalized.PanelOrder);
        _panelSizePresets.Clear();
        foreach (var (panelKey, size) in normalized.PanelSizes)
        {
            _panelSizePresets[panelKey] = size;
        }

        foreach (var toggle in PanelToggles)
        {
            toggle.IsVisible = normalized.PanelVisibility.GetValueOrDefault(toggle.PanelKey, true);
        }

        foreach (var panel in _panelLookup.Values)
        {
            panel.SetRetentionWindow(_retentionWindow);
            panel.IsVisible = normalized.PanelVisibility.GetValueOrDefault(panel.PanelKey, panel.IsDashboardPanel);
            panel.SizePreset = _panelSizePresets.GetValueOrDefault(panel.PanelKey, PanelSizePreset.Standard);
            panel.SelectedRange = MapToRangePreset(normalized.GraphWindowMinutes);
        }

        ApplyPanelLayouts(normalized);

        _dashboardPanelsDirty = true;
        _panelTogglesDirty = true;
        SyncDashboardPanels();
        SyncPanelToggles();

        OverlayConfig = normalized.Overlay ?? new OverlayConfig().Normalize();
    }

    private bool _isHistoryLoading;

    /// <summary>While a history read is in flight, panels show a loading placeholder instead of
    /// "Waiting for samples", which read as "no data" for the moment before history arrived.</summary>
    public void SetHistoryLoading(bool loading)
    {
        _isHistoryLoading = loading;
        foreach (var panel in _panelLookup.Values)
        {
            panel.IsHistoryLoading = loading;
        }
    }

    public void LoadHistory(IReadOnlyList<MetricSeriesHistory> history)
    {
        var panelCreated = false;
        foreach (var panelHistory in history)
        {
            var panel = GetOrCreatePanel(panelHistory.PanelKey, panelHistory.PanelTitle, panelHistory.Category, panelHistory.Unit);
            panel.LoadHistory(panelHistory);
            panelCreated = true;
        }

        if (panelCreated)
        {
            _dashboardPanelsDirty = true;
            _panelTogglesDirty = true;
            SyncDashboardPanels();
            SyncPanelToggles();
        }
    }

    /// <summary>
    /// Puts a coarser read of older history in front of what each series holds, replacing any
    /// points before <paramref name="beforeUtc"/>. The recent window stays at full resolution, so
    /// a long range never coarsens the short ones.
    /// </summary>
    public void MergeOlderHistory(IReadOnlyList<MetricSeriesHistory> history, DateTimeOffset beforeUtc)
    {
        var panelCreated = false;
        foreach (var panelHistory in history)
        {
            var existed = _panelLookup.ContainsKey(panelHistory.PanelKey);
            var panel = GetOrCreatePanel(panelHistory.PanelKey, panelHistory.PanelTitle, panelHistory.Category, panelHistory.Unit);
            panel.MergeOlderHistory(panelHistory, beforeUtc);
            panelCreated |= !existed;
        }

        if (panelCreated)
        {
            _dashboardPanelsDirty = true;
            _panelTogglesDirty = true;
            SyncDashboardPanels();
            SyncPanelToggles();
        }
    }

    public void ApplySnapshot(MetricSnapshot snapshot)
    {
        var panelCreated = false;
        foreach (var sample in snapshot.Samples)
        {
            var panel = GetOrCreatePanel(sample.PanelKey, sample.PanelTitle, sample.Category, sample.Unit);
            panel.AppendSampleFast(sample);
            panelCreated |= panel.IsNewlyCreated;
            panel.IsNewlyCreated = false;
        }

        // Batch presentation refresh only for panels that received new data
        foreach (var panel in _panelLookup.Values)
        {
            if (panel.IsVisible && !panel.IsZoomed && panel._hasNewSamples)
            {
                panel._hasNewSamples = false;
                panel.RefreshPresentation();
            }
        }

        // Only extract detail context if at least one visible panel needs it
        var needsDetail = false;
        foreach (var panel in _panelLookup.Values)
        {
            if (!panel.IsVisible) continue;
            if (panel.SupportsProcessTable || panel.Category == MetricCategory.Cpu || panel.Category == MetricCategory.Disk)
            {
                needsDetail = true;
                break;
            }
        }

        var detailContext = needsDetail ? PanelDetailContext.FromSnapshot(snapshot) : PanelDetailContext.Empty;
        foreach (var panel in _panelLookup.Values)
        {
            // Only apply detail context to visible panels that need it
            // (process tables + panels that show frequency/process/thread counts)
            if (!panel.IsVisible) continue;
            if (!panel.SupportsProcessTable &&
                !string.Equals(panel.PanelKey, "cpu-total", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(panel.PanelKey, "cpu-cores", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(panel.PanelKey, "cpu-frequency", StringComparison.OrdinalIgnoreCase) &&
                panel.Category != MetricCategory.Disk) continue;

            panel.ApplyDetailContext(detailContext);
        }

        var panelCount = DashboardPanels.Count;
        if (_historyWriteIssue is not null)
        {
            SetStatus(_historyWriteIssue, StatusSeverity.Issue);
        }
        else if (DateTimeOffset.UtcNow >= _statusHeldUntilUtc)
        {
            StatusSeverity = StatusSeverity.Healthy;
            StatusText = $"Last sample {snapshot.Timestamp.LocalDateTime:t} · {panelCount} panels · {snapshot.Samples.Count} metrics";
        }
        UpdateSummaryCards(snapshot, detailContext);

        if (panelCreated)
        {
            _dashboardPanelsDirty = true;
            _panelTogglesDirty = true;
            SyncPanelToggles();
            SyncDashboardPanels();
        }
    }

    public void ApplyPanelVisibility()
    {
        var visibilityMap = PanelToggles.ToDictionary(toggle => toggle.PanelKey, toggle => toggle.IsVisible, StringComparer.OrdinalIgnoreCase);
        foreach (var panel in _panelLookup.Values)
        {
            panel.IsVisible = visibilityMap.GetValueOrDefault(panel.PanelKey, panel.IsDashboardPanel);
        }

        _dashboardPanelsDirty = true;
        SyncDashboardPanels();
    }

    public bool MovePanel(string movingKey, string targetKey)
    {
        if (string.IsNullOrWhiteSpace(movingKey) ||
            string.IsNullOrWhiteSpace(targetKey) ||
            string.Equals(movingKey, targetKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_panelLookup.ContainsKey(movingKey) || !_panelLookup.ContainsKey(targetKey))
        {
            return false;
        }

        var orderedKeys = BuildPersistedPanelOrder().ToList();
        var movingIndex = orderedKeys.FindIndex(key => string.Equals(key, movingKey, StringComparison.OrdinalIgnoreCase));
        var targetIndex = orderedKeys.FindIndex(key => string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase));
        if (movingIndex < 0 || targetIndex < 0 || movingIndex == targetIndex)
        {
            return false;
        }

        // Direct swap — panels exchange positions, nothing else shifts
        (orderedKeys[movingIndex], orderedKeys[targetIndex]) = (orderedKeys[targetIndex], orderedKeys[movingIndex]);
        ApplyPanelOrder(orderedKeys);

        // Swap in the ObservableCollection
        var movingCollectionIndex = -1;
        var targetCollectionIndex = -1;
        for (var i = 0; i < DashboardPanels.Count; i++)
        {
            if (string.Equals(DashboardPanels[i].PanelKey, movingKey, StringComparison.OrdinalIgnoreCase))
            {
                movingCollectionIndex = i;
            }
            else if (string.Equals(DashboardPanels[i].PanelKey, targetKey, StringComparison.OrdinalIgnoreCase))
            {
                targetCollectionIndex = i;
            }
        }

        if (movingCollectionIndex >= 0 && targetCollectionIndex >= 0)
        {
            DashboardPanels.Move(movingCollectionIndex, targetCollectionIndex);
            if (targetCollectionIndex < movingCollectionIndex)
            {
                DashboardPanels.Move(targetCollectionIndex + 1, movingCollectionIndex);
            }
            else
            {
                DashboardPanels.Move(targetCollectionIndex - 1, movingCollectionIndex);
            }
        }

        _dashboardPanelsDirty = false;
        return true;
    }

    /// <summary>
    /// Loads saved geometry. Panels not on the board yet are parked until they arrive; panels
    /// without an entry are filled in at placement time.
    /// </summary>
    private void ApplyPanelLayouts(SwissOllyConfig normalized)
    {
        _panelLayouts.Clear();
        _parkedPanelLayouts.Clear();
        var onBoard = new HashSet<string>(DashboardPanels.Select(panel => panel.PanelKey), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, layout) in normalized.PanelLayouts)
        {
            (onBoard.Contains(key) ? _panelLayouts : _parkedPanelLayouts)[key] = layout.Clone().Normalize();
        }

        _panelLayoutsNeedResolution = true;
    }

    /// <summary>
    /// Guarantees every supplied panel has geometry, then resolves the board.
    ///
    /// Called from the render path rather than from ApplyConfig because the dashboard panel list
    /// is not populated yet when config is applied — doing it there left most panels without a
    /// layout, and they all collapsed onto cell (0,0).
    /// </summary>
    public void EnsureLayoutsFor(IReadOnlyList<string> panelKeys)
    {
        if (panelKeys.Count == 0)
        {
            return;
        }

        if (PanelBoard.Reconcile(
            _panelLayouts,
            _parkedPanelLayouts,
            panelKeys,
            key => _panelSizePresets.GetValueOrDefault(key, PanelSizePreset.Standard)))
        {
            // Returning geometry may overlap a panel placed while it was away.
            _panelLayoutsNeedResolution = true;
        }

        if (_panelLayoutsNeedResolution)
        {
            ResolvePanelLayouts();
        }
    }

    /// <summary>
    /// Fixes loaded overlaps without compacting intentional saved gaps, then writes the result back.
    /// </summary>
    public void ResolvePanelLayouts()
    {
        if (_panelLayouts.Count == 0)
        {
            _panelLayoutsNeedResolution = false;
            return;
        }

        var ordered = BuildPersistedPanelOrder()
            .Where(_panelLayouts.ContainsKey)
            .Concat(_panelLayouts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => new PanelPlacement(k, _panelLayouts[k]));

        foreach (var placement in GridLayoutEngine.ResolveResponsive(ordered, PanelLayout.MinWidth))
        {
            _panelLayouts[placement.Key] = placement.Layout;
        }
        _panelLayoutsNeedResolution = false;
    }

    public PanelLayout? GetPanelLayout(string panelKey)
        => _panelLayouts.TryGetValue(panelKey, out var layout) ? layout : null;

    public IReadOnlyDictionary<string, PanelLayout> PanelLayouts => _panelLayouts;

    /// <summary>Snapshot for Cancel in edit mode.</summary>
    public Dictionary<string, PanelLayout> SnapshotPanelLayouts()
        => _panelLayouts.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    public void RestorePanelLayouts(Dictionary<string, PanelLayout> snapshot)
    {
        _panelLayouts.Clear();
        foreach (var (key, layout) in snapshot)
        {
            _panelLayouts[key] = layout.Clone();
        }
        _panelLayoutsNeedResolution = false;
    }

    /// <summary>Applies an engine result (from a move or resize) to the stored geometry.</summary>
    public void ApplyPlacements(IEnumerable<PanelPlacement> placements)
    {
        foreach (var placement in placements)
        {
            _panelLayouts[placement.Key] = placement.Layout;
        }
        _panelLayoutsNeedResolution = false;
    }

    public bool SetPanelSizePreset(string panelKey, PanelSizePreset preset)
    {
        if (string.IsNullOrWhiteSpace(panelKey) || !Enum.IsDefined(typeof(PanelSizePreset), preset))
        {
            return false;
        }

        if (_panelLookup.TryGetValue(panelKey, out var panel))
        {
            if (panel.SizePreset == preset)
            {
                return false;
            }

            panel.SizePreset = preset;
        }

        _panelSizePresets[panelKey] = preset;

        // Layout now comes from PanelLayouts, so the preset has to translate into geometry or
        // the Size chip silently stops doing anything. Routing it through the engine also means
        // neighbours reflow exactly as they will when drag-resize lands.
        if (_panelLayouts.ContainsKey(panelKey))
        {
            var placements = _panelLayouts
                .Select(kv => new PanelPlacement(kv.Key, kv.Value))
                .ToList();

            ApplyPlacements(GridLayoutEngine.Resize(
                placements,
                panelKey,
                PanelLayout.WidthFromLegacyPreset(preset),
                PanelLayout.HeightFromLegacyPreset(preset)));
        }

        return true;
    }

    public int EffectiveScrapeIntervalSeconds =>
        ParseOptionalInt(ScrapeIntervalInput, SwissOllyConfig.DefaultScrapeIntervalSeconds, 1, 60);

    public int EffectiveRetentionHours =>
        TryParseRetentionInput(RetentionHoursInput, out var hours, out _)
            ? hours
            : SwissOllyConfig.DefaultMaxRetentionHours;

    public string EffectiveStorageDirectory =>
        string.IsNullOrWhiteSpace(StorageDirectory)
            ? SwissOllyConfig.DefaultStorageDirectory
            : StorageDirectory.Trim();

    public bool HasValidRetentionInput =>
        TryParseRetentionInput(RetentionHoursInput, out _, out _);

    public static bool TryParseRetentionInput(string? text, out int hours, out string normalizedText)
    {
        hours = SwissOllyConfig.DefaultMaxRetentionHours;
        normalizedText = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!SwissOllyConfig.TryParseRetentionWindow(text, out var retentionWindow, out normalizedText))
        {
            return false;
        }

        var totalHours = retentionWindow.TotalHours;
        if (totalHours > 24 * 3650)
        {
            return false;
        }

        hours = Math.Clamp((int)Math.Ceiling(totalHours), 1, 24 * 3650);
        return true;
    }

    public static string FormatRetentionInput(int hours)
    {
        if (hours > 0 && hours % 24 == 0)
        {
            return $"{hours / 24}d";
        }

        return $"{hours}h";
    }

    private static string NormalizeRetentionInputForPersistence(string? input)
    {
        return TryParseRetentionInput(input, out _, out var normalizedText)
            ? normalizedText
            : string.Empty;
    }

    private MetricPanelViewModel GetOrCreatePanel(string panelKey, string title, MetricCategory category, MetricUnit unit)
    {
        if (_panelLookup.TryGetValue(panelKey, out var existing))
        {
            return existing;
        }

        if (panelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase))
        {
            unit = MetricUnit.Percent;
        }

        var panel = new MetricPanelViewModel(panelKey, title, category, unit)
        {
            SelectedRange = MapToRangePreset(SelectedWindowMinutes),
            SizePreset = _panelSizePresets.GetValueOrDefault(panelKey, PanelSizePreset.Standard),
            IsVisible = true,
            IsNewlyCreated = true,
            IsHistoryLoading = _isHistoryLoading,
        };
        panel.SetRetentionWindow(_retentionWindow);

        _panelLookup.Add(panelKey, panel);
        _dashboardPanelsDirty = true;
        _panelTogglesDirty = true;
        return panel;
    }

    private void SyncDashboardPanels()
    {
        if (!_dashboardPanelsDirty)
        {
            return;
        }

        var orderedPanels = _panelLookup.Values
            .Where(panel => panel.IsDashboardPanel && panel.IsVisible)
            .OrderBy(GetPanelOrderRank)
            .ThenBy(panel => panel.SortBucket)
            .ThenBy(panel => panel.SortGroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(panel => panel.SortVariant)
            .ThenBy(panel => panel.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (DashboardPanels.Count == orderedPanels.Length && DashboardPanels.SequenceEqual(orderedPanels))
        {
            _dashboardPanelsDirty = false;
            return;
        }

        DashboardPanels.Clear();
        foreach (var panel in orderedPanels)
        {
            DashboardPanels.Add(panel);
        }

        _dashboardPanelsDirty = false;
    }

    private void SyncPanelToggles()
    {
        if (!_panelTogglesDirty)
        {
            return;
        }

        var visibilityLookup = PanelToggles.ToDictionary(toggle => toggle.PanelKey, toggle => toggle.IsVisible, StringComparer.OrdinalIgnoreCase);
        var panels = _panelLookup.Values
            .Where(panel => panel.IsDashboardPanel)
            .OrderBy(GetPanelOrderRank)
            .ThenBy(panel => panel.SortBucket)
            .ThenBy(panel => panel.SortGroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(panel => panel.SortVariant)
            .ThenBy(panel => panel.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (PanelToggles.Count == panels.Length &&
            PanelToggles.Select(toggle => toggle.PanelKey).SequenceEqual(panels.Select(panel => panel.PanelKey), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var toggle in PanelToggles)
            {
                toggle.IsVisible = visibilityLookup.GetValueOrDefault(toggle.PanelKey, true);
            }

            _panelTogglesDirty = false;
            return;
        }

        PanelToggles.Clear();
        foreach (var panel in panels)
        {
            PanelToggles.Add(new PanelToggleViewModel(panel.PanelKey, panel.Title, visibilityLookup.GetValueOrDefault(panel.PanelKey, panel.IsVisible)));
        }

        _panelTogglesDirty = false;
    }

    private void ApplyPanelOrder(IEnumerable<string>? orderedKeys)
    {
        _panelOrder.Clear();
        if (orderedKeys is null)
        {
            return;
        }

        var index = 0;
        foreach (var key in orderedKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || _panelOrder.ContainsKey(key))
            {
                continue;
            }

            _panelOrder[key] = index++;
        }
    }

    private IReadOnlyList<string> BuildPersistedPanelOrder()
    {
        var known = _panelLookup.Values
            .Where(panel => panel.IsDashboardPanel)
            .OrderBy(GetPanelOrderRank)
            .ThenBy(panel => panel.SortBucket)
            .ThenBy(panel => panel.SortGroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(panel => panel.SortVariant)
            .ThenBy(panel => panel.Title, StringComparer.OrdinalIgnoreCase)
            .Select(panel => panel.PanelKey)
            .ToArray();
        return PanelBoard.MergeOrder(known, _panelOrder, _panelLookup.ContainsKey);
    }

    private Dictionary<string, PanelSizePreset> BuildPersistedPanelSizes()
    {
        var sizes = new Dictionary<string, PanelSizePreset>(_panelSizePresets, StringComparer.OrdinalIgnoreCase);
        foreach (var panel in _panelLookup.Values)
        {
            sizes[panel.PanelKey] = panel.SizePreset;
        }

        return sizes;
    }

    private int GetPanelOrderRank(MetricPanelViewModel panel)
    {
        return _panelOrder.TryGetValue(panel.PanelKey, out var rank)
            ? rank
            : int.MaxValue;
    }

    private void EnsureBaselinePanels()
    {
        // GPU temp shows up without a kernel driver; CPU temp requires PawnIO (roadmap item)
        EnsureBaselinePanel("gpu-temperature", "GPU Temperature", MetricCategory.Gpu, MetricUnit.Celsius);
    }

    private void EnsureBaselinePanel(string panelKey, string title, MetricCategory category, MetricUnit unit)
    {
        var panel = GetOrCreatePanel(panelKey, title, category, unit);
        panel.IsNewlyCreated = false;
    }

    private void UpdateSummaryCards(MetricSnapshot snapshot, PanelDetailContext detailContext)
    {
        // Single-pass extraction — no GroupBy/ToDictionary/Where/Sum allocations
        var cpuUsage = 0d;
        var gpuUsage = 0d;
        var gpuMemoryGb = 0d;
        var usedMemory = 0d;
        var availableMemory = 0d;
        var driveUsages = new List<(string Label, double UsedPct)>();

        foreach (var sample in snapshot.Samples)
        {
            if (string.Equals(sample.PanelKey, "cpu-total", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sample.SeriesKey, "usage", StringComparison.OrdinalIgnoreCase))
                cpuUsage = sample.Value;
            else if (string.Equals(sample.PanelKey, "gpu-total", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sample.SeriesKey, "usage", StringComparison.OrdinalIgnoreCase))
                gpuUsage = sample.Value;
            else if (string.Equals(sample.PanelKey, "gpu-memory", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sample.SeriesKey, "dedicated-gb", StringComparison.OrdinalIgnoreCase))
                gpuMemoryGb = sample.Value;
            else if (string.Equals(sample.PanelKey, "memory", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sample.SeriesKey, "used-gb", StringComparison.OrdinalIgnoreCase))
                    usedMemory = sample.Value;
                else if (string.Equals(sample.SeriesKey, "available-gb", StringComparison.OrdinalIgnoreCase))
                    availableMemory = sample.Value;
            }
            else if (sample.Category == MetricCategory.Disk &&
                     sample.PanelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(sample.SeriesKey, "used-percent", StringComparison.OrdinalIgnoreCase))
            {
                var driveLabel = sample.PanelTitle.Replace("Drive ", "").Replace(" Capacity", "");
                driveUsages.Add((driveLabel, sample.Value));
            }
        }

        SummaryCards[0].Update(
            $"{cpuUsage:0.#}%",
            BuildSummaryCaption(
                detailContext.CpuFrequencyMhz > 0 ? $"{detailContext.CpuFrequencyMhz / 1000d:0.00} GHz" : "Processor load",
                detailContext.ProcessCount > 0 ? $"{FormatCompactCount(detailContext.ProcessCount)} proc" : null,
                detailContext.ThreadCount > 0 ? $"{FormatCompactCount(detailContext.ThreadCount)} thr" : null),
            cpuUsage);

        SummaryCards[1].Update(
            $"{gpuUsage:0.#}%",
            // GiB, as the GPU Memory panel says it; "GB" here disagreed with the same number there.
            gpuMemoryGb > 0 ? $"{FormatCapacityForSummary(gpuMemoryGb)} VRAM" : "GPU utilization",
            gpuUsage);

        var totalMemory = usedMemory + availableMemory;
        var memoryPct = totalMemory > 0 ? usedMemory / totalMemory * 100d : 0d;
        SummaryCards[2].Update(
            $"{memoryPct:0.#}%",
            $"{FormatCapacityForSummary(usedMemory)} of {FormatCapacityForSummary(totalMemory)}",
            memoryPct);

        // Disk card: show highest drive usage, caption lists all drives
        if (driveUsages.Count > 0)
        {
            driveUsages.Sort((a, b) => b.UsedPct.CompareTo(a.UsedPct));
            var highestPct = driveUsages[0].UsedPct;
            var caption = driveUsages.Count == 1
                ? $"{driveUsages[0].Label} drive"
                : string.Join(" · ", driveUsages.ConvertAll(d => $"{d.Label} {d.UsedPct:0.#}%"));
            SummaryCards[3].Update($"{highestPct:0.#}%", caption, highestPct);
        }
        else
        {
            SummaryCards[3].Update("--", "Waiting for drive data");
        }
    }

    private static TimeRangePreset MapToRangePreset(int minutes) => minutes switch
    {
        <= 1 => TimeRangePreset.OneMinute,
        <= 5 => TimeRangePreset.FiveMinutes,
        <= 15 => TimeRangePreset.FifteenMinutes,
        <= 30 => TimeRangePreset.ThirtyMinutes,
        <= 60 => TimeRangePreset.OneHour,
        <= 720 => TimeRangePreset.TwelveHours,
        <= 1440 => TimeRangePreset.TwentyFourHours,
        <= 2880 => TimeRangePreset.TwoDays,
        <= 7200 => TimeRangePreset.FiveDays,
        <= 10080 => TimeRangePreset.SevenDays,
        <= 43200 => TimeRangePreset.ThirtyDays,
        <= 129600 => TimeRangePreset.NinetyDays,
        _ => TimeRangePreset.OneYear,
    };

    private static int ParseOptionalInt(string? text, int fallback, int minValue, int maxValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), out var parsed) && parsed >= minValue && parsed <= maxValue
            ? parsed
            : fallback;
    }

    private static string FormatCapacityForSummary(double gigabytes)
    {
        if (gigabytes >= 1024d)
        {
            return $"{gigabytes / 1024d:0.0} TiB";
        }

        return $"{gigabytes:0.0} GiB";
    }

    // " · ", the separator every other caption uses.
    private static string BuildSummaryCaption(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string FormatCompactCount(double value)
    {
        value = Math.Max(0d, value);
        if (value >= 1_000_000d)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000d)
        {
            return $"{value / 1_000d:0.#}k";
        }

        return $"{value:0}";
    }
}

public sealed class SelectionOption
{
    public SelectionOption(int value, string label)
    {
        Value = value;
        Label = label;
    }

    public int Value { get; }

    public string Label { get; }
}

public sealed class SummaryCardViewModel : ObservableObject
{
    private string _value = "--";
    private string _caption = "Waiting for data";

    private readonly Windows.UI.Color _baseAccentColor;

    public SummaryCardViewModel(string glyph, string title, SolidColorBrush accentBrush)
    {
        Glyph = glyph;
        Title = title;
        AccentBrush = accentBrush;
        _baseAccentColor = accentBrush.Color;
        RefreshColors();
    }

    public string Glyph { get; }

    public string Title { get; }

    public SolidColorBrush AccentBrush { get; }

    /// <summary>Darkens the accent on light palettes, as chart lines are (see ThemeVisuals.AdaptSeriesColor).</summary>
    public void RefreshColors() => AccentBrush.Color = ThemeVisuals.AdaptSeriesColor(_baseAccentColor);

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public string Caption
    {
        get => _caption;
        private set => SetProperty(ref _caption, value);
    }

    public double Utilization
    {
        get => _utilization;
        private set => SetProperty(ref _utilization, value);
    }

    private double _utilization;

    public void Update(string value, string caption, double utilization = 0d)
    {
        Value = value;
        Caption = caption;
        Utilization = utilization;
    }
}

public sealed class PanelToggleViewModel : ObservableObject
{
    private bool _isVisible;

    public PanelToggleViewModel(string panelKey, string title, bool isVisible)
    {
        PanelKey = panelKey;
        Title = title;
        _isVisible = isVisible;
    }

    public string PanelKey { get; }

    public string Title { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}

internal sealed class PanelDetailContext
{
    public static PanelDetailContext Empty { get; } = new(0d, 0, 0, 0, Array.Empty<ProcessActivitySample>(), new Dictionary<string, DriveDetailContext>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyList<ProcessActivitySample> _processes;
    private readonly IReadOnlyDictionary<string, DriveDetailContext> _driveDetails;

    public PanelDetailContext(
        double cpuFrequencyMhz,
        int processCount,
        int threadCount,
        int handleCount,
        IReadOnlyList<ProcessActivitySample> processes,
        IReadOnlyDictionary<string, DriveDetailContext> driveDetails)
    {
        CpuFrequencyMhz = cpuFrequencyMhz;
        ProcessCount = processCount;
        ThreadCount = threadCount;
        HandleCount = handleCount;
        _processes = processes;
        _driveDetails = driveDetails;
    }

    public double CpuFrequencyMhz { get; }

    public int ProcessCount { get; }

    public int ThreadCount { get; }

    public int HandleCount { get; }

    public IReadOnlyList<ProcessActivitySample> Processes => _processes;

    public bool TryGetDriveDetail(string panelKey, out DriveDetailContext detail)
    {
        detail = default;
        var suffix = ExtractDriveSuffix(panelKey);
        return !string.IsNullOrWhiteSpace(suffix) && _driveDetails.TryGetValue(suffix, out detail);
    }

    public static PanelDetailContext FromSnapshot(MetricSnapshot snapshot)
    {
        // Single-pass extraction — avoids GroupBy/ToDictionary/LINQ overhead on every 2s tick
        var cpuFrequency = 0d;
        var processCount = 0d;
        var threadCount = 0d;
        var handleCount = 0d;
        var driveDetails = new Dictionary<string, (double UsedPercent, double UsedGb, double TotalGb)>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in snapshot.Samples)
        {
            if (string.Equals(sample.PanelKey, "cpu-frequency", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sample.SeriesKey, "clock", StringComparison.OrdinalIgnoreCase))
            {
                cpuFrequency = sample.Value;
            }
            else if (string.Equals(sample.PanelKey, "host-activity", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sample.SeriesKey, "processes", StringComparison.OrdinalIgnoreCase))
                    processCount = sample.Value;
                else if (string.Equals(sample.SeriesKey, "threads", StringComparison.OrdinalIgnoreCase))
                    threadCount = sample.Value;
                else if (string.Equals(sample.SeriesKey, "handles", StringComparison.OrdinalIgnoreCase))
                    handleCount = sample.Value;
            }
            else if (sample.PanelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = ExtractDriveSuffix(sample.PanelKey);
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    if (!driveDetails.TryGetValue(suffix, out var detail))
                    {
                        detail = (0d, 0d, 0d);
                    }

                    if (string.Equals(sample.SeriesKey, "used-percent", StringComparison.OrdinalIgnoreCase))
                        detail.UsedPercent = sample.Value;
                    else if (string.Equals(sample.SeriesKey, "used-gb", StringComparison.OrdinalIgnoreCase))
                        detail.UsedGb = sample.Value;
                    else if (string.Equals(sample.SeriesKey, "total-gb", StringComparison.OrdinalIgnoreCase))
                        detail.TotalGb = sample.Value;

                    driveDetails[suffix] = detail;
                }
            }
        }

        var driveContexts = new Dictionary<string, DriveDetailContext>(driveDetails.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, detail) in driveDetails)
        {
            driveContexts[key] = new DriveDetailContext(detail.UsedPercent, detail.UsedGb, detail.TotalGb);
        }

        return new PanelDetailContext(
            cpuFrequency,
            (int)Math.Round(processCount, MidpointRounding.AwayFromZero),
            (int)Math.Round(threadCount, MidpointRounding.AwayFromZero),
            (int)Math.Round(handleCount, MidpointRounding.AwayFromZero),
            snapshot.LiveDetails?.Processes ?? Array.Empty<ProcessActivitySample>(),
            driveContexts);
    }

    private static string ExtractDriveSuffix(string panelKey)
    {
        if (panelKey.StartsWith("disk-", StringComparison.OrdinalIgnoreCase))
        {
            return panelKey["disk-".Length..];
        }

        if (panelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase))
        {
            return panelKey["volume-".Length..];
        }

        return string.Empty;
    }
}

internal readonly record struct DriveDetailContext(double UsedPercent, double UsedGigabytes, double TotalGigabytes);

public enum StatusSeverity
{
    Healthy,
    Pending,
    Issue,
}

public enum ProcessSortMode
{
    Highest = 0,
    Lowest = 1,
    Name = 2,
    NameReverse = 3,
}

public sealed class ProcessListItemViewModel
{
    public ProcessListItemViewModel(string key, string name, string value, string caption, double intensity, bool isPinned = false, Brush? chartColor = null)
    {
        Key = key;
        Name = name;
        Value = value;
        Caption = caption;
        Intensity = intensity;
        IsPinned = isPinned;
        ChartColor = chartColor;
    }

    public string Key { get; }

    public string Name { get; }

    public string Value { get; }

    public string Caption { get; }

    public double Intensity { get; }

    /// <summary>Whether this process is pinned to the chart.</summary>
    public bool IsPinned { get; }

    /// <summary>The chart line color for this process (null if not charted).</summary>
    public Brush? ChartColor { get; }
}

public sealed class MetricPanelViewModel : ObservableObject
{
    private static readonly TimeSpan BufferTrimInterval = TimeSpan.FromSeconds(20);
    private readonly Dictionary<string, SeriesBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SolidColorBrush _accentBrush;

    private IReadOnlyList<ChartSeriesViewModel> _visibleSeries = Array.Empty<ChartSeriesViewModel>();
    private string _currentValue = "Waiting";
    private string _secondaryValue = "Collecting hardware samples";
    private string _footerText = "arming";
    private string _scaleLabel = string.Empty;
    private string _emptyStateText = "Waiting for samples";
    private double _chartCeilingValue;
    private bool _isVisible;
    private PanelSizePreset _sizePreset = PanelSizePreset.Standard;
    private TimeRangePreset _selectedRange = TimeRangePreset.FifteenMinutes;
    private DateTimeOffset _windowStartUtc = DateTimeOffset.UtcNow.AddMinutes(-15);
    private DateTimeOffset _windowEndUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _zoomWindowStartUtc;
    private DateTimeOffset? _zoomWindowEndUtc;
    private DateTimeOffset _latestPointTimestampUtc = DateTimeOffset.UtcNow;
    private PanelDetailContext _detailContext = PanelDetailContext.Empty;
    private IReadOnlyList<ProcessListItemViewModel> _processRows = Array.Empty<ProcessListItemViewModel>();
    private ProcessSortMode _processSortMode = ProcessSortMode.Highest;
    private bool _processListExpanded;
    private bool _perProcessChartsEnabled;
    private int _totalProcessCount;
    private string? _focusedSeriesKey;
    private const int MaxProcessChartSeries = 8;
    private const string ProcessSeriesPrefix = "proc:";
    private readonly HashSet<string> _pinnedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _retentionWindow = TimeSpan.FromHours(SwissOllyConfig.DefaultMaxRetentionHours);
    internal bool _hasNewSamples;
    internal bool _forceProcessRowRebuild;

    public MetricPanelViewModel(string panelKey, string title, MetricCategory category, MetricUnit unit)
    {
        PanelKey = panelKey;
        Title = title;
        Category = category;
        Unit = unit;
        _accentBrush = ResolveBrush(0);
        _accentBaseColor = _accentBrush.Color;
        _accentBrush.Color = ThemeVisuals.AdaptSeriesColor(_accentBaseColor);
    }

    private readonly Windows.UI.Color _accentBaseColor;

    /// <summary>Recolours this panel's lines, fills and accent for the current palette.</summary>
    internal void RefreshSeriesColors()
    {
        _accentBrush.Color = ThemeVisuals.AdaptSeriesColor(_accentBaseColor);
        foreach (var buffer in _buffers.Values)
        {
            buffer.RefreshColors();
        }

        // The brushes recolour in place; only the dimmed copies a focused legend row gives the
        // other series need rebuilding. A full refresh of every panel on each theme click
        // re-sampled every series for nothing.
        if (_focusedSeriesKey is not null)
        {
            RefreshPresentation();
        }
    }

    internal static void ClearDimmedBrushCache() => s_dimmedBrushCache.Clear();

    /// <summary>True for a pinned process's line (as opposed to one of the panel's own series).</summary>
    internal static bool IsProcessSeriesKey(string? key) =>
        key is not null && key.StartsWith(ProcessSeriesPrefix, StringComparison.Ordinal);

    public string PanelKey { get; }

    public string Title { get; }

    public MetricCategory Category { get; }

    public MetricUnit Unit { get; }

    public bool IsNewlyCreated { get; set; }

    public bool IsDashboardPanel => true;

    public int SortBucket => PanelKey switch
    {
        "cpu-total" => 0,
        "cpu-cores" => 1,
        "cpu-frequency" => 2,
        "cpu-temperature" => 3,
        "gpu-total" => 4,
        "gpu-memory" => 5,
        "gpu-temperature" => 6,
        "host-activity" => 7,
        "memory" => 8,
        _ when PanelKey.StartsWith("disk-", StringComparison.OrdinalIgnoreCase) || PanelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase) => 9,
        _ when PanelKey.StartsWith("net-", StringComparison.OrdinalIgnoreCase) => 10,
        _ => 11,
    };

    public int SortVariant => PanelKey switch
    {
        _ when PanelKey.StartsWith("disk-", StringComparison.OrdinalIgnoreCase) => 0,
        _ when PanelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase) => 1,
        _ => 0,
    };

    public string SortGroupKey => PanelKey switch
    {
        _ when PanelKey.StartsWith("disk-", StringComparison.OrdinalIgnoreCase) => PanelKey["disk-".Length..],
        _ when PanelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase) => PanelKey["volume-".Length..],
        _ => PanelKey,
    };

    public string Badge => Category switch
    {
        MetricCategory.Disk when PrefersGaugeVisual => "DRV",
        MetricCategory.Cpu => "CPU",
        MetricCategory.Gpu => "GPU",
        MetricCategory.Memory => "MEM",
        MetricCategory.Disk => "DSK",
        MetricCategory.Network => "NET",
        MetricCategory.System => "SYS",
        _ => "LIVE",
    };

    public Brush AccentBrush => _accentBrush;

    public bool PrefersGaugeVisual =>
        Category == MetricCategory.Disk &&
        PanelKey.StartsWith("volume-", StringComparison.OrdinalIgnoreCase);

    public bool SupportsProcessTable =>
        string.Equals(PanelKey, "cpu-total", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(PanelKey, "memory", StringComparison.OrdinalIgnoreCase);

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public PanelSizePreset SizePreset
    {
        get => _sizePreset;
        set => SetProperty(ref _sizePreset, Enum.IsDefined(typeof(PanelSizePreset), value)
            ? value
            : PanelSizePreset.Standard);
    }

    public TimeRangePreset SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (!SetProperty(ref _selectedRange, value))
            {
                return;
            }

            _zoomWindowStartUtc = null;
            _zoomWindowEndUtc = null;
            RefreshPresentation();
        }
    }

    public IReadOnlyList<ChartSeriesViewModel> VisibleSeries
    {
        get => _visibleSeries;
        private set => SetProperty(ref _visibleSeries, value);
    }

    public string CurrentValue
    {
        get => _currentValue;
        private set => SetProperty(ref _currentValue, value);
    }

    public string SecondaryValue
    {
        get => _secondaryValue;
        private set => SetProperty(ref _secondaryValue, value);
    }

    public string FooterText
    {
        get => _footerText;
        private set => SetProperty(ref _footerText, value);
    }

    public string ScaleLabel
    {
        get => _scaleLabel;
        private set => SetProperty(ref _scaleLabel, value);
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        private set => SetProperty(ref _emptyStateText, value);
    }

    private bool _isHistoryLoading;

    public bool IsHistoryLoading
    {
        get => _isHistoryLoading;
        set => SetProperty(ref _isHistoryLoading, value);
    }

    public double UtilizationPercent { get; private set; }

    public double ChartCeilingValue
    {
        get => _chartCeilingValue;
        private set => SetProperty(ref _chartCeilingValue, value);
    }

    public double GaugeValue =>
        !PrefersGaugeVisual || VisibleSeries.Count == 0 || VisibleSeries[0].Points.Count == 0
            ? 0d
            : Math.Clamp(VisibleSeries[0].Points[^1].Value, 0d, 100d);

    public IReadOnlyList<ProcessListItemViewModel> ProcessRows
    {
        get => _processRows;
        private set => SetProperty(ref _processRows, value);
    }

    public ProcessSortMode ProcessSortMode
    {
        get => _processSortMode;
        set
        {
            if (!SetProperty(ref _processSortMode, value))
            {
                return;
            }

            RefreshProcessRows();
        }
    }

    public bool ProcessListExpanded
    {
        get => _processListExpanded;
        set
        {
            if (!SetProperty(ref _processListExpanded, value))
            {
                return;
            }

            RefreshProcessRows();
        }
    }

    public int TotalProcessCount => _totalProcessCount;

    public bool PerProcessChartsEnabled
    {
        get => _perProcessChartsEnabled;
        set
        {
            if (!SetProperty(ref _perProcessChartsEnabled, value))
            {
                return;
            }

            if (!value)
            {
                // Remove process chart series when disabled
                var processKeys = new List<string>();
                foreach (var key in _buffers.Keys)
                {
                    if (key.StartsWith(ProcessSeriesPrefix, StringComparison.Ordinal))
                    {
                        processKeys.Add(key);
                    }
                }
                foreach (var key in processKeys)
                {
                    _buffers.Remove(key);
                }

                RefreshPresentation();
            }
            // When enabling, skip the expensive full rebuild — process series will
            // appear on the next scrape via InjectPerProcessChartData, which already
            // triggers RefreshPresentation through AppendSample.
        }
    }

    public string ProcessTableTitle =>
        string.Equals(PanelKey, "memory", StringComparison.OrdinalIgnoreCase)
            ? "Process memory"
            : "Process load";

    /// <summary>
    /// Toggles whether a specific process is pinned to the chart.
    /// Pinned processes always appear as chart series regardless of the top-N ranking.
    /// </summary>
    public void ToggleProcessPin(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        if (!_pinnedProcesses.Remove(processName))
        {
            _pinnedProcesses.Add(processName);
        }
        else
        {
            // Unpinned — remove that process's chart data
            var seriesKey = $"{ProcessSeriesPrefix}{processName}";
            _buffers.Remove(seriesKey);
        }

        // Enable/disable per-process charts based on pin count
        _perProcessChartsEnabled = _pinnedProcesses.Count > 0;

        // Force a full process row rebuild so pin visual state (bold, dot) updates.
        // The flag stays true until the UI consumes it in RefreshProcessRows.
        _forceProcessRowRebuild = true;
        RefreshPresentation();
        RefreshProcessRows();
    }

    /// <summary>Whether a specific process is pinned to the chart.</summary>
    public bool IsProcessPinned(string processName) =>
        _pinnedProcesses.Contains(processName);

    /// <summary>
    /// When set, only this series is shown at full opacity; others are hidden.
    /// Set to null to show all series.
    /// </summary>
    public string? FocusedSeriesKey
    {
        get => _focusedSeriesKey;
        private set => SetProperty(ref _focusedSeriesKey, value);
    }

    /// <summary>
    /// Toggles focus on a specific series. If the series is already focused, clears the focus.
    /// </summary>
    public void ToggleSeriesFocus(string? seriesKey)
    {
        FocusedSeriesKey = string.Equals(_focusedSeriesKey, seriesKey, StringComparison.OrdinalIgnoreCase)
            ? null
            : seriesKey;
        RefreshPresentation();
    }

    public event EventHandler? ExpandRequested;

    public bool IsZoomed => _zoomWindowStartUtc.HasValue && _zoomWindowEndUtc.HasValue;

    public DateTimeOffset WindowStartUtc
    {
        get => _windowStartUtc;
        private set => SetProperty(ref _windowStartUtc, value);
    }

    public DateTimeOffset WindowEndUtc
    {
        get => _windowEndUtc;
        private set => SetProperty(ref _windowEndUtc, value);
    }

    public void LoadHistory(MetricSeriesHistory history)
    {
        _buffers.Clear();
        var paletteIndex = 0;
        var latestTimestamp = DateTimeOffset.MinValue;
        foreach (var series in history.Series)
        {
            // Move the incoming list in directly instead of copying. If the caller
            // gave us a List<MetricPoint>, adopt it; otherwise do a single copy.
            // This avoids an O(N) allocation per series on wide-range loads.
            var points = series.Points is List<MetricPoint> list
                ? list
                : new List<MetricPoint>(series.Points);

            _buffers[series.SeriesKey] = new SeriesBuffer(
                series.SeriesKey,
                series.SeriesName,
                points,
                ResolveBrush(paletteIndex),
                ResolveFillBrush(paletteIndex),
                points.Count > 0 ? points[^1].Timestamp : DateTimeOffset.UtcNow);

            if (points.Count > 0 && points[^1].Timestamp > latestTimestamp)
            {
                latestTimestamp = points[^1].Timestamp;
            }
            paletteIndex++;
        }

        if (latestTimestamp > DateTimeOffset.MinValue)
        {
            _latestPointTimestampUtc = latestTimestamp;
        }

        // Skip TrimBuffers — the store already returns only data within the retention
        // window, and the hard buffer cap doesn't apply to loaded history (buffers
        // will be trimmed naturally on the next live-sample append if needed).
        RefreshPresentation();
    }

    internal void MergeOlderHistory(MetricSeriesHistory history, DateTimeOffset beforeUtc)
    {
        foreach (var series in history.Series)
        {
            if (_buffers.TryGetValue(series.SeriesKey, out var buffer))
            {
                // Swap the head of the series: drop what it held before the cut, then put the
                // older read in front. Points stay in time order for the binary searches.
                var keepFrom = 0;
                while (keepFrom < buffer.Points.Count && buffer.Points[keepFrom].Timestamp < beforeUtc)
                {
                    keepFrom++;
                }

                buffer.Points.RemoveRange(0, keepFrom);
                var older = new List<MetricPoint>(series.Points.Count);
                foreach (var point in series.Points)
                {
                    if (point.Timestamp < beforeUtc)
                    {
                        older.Add(point);
                    }
                }

                buffer.Points.InsertRange(0, older);
            }
            else
            {
                var paletteIndex = _buffers.Count;
                var points = series.Points.Where(point => point.Timestamp < beforeUtc).ToList();
                _buffers[series.SeriesKey] = new SeriesBuffer(
                    series.SeriesKey,
                    series.SeriesName,
                    points,
                    ResolveBrush(paletteIndex),
                    ResolveFillBrush(paletteIndex),
                    points.Count > 0 ? points[^1].Timestamp : DateTimeOffset.UtcNow);
            }
        }

        RefreshPresentation();
    }

    /// <summary>Appends a sample and refreshes presentation (used for single-sample updates).</summary>
    public void AppendSample(MetricSample sample)
    {
        AppendSampleFast(sample);
        if (!IsZoomed)
        {
            RefreshPresentation(sample.Timestamp);
        }
    }

    /// <summary>Appends a sample without refreshing presentation (used during batch snapshot apply).</summary>
    internal void AppendSampleFast(MetricSample sample)
    {
        _hasNewSamples = true;
        if (!_buffers.TryGetValue(sample.SeriesKey, out var buffer))
        {
            var paletteIndex = _buffers.Count;
            buffer = new SeriesBuffer(
                sample.SeriesKey,
                sample.SeriesName,
                [],
                ResolveBrush(paletteIndex),
                ResolveFillBrush(paletteIndex),
                sample.Timestamp);
            _buffers.Add(sample.SeriesKey, buffer);
        }

        var point = new MetricPoint(sample.Timestamp, sample.Value);
        buffer.Points.Add(point);
        if (point.Value > buffer.TrackedPeak)
        {
            buffer.TrackedPeak = point.Value;
        }
        _latestPointTimestampUtc = sample.Timestamp;
        if (sample.Timestamp - buffer.LastTrimUtc >= BufferTrimInterval)
        {
            TrimBuffers(sample.Timestamp);
        }
    }

    public void SetRetentionWindow(TimeSpan retentionWindow)
    {
        var normalizedWindow = retentionWindow <= TimeSpan.Zero
            ? TimeSpan.FromHours(SwissOllyConfig.DefaultMaxRetentionHours)
            : retentionWindow;

        if (_retentionWindow == normalizedWindow)
        {
            return;
        }

        _retentionWindow = normalizedWindow;
        TrimBuffers(DateTimeOffset.UtcNow);
        RefreshPresentation();
    }

    internal void ApplyDetailContext(PanelDetailContext detailContext)
    {
        var processDetailsChanged =
            !ReferenceEquals(_detailContext.Processes, detailContext.Processes) ||
            _detailContext.ProcessCount != detailContext.ProcessCount ||
            _detailContext.ThreadCount != detailContext.ThreadCount ||
            _detailContext.HandleCount != detailContext.HandleCount;

        _detailContext = detailContext;
        if (processDetailsChanged && SupportsProcessTable)
        {
            RefreshProcessRows();
            InjectPerProcessChartData(detailContext);
        }

        UpdateSummaryText();
    }

    private void InjectPerProcessChartData(PanelDetailContext detailContext)
    {
        // Only inject data for explicitly pinned processes — no auto top-N
        if (_pinnedProcesses.Count == 0 || detailContext.Processes.Count == 0)
        {
            return;
        }

        var timestamp = _latestPointTimestampUtc;
        var isMemoryPanel = string.Equals(PanelKey, "memory", StringComparison.OrdinalIgnoreCase);

        // Pins are by name, so a pinned name's line is the total of every process with that name
        // (all of Code's helpers, every svchost), as Task Manager groups apps. Each instance used
        // to add its own point at the same timestamp, which drew the line as a vertical zigzag.
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in detailContext.Processes)
        {
            if (_pinnedProcesses.Contains(proc.Name))
            {
                totals[proc.Name] = totals.GetValueOrDefault(proc.Name) + (isMemoryPanel ? proc.MemoryGigabytes : proc.CpuPercent);
            }
        }

        foreach (var (name, total) in totals)
        {
            var seriesKey = $"{ProcessSeriesPrefix}{name}";
            if (!_buffers.TryGetValue(seriesKey, out var buffer))
            {
                var color = BrushFactory.ParseColor(NextPinnedProcessColor());
                buffer = new SeriesBuffer(
                    seriesKey,
                    name,
                    [],
                    new SolidColorBrush(color),
                    CreateFillBrush(color),
                    timestamp);
                _buffers.Add(seriesKey, buffer);
            }

            // Ignore a refresh that lands on the previous point's timestamp.
            if (buffer.Points.Count > 0 && buffer.Points[^1].Timestamp >= timestamp)
            {
                continue;
            }

            buffer.Points.Add(new MetricPoint(timestamp, Math.Min(total, isMemoryPanel ? double.MaxValue : 100d)));

            // Cap process series to a smaller budget (they accumulate per-process)
            const int maxProcessPoints = 2000;
            if (buffer.Points.Count > maxProcessPoints)
            {
                buffer.Points.RemoveRange(0, buffer.Points.Count - maxProcessPoints);
            }
        }
    }

    /// <summary>
    /// Pinned process lines, assigned in pin order. Hues well clear of the panels' own cyan and
    /// green: colours from the panel's category palette drew every pinned process in the same
    /// shade as each other and as the panel's own line.
    /// </summary>
    private static readonly string[] PinnedProcessColors = ["#FF7AC6", "#A78BFA", "#FF9F43", "#B5E853", "#5B9BFF", "#FFD34D"];

    /// <summary>The first pinned-process colour not already on this chart.</summary>
    private string NextPinnedProcessColor()
    {
        var inUse = new HashSet<Windows.UI.Color>();
        foreach (var buffer in _buffers.Values)
        {
            if (buffer.Key.StartsWith(ProcessSeriesPrefix, StringComparison.Ordinal))
            {
                // The colour as assigned; on a light palette the drawn colour is darkened.
                inUse.Add(buffer.BaseColor);
            }
        }

        foreach (var hex in PinnedProcessColors)
        {
            if (!inUse.Contains(BrushFactory.ParseColor(hex)))
            {
                return hex;
            }
        }

        return PinnedProcessColors[inUse.Count % PinnedProcessColors.Length];
    }

    public PanelHoverInfo? BuildHoverInfo(double normalizedPosition)
    {
        if (VisibleSeries.Count == 0)
        {
            return null;
        }

        normalizedPosition = Math.Clamp(normalizedPosition, 0d, 1d);
        // Find the series with the most points for timestamp reference
        ChartSeriesViewModel? referenceSeries = null;
        foreach (var s in VisibleSeries)
        {
            if (referenceSeries is null || s.Points.Count > referenceSeries.Points.Count)
                referenceSeries = s;
        }

        if (referenceSeries is null || referenceSeries.Points.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Round(normalizedPosition * Math.Max(0, referenceSeries.Points.Count - 1));
        index = Math.Clamp(index, 0, referenceSeries.Points.Count - 1);
        var targetTimestamp = referenceSeries.Points[index].Timestamp;

        var values = new List<HoverSeriesValue>(VisibleSeries.Count);
        foreach (var series in VisibleSeries)
        {
            if (series.Points.Count == 0) continue;
            var seriesIndex = (int)Math.Round(normalizedPosition * Math.Max(0, series.Points.Count - 1));
            var point = series.Points[Math.Clamp(seriesIndex, 0, series.Points.Count - 1)];
            values.Add(new HoverSeriesValue(series.Name, FormatValue(point.Value, Unit), series.StrokeBrush));
        }

        return values.Count == 0 ? null : new PanelHoverInfo(targetTimestamp, values);
    }

    public void RequestExpand() => ExpandRequested?.Invoke(this, EventArgs.Empty);

    public void ApplyRangePreset(TimeRangePreset preset)
    {
        _zoomWindowStartUtc = null;
        _zoomWindowEndUtc = null;
        if (_selectedRange != preset)
        {
            SelectedRange = preset;
            return;
        }

        RefreshPresentation();
    }

    public void ZoomToWindow(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc)
        {
            return;
        }

        _zoomWindowStartUtc = startUtc;
        _zoomWindowEndUtc = endUtc;
        RefreshPresentation();
    }

    public void ResetZoom()
    {
        if (!IsZoomed)
        {
            return;
        }

        _zoomWindowStartUtc = null;
        _zoomWindowEndUtc = null;
        // Resume live rendering with all buffered data that accumulated while frozen
        RefreshPresentation();
    }

    internal void RefreshPresentation(DateTimeOffset? anchor = null)
    {
        var liveAnchor = anchor ?? _latestPointTimestampUtc;
        var end = IsZoomed ? _zoomWindowEndUtc!.Value : liveAnchor;
        var start = IsZoomed ? _zoomWindowStartUtc!.Value : end.AddMinutes(-(int)SelectedRange);
        WindowStartUtc = start;
        WindowEndUtc = end;

        var pointBudget = ResolveVisiblePointBudget(end - start);
        var hasFocus = !string.IsNullOrEmpty(_focusedSeriesKey);
        var visibleSeries = new List<ChartSeriesViewModel>(_buffers.Count);
        foreach (var buffer in _buffers.Values)
        {
            if (!ShouldShowSeries(buffer))
            {
                continue;
            }

            var points = BuildVisiblePoints(buffer.Points, start, end, pointBudget, out var runStarts);
            if (points.Count == 0)
            {
                continue;
            }

            // When a series is focused, dim unfocused series by using a low-opacity brush
            // rather than hiding them entirely, so the user retains visual context.
            var isFocused = !hasFocus || string.Equals(buffer.Key, _focusedSeriesKey, StringComparison.OrdinalIgnoreCase);
            var strokeBrush = isFocused ? buffer.StrokeBrush : GetOrCreateDimmedBrush(buffer.StrokeBrush);
            var fillBrush = isFocused ? buffer.FillBrush : GetOrCreateDimmedBrush(buffer.FillBrush);

            visibleSeries.Add(new ChartSeriesViewModel(buffer.Key, buffer.Name, points, strokeBrush, fillBrush, runStarts));
        }

        // Sort series for panels with numbered entries (e.g., CPU cores) so they
        // appear in natural order: Core 0, Core 1, Core 2, ... rather than dictionary order.
        if (string.Equals(PanelKey, "cpu-cores", StringComparison.OrdinalIgnoreCase))
        {
            visibleSeries.Sort((a, b) => NaturalCompareSeriesKey(a.Key, b.Key));
        }

        VisibleSeries = visibleSeries.ToArray();
        UpdateSummaryText();
        FooterText = BuildFooterText(start, end);
    }

    /// <summary>Gets the latest value from a buffer by key, or 0 if not found.</summary>
    private double LatestBufferValue(string key)
    {
        return _buffers.TryGetValue(key, out var buf) && buf.Points.Count > 0
            ? buf.Points[^1].Value
            : 0d;
    }

    /// <summary>
    /// Gets the latest value of a series by name: the visible one first, then the stored samples,
    /// so a series hidden from the chart (by a legend focus, say) still feeds the headline.
    /// </summary>
    private double LatestSeriesValue(string name)
    {
        foreach (var s in VisibleSeries)
        {
            if (s.Points.Count > 0 && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return s.Points[^1].Value;
        }

        foreach (var buffer in _buffers.Values)
        {
            if (buffer.Points.Count > 0 &&
                !buffer.Key.StartsWith(ProcessSeriesPrefix, StringComparison.Ordinal) &&
                string.Equals(buffer.Name, name, StringComparison.OrdinalIgnoreCase))
                return buffer.Points[^1].Value;
        }

        return 0d;
    }

    private void UpdateSummaryText()
    {

        if (VisibleSeries.Count == 0)
        {
            if (string.Equals(PanelKey, "cpu-temperature", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(PanelKey, "gpu-temperature", StringComparison.OrdinalIgnoreCase))
            {
                CurrentValue = "Not detected";
                SecondaryValue = "No readable GPU sensor";
                EmptyStateText = "GPU temperature sensor not detected on this system";
            }
            else
            {
                CurrentValue = "Waiting";
                SecondaryValue = "Collecting hardware samples";
                EmptyStateText = "Waiting for samples";
            }

            ScaleLabel = string.Empty;
            ChartCeilingValue = Unit == MetricUnit.Percent ? 100d : Unit == MetricUnit.Count ? 100d : 0d;
            return;
        }

        EmptyStateText = "Waiting for samples";

        // No dictionary allocation — use LatestSeriesValue/LatestBufferValue helpers
        {
        }

        switch (Category)
        {
            case MetricCategory.Cpu when string.Equals(PanelKey, "cpu-total", StringComparison.OrdinalIgnoreCase):
                var cpuUsage = LatestSeriesValue("Usage");
                UtilizationPercent = cpuUsage;
                CurrentValue = $"{cpuUsage:0.#}%";
                SecondaryValue = BuildJoinedText(
                    _detailContext.CpuFrequencyMhz > 0 ? $"{_detailContext.CpuFrequencyMhz / 1000d:0.00} GHz" : "Total processor load",
                    _detailContext.ProcessCount > 0 ? $"{FormatCompactCount(_detailContext.ProcessCount)} processes" : null,
                    _detailContext.ThreadCount > 0 ? $"{FormatCompactCount(_detailContext.ThreadCount)} threads" : null);
                // The caption describes this chart's scale. Handle counts belong to Host Activity.
                ScaleLabel = "100% ceiling";
                ChartCeilingValue = 100d;
                break;
            case MetricCategory.Cpu:
                var coreCount = VisibleSeries.Count;
                var coreSum = 0d;
                foreach (var s in VisibleSeries)
                {
                    if (s.Points.Count > 0) coreSum += s.Points[^1].Value;
                }
                var averageCore = coreCount > 0 ? coreSum / coreCount : 0d;
                if (string.Equals(PanelKey, "cpu-frequency", StringComparison.OrdinalIgnoreCase))
                {
                    var clockMhz = LatestSeriesValue("Clock");
                    CurrentValue = $"{clockMhz / 1000d:0.00} GHz";
                    SecondaryValue = "Processor frequency";
                    ChartCeilingValue = ResolveDynamicCeiling(VisibleSeries, Math.Max(1000d, clockMhz));
                    ScaleLabel = $"Peak {FormatValue(ChartCeilingValue, Unit)}";
                    break;
                }

                if (string.Equals(PanelKey, "cpu-temperature", StringComparison.OrdinalIgnoreCase))
                {
                    var cpuTemp = LatestSeriesValue("Temperature");
                    CurrentValue = $"{cpuTemp:0.#} °C";
                    SecondaryValue = "Processor temperature";
                    ChartCeilingValue = ResolveDynamicCeiling(VisibleSeries, 100d);
                    ScaleLabel = $"Max {FormatValue(ChartCeilingValue, Unit)}";
                    break;
                }

                CurrentValue = $"{averageCore:0.#}% avg";
                SecondaryValue = BuildJoinedText(
                    coreCount == 1 ? "1 logical core" : $"{coreCount} logical cores",
                    _detailContext.CpuFrequencyMhz > 0 ? $"{_detailContext.CpuFrequencyMhz / 1000d:0.00} GHz" : null);
                ScaleLabel = $"100% per core / {coreCount} cores";
                ChartCeilingValue = 100d;
                break;
            case MetricCategory.Memory:
                var used = LatestSeriesValue("Used");
                // Use total-gb from the collector (reports TotalPhys directly) for an accurate ceiling.
                // Fallback to used + available if total-gb isn't available yet.
                var total = _buffers.TryGetValue("total-gb", out var totalBuf) && totalBuf.Points.Count > 0
                    ? totalBuf.Points[^1].Value
                    : 0d;
                if (total <= 0d)
                {
                    var available = _buffers.TryGetValue("available-gb", out var availBuf) && availBuf.Points.Count > 0
                        ? availBuf.Points[^1].Value
                        : 0d;
                    total = used + available;
                }
                var percent = total > 0 ? used / total * 100d : 0d;
                UtilizationPercent = percent;
                CurrentValue = $"{FormatCapacity(used)} used";
                SecondaryValue = BuildJoinedText(
                    $"{percent:0.#}% of {FormatCapacity(total)}",
                    _detailContext.ProcessCount > 0 ? $"{FormatCompactCount(_detailContext.ProcessCount)} proc" : null,
                    _detailContext.ThreadCount > 0 ? $"{FormatCompactCount(_detailContext.ThreadCount)} thr" : null);
                ScaleLabel = $"{FormatCapacity(total)} total";
                ChartCeilingValue = Math.Max(total, 1d);
                break;
            case MetricCategory.Gpu when string.Equals(PanelKey, "gpu-memory", StringComparison.OrdinalIgnoreCase):
                var dedicated = LatestSeriesValue("Dedicated");
                var peakVram = ResolveDynamicCeiling(VisibleSeries, Math.Max(1d, dedicated));
                CurrentValue = $"{FormatCapacity(dedicated)}";
                SecondaryValue = $"Dedicated VRAM · peak {FormatCapacity(peakVram)}";
                ChartCeilingValue = peakVram;
                ScaleLabel = $"{FormatCapacity(peakVram)} peak";
                break;
            case MetricCategory.Gpu when string.Equals(PanelKey, "gpu-temperature", StringComparison.OrdinalIgnoreCase):
                var gpuTemp = LatestSeriesValue("Temperature");
                CurrentValue = $"{gpuTemp:0.#} °C";
                SecondaryValue = "Graphics temperature";
                ChartCeilingValue = ResolveDynamicCeiling(VisibleSeries, 100d);
                ScaleLabel = $"Max {FormatValue(ChartCeilingValue, Unit)}";
                break;
            case MetricCategory.Gpu:
                UtilizationPercent = LatestSeriesValue("Usage");
                CurrentValue = $"{LatestSeriesValue("Usage"):0.#}%";
                SecondaryValue = "GPU usage";
                ChartCeilingValue = 100d;
                ScaleLabel = "100% ceiling";
                break;
            case MetricCategory.Disk when PrefersGaugeVisual:
                UtilizationPercent = LatestSeriesValue("Used");
                CurrentValue = $"{LatestSeriesValue("Used"):0.#}% used";
                var totalGb = LatestBufferValue("total-gb");
                var usedGb = LatestBufferValue("used-gb");
                SecondaryValue = totalGb > 0 ? $"{FormatCapacity(usedGb)} of {FormatCapacity(totalGb)}" : "Drive capacity";
                ScaleLabel = totalGb > 0 ? $"{FormatCapacity(totalGb)} total" : "100% ceiling";
                ChartCeilingValue = 100d;
                break;
            case MetricCategory.Disk:
                _detailContext.TryGetDriveDetail(PanelKey, out var driveDetail);
                CurrentValue = $"{LatestSeriesValue("Read"):0.0} MB/s read";
                SecondaryValue = BuildJoinedText(
                    $"{LatestSeriesValue("Write"):0.0} MB/s write",
                    driveDetail.TotalGigabytes > 0 ? $"{driveDetail.UsedPercent:0.#}% full" : null);
                ChartCeilingValue = ResolveDynamicCeiling(VisibleSeries, 10d);
                ScaleLabel = driveDetail.TotalGigabytes > 0
                    ? $"Peak {FormatValue(ChartCeilingValue, Unit)} · {FormatCapacity(driveDetail.TotalGigabytes)} total"
                    : $"Peak {FormatValue(ChartCeilingValue, Unit)}";
                break;
            case MetricCategory.Network:
                CurrentValue = $"{LatestSeriesValue("Down"):0.0} Mbps down";
                SecondaryValue = $"{LatestSeriesValue("Up"):0.0} Mbps up";
                ChartCeilingValue = ResolveDynamicCeiling(VisibleSeries, 10d);
                ScaleLabel = $"Peak {FormatValue(ChartCeilingValue, Unit)}";
                break;
            case MetricCategory.System:
                CurrentValue = $"{FormatCompactCount(LatestSeriesValue("Processes"))} proc";
                SecondaryValue = BuildJoinedText(
                    $"{FormatCompactCount(LatestSeriesValue("Threads"))} thr",
                    $"{FormatCompactCount(LatestSeriesValue("Handles"))} handles");
                ChartCeilingValue = ResolveDynamicCeiling(VisibleSeries, 100d);
                ScaleLabel = $"Peak {FormatValue(ChartCeilingValue, Unit)}";
                break;
            default:
                CurrentValue = FormatValue(VisibleSeries[0].Points.Last().Value, Unit);
                SecondaryValue = "Live metric";
                ChartCeilingValue = ResolveDynamicCeiling(VisibleSeries, 1d);
                ScaleLabel = $"Peak {FormatValue(ChartCeilingValue, Unit)}";
                break;
        }
    }

    private bool ShouldShowSeries(SeriesBuffer buffer)
    {
        var isProcessSeries = buffer.Key.StartsWith(ProcessSeriesPrefix, StringComparison.Ordinal);

        // Hide per-process chart series when no processes are pinned
        if (isProcessSeries && !_perProcessChartsEnabled)
        {
            return false;
        }

        // Pinned processes are drawn beside the panel's own line, not instead of it. Hiding it
        // left a chart that had just been pinned to blank - a process line has no history before
        // its pin - and the headline, read from the visible series, dropped to 0%.

        // Memory panel: only show used-gb (hide available-gb and total-gb to keep chart clean)
        if (string.Equals(PanelKey, "memory", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(buffer.Key, "available-gb", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(buffer.Key, "total-gb", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!PrefersGaugeVisual)
        {
            return true;
        }

        return string.Equals(buffer.Key, "used-percent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maximum points per series buffer to cap memory usage.</summary>
    // With older samples folded into minutes, this is an hour of raw samples plus about 40 days
    // of minute averages.
    private const int MaxBufferPointsPerSeries = 60_000;

    /// <summary>
    /// How long live samples stay sample by sample. Older ones are folded into minute averages:
    /// the form the store keeps them in and a restart reloads them as (see RawHistoryWindow in
    /// ShellWindow), so a long session's 24h chart is the chart you see after restarting.
    /// </summary>
    internal static readonly TimeSpan RawSampleWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan MinuteBucket = TimeSpan.FromMinutes(1);

    /// <summary>Where a buffer past its cap is coarsened to, instead of losing its oldest points.</summary>
    private static readonly TimeSpan CoarseBucket = TimeSpan.FromMinutes(10);

    /// <summary>The start of the minute holding <paramref name="value"/>, as the store buckets it.</summary>
    internal static DateTimeOffset MinuteFloor(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds() / 60_000 * 60_000);

    private void TrimBuffers(DateTimeOffset anchor)
    {
        var keepAfter = anchor - _retentionWindow;
        // Whole minutes only, so every folded average covers a full minute, as a rollup does.
        var rawFrom = MinuteFloor(anchor - RawSampleWindow);
        foreach (var buffer in _buffers.Values)
        {
            // Binary search for the cutoff — O(log N) instead of O(N) RemoveAll
            var points = buffer.Points;
            if (points.Count > 0 && points[0].Timestamp < keepAfter)
            {
                var lo = 0;
                var hi = points.Count - 1;
                while (lo < hi)
                {
                    var mid = lo + ((hi - lo) >> 1);
                    if (points[mid].Timestamp < keepAfter)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                if (lo > 0)
                {
                    points.RemoveRange(0, lo);
                }
            }

            var folded = 0;
            if (rawFrom > buffer.FoldedThroughUtc)
            {
                folded = MetricHistory.Compact(points, buffer.FoldedThroughUtc, rawFrom, MinuteBucket);
                buffer.FoldedThroughUtc = rawFrom;
            }

            // Past the cap, the older half is coarsened rather than dropped. Dropping it emptied
            // the start of long ranges after a day or so in the tray, and nothing reloaded it.
            if (points.Count > MaxBufferPointsPerSeries)
            {
                folded += MetricHistory.Compact(points, DateTimeOffset.MinValue, points[points.Count / 2].Timestamp, CoarseBucket);
                if (points.Count > MaxBufferPointsPerSeries)
                {
                    points.RemoveRange(0, points.Count - MaxBufferPointsPerSeries);
                }
            }

            if (folded > 0)
            {
                // Averages flatten short spikes; the peak follows what is now held, as it would
                // after a reload.
                var peak = 0d;
                foreach (var point in points)
                {
                    if (point.Value > peak) peak = point.Value;
                }

                buffer.TrackedPeak = peak;
            }

            buffer.LastTrimUtc = anchor;
        }
    }

    private void RefreshProcessRows()
    {
        if (!SupportsProcessTable)
        {
            ProcessRows = Array.Empty<ProcessListItemViewModel>();
            return;
        }

        var sourceProcesses = _detailContext.Processes;
        if (sourceProcesses.Count == 0)
        {
            ProcessRows = Array.Empty<ProcessListItemViewModel>();
            return;
        }

        // Use total system memory for meter scaling (not peak process)
        // so bars represent proportion of total RAM, not relative to each other
        var totalMemory = LatestBufferValue("total-gb");
        if (totalMemory <= 0)
        {
            totalMemory = LatestBufferValue("used-gb") + LatestBufferValue("available-gb");
        }
        totalMemory = Math.Max(1d, totalMemory);

        // Copy to array for in-place sort (avoids LINQ OrderBy allocations)
        var sorted = new ProcessActivitySample[sourceProcesses.Count];
        for (var i = 0; i < sourceProcesses.Count; i++)
        {
            sorted[i] = sourceProcesses[i];
        }

        var isMemoryPanel = string.Equals(PanelKey, "memory", StringComparison.OrdinalIgnoreCase);
        var sortMode = ProcessSortMode;
        Array.Sort(sorted, (a, b) =>
        {
            int cmp;
            switch (sortMode)
            {
                case ProcessSortMode.Name:
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                case ProcessSortMode.NameReverse:
                    return string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase);
                case ProcessSortMode.Lowest when isMemoryPanel:
                    cmp = a.MemoryGigabytes.CompareTo(b.MemoryGigabytes);
                    return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                case ProcessSortMode.Lowest:
                    cmp = a.CpuPercent.CompareTo(b.CpuPercent);
                    return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                default:
                    if (isMemoryPanel)
                    {
                        cmp = b.MemoryGigabytes.CompareTo(a.MemoryGigabytes);
                        return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    }
                    cmp = b.CpuPercent.CompareTo(a.CpuPercent);
                    return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
        });

        _totalProcessCount = sorted.Length;
        // Cap visible rows — fewer rows = less DOM, less layout cost
        const int defaultVisibleRows = 15;
        const int expandedVisibleRows = 50;
        var visibleCount = _processListExpanded
            ? Math.Min(sorted.Length, expandedVisibleRows)
            : Math.Min(sorted.Length, defaultVisibleRows);
        var rows = new ProcessListItemViewModel[visibleCount];
        for (var i = 0; i < visibleCount; i++)
        {
            rows[i] = CreateProcessRow(sorted[i], totalMemory);
        }

        ProcessRows = rows;
    }

    private ProcessListItemViewModel CreateProcessRow(ProcessActivitySample process, double totalMemory)
    {
        var isPinned = _pinnedProcesses.Contains(process.Name);
        var seriesKey = $"{ProcessSeriesPrefix}{process.Name}";
        Brush? chartColor = _perProcessChartsEnabled && _buffers.TryGetValue(seriesKey, out var buf) ? buf.StrokeBrush : null;

        if (string.Equals(PanelKey, "memory", StringComparison.OrdinalIgnoreCase))
        {
            // Meter bar shows proportion of TOTAL system RAM, not relative to peak process
            return new ProcessListItemViewModel(
                $"{process.ProcessId}",
                process.Name,
                FormatCapacity(process.MemoryGigabytes),
                $"{process.CpuPercent:0.#}% cpu · {FormatCompactCount(process.ThreadCount)} threads",
                Math.Clamp(process.MemoryGigabytes / totalMemory, 0d, 1d),
                isPinned,
                chartColor);
        }

        return new ProcessListItemViewModel(
            $"{process.ProcessId}",
            process.Name,
            $"{process.CpuPercent:0.#}%",
            $"{FormatCompactCount(process.ThreadCount)} threads · PID {process.ProcessId}",
            Math.Clamp(process.CpuPercent / 100d, 0d, 1d),
            isPinned,
            chartColor);
    }

    private SolidColorBrush ResolveBrush(int index)
    {
        var color = Category switch
        {
            MetricCategory.Cpu when index == 0 => BrushFactory.ParseColor("#5DE6FF"),
            MetricCategory.Cpu => index % 2 == 0 ? BrushFactory.ParseColor("#3BB7FF") : BrushFactory.ParseColor("#8BF7FF"),
            MetricCategory.Memory when index == 0 => BrushFactory.ParseColor("#7BF7D0"),
            MetricCategory.Memory => BrushFactory.ParseColor("#54C99B"),
            MetricCategory.Gpu when index == 0 => BrushFactory.ParseColor("#7FB4FF"),
            MetricCategory.Gpu => index % 2 == 0 ? BrushFactory.ParseColor("#5E96F4") : BrushFactory.ParseColor("#9CC7FF"),
            // Violet, not amber. Disk previously used #FF9B54/#FFD166, which sit in the same
            // hue family as WarningBrush (#F2BF66) and CriticalBrush (#FF897D) — so a drive at
            // 37% and a drive at 86% rendered in indistinguishable orange and the threshold
            // colouring carried no information on exactly the panels where capacity matters most.
            MetricCategory.Disk when index == 0 => BrushFactory.ParseColor("#B08BF0"),
            MetricCategory.Disk => BrushFactory.ParseColor("#C9AEF7"),
            MetricCategory.Network when index == 0 => BrushFactory.ParseColor("#6CC7FF"),
            MetricCategory.Network => BrushFactory.ParseColor("#4C9FE0"),
            MetricCategory.System when index == 0 => BrushFactory.ParseColor("#79C7FF"),
            MetricCategory.System => BrushFactory.ParseColor("#4FA3FF"),
            _ => BrushFactory.ParseColor("#7AD8FF"),
        };

        return new SolidColorBrush(color);
    }

    // Cache dimmed brushes to avoid creating new brush objects on every render tick
    private static readonly Dictionary<Brush, Brush> s_dimmedBrushCache = new(ReferenceEqualityComparer.Instance);

    private static Brush GetOrCreateDimmedBrush(Brush source)
    {
        if (s_dimmedBrushCache.TryGetValue(source, out var cached))
        {
            return cached;
        }

        var dimmed = CreateDimmedBrush(source);
        s_dimmedBrushCache[source] = dimmed;
        return dimmed;
    }

    private static Brush CreateDimmedBrush(Brush source)
    {
        if (source is SolidColorBrush solid)
        {
            var color = solid.Color;
            return new SolidColorBrush(ColorHelper.FromArgb((byte)(color.A * 0.18), color.R, color.G, color.B));
        }

        // For gradient brushes (fill), just reduce overall opacity
        if (source is LinearGradientBrush gradient)
        {
            var dimmed = new LinearGradientBrush
            {
                StartPoint = gradient.StartPoint,
                EndPoint = gradient.EndPoint,
            };
            foreach (var stop in gradient.GradientStops)
            {
                var c = stop.Color;
                dimmed.GradientStops.Add(new GradientStop
                {
                    Color = ColorHelper.FromArgb((byte)(c.A * 0.18), c.R, c.G, c.B),
                    Offset = stop.Offset,
                });
            }
            return dimmed;
        }

        return source;
    }

    private Brush ResolveFillBrush(int index) => CreateFillBrush(ResolveBrush(index).Color);

    private static Brush CreateFillBrush(Windows.UI.Color color)
    {
        // Gradient fill: series color at the top fading to transparent at the bottom.
        // Creates a clean "mountain" silhouette like Grafana/Datadog dashboards.
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                // Richer at the crest and falling away faster. The previous 64 top alpha was
                // further multiplied by the chart's 0.7 layer opacity, landing near 18% — enough
                // to tint the plot bed but too weak to give the trace any body.
                new GradientStop { Color = ColorHelper.FromArgb(120, color.R, color.G, color.B), Offset = 0 },
                new GradientStop { Color = ColorHelper.FromArgb(38, color.R, color.G, color.B), Offset = 0.45 },
                new GradientStop { Color = ColorHelper.FromArgb(0, color.R, color.G, color.B), Offset = 1.0 },
            },
        };
    }

    private static string FormatValue(double value, MetricUnit unit) => unit switch
    {
        MetricUnit.Percent => $"{value:0.#}%",
        MetricUnit.Celsius => $"{value:0.#} °C",
        MetricUnit.Gigabytes => FormatCapacity(value),
        MetricUnit.MegabytesPerSecond => $"{value:0.0} MB/s",
        MetricUnit.MegabitsPerSecond => $"{value:0.0} Mbps",
        MetricUnit.Megahertz => $"{value / 1000d:0.00} GHz",
        MetricUnit.Count => FormatCompactCount(value),
        _ => $"{value:0.##}",
    };

    private static string BuildJoinedText(string? a, string? b = null, string? c = null)
    {
        var hasA = !string.IsNullOrWhiteSpace(a);
        var hasB = !string.IsNullOrWhiteSpace(b);
        var hasC = !string.IsNullOrWhiteSpace(c);

        if (!hasA && !hasB && !hasC) return "Live metric";
        if (hasA && !hasB && !hasC) return a!;
        if (hasA && hasB && !hasC) return $"{a} · {b}";
        if (hasA && hasB && hasC) return $"{a} · {b} · {c}";
        if (hasA && !hasB && hasC) return $"{a} · {c}";
        if (!hasA && hasB) return hasC ? $"{b} · {c}" : b!;
        return c!;
    }

    private static string FormatCompactCount(double value)
    {
        value = Math.Max(0d, value);
        if (value >= 1_000_000d)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000d)
        {
            return $"{value / 1_000d:0.#}k";
        }

        return $"{value:0}";
    }

    /// <summary>
    /// Compares series keys with natural numeric ordering so "core-2" sorts before "core-10".
    /// </summary>
    private static int NaturalCompareSeriesKey(string a, string b)
    {
        // Extract trailing numeric suffix for natural sort
        var numA = ExtractTrailingNumber(a);
        var numB = ExtractTrailingNumber(b);
        if (numA >= 0 && numB >= 0)
        {
            return numA.CompareTo(numB);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractTrailingNumber(string key)
    {
        var lastDash = key.LastIndexOf('-');
        if (lastDash < 0 || lastDash >= key.Length - 1)
        {
            return -1;
        }

        return int.TryParse(key.AsSpan(lastDash + 1), out var num) ? num : -1;
    }

    private static string FormatCapacity(double gigabytes)
    {
        if (gigabytes >= 1024d)
        {
            return $"{gigabytes / 1024d:0.0} TiB";
        }

        return $"{gigabytes:0.0} GiB";
    }

    private double ResolveDynamicCeiling(IReadOnlyList<ChartSeriesViewModel> series, double minimum)
    {
        // Use tracked peak from buffers instead of iterating all visible points.
        // This avoids O(N) iteration on every snapshot for panels with large datasets.
        var peak = minimum;
        foreach (var buffer in _buffers.Values)
        {
            if (!ShouldShowSeries(buffer))
            {
                continue;
            }

            if (buffer.TrackedPeak > peak)
            {
                peak = buffer.TrackedPeak;
            }
        }

        return Math.Max(minimum, peak * 1.12d);
    }

    private string BuildFooterText(DateTimeOffset start, DateTimeOffset end)
    {
        if (IsZoomed)
        {
            var span = end - start;
            var spanText = span.TotalMinutes >= 1d
                ? $"{span.TotalMinutes:0.#}m"
                : $"{span.TotalSeconds:0.#}s";
            return PrefersGaugeVisual
                ? $"capacity / zoomed {spanText}"
                : $"zoomed {spanText}";
        }

        return PrefersGaugeVisual
            ? $"capacity / {FormatRangeLabel((int)SelectedRange)} replay"
            : $"{FormatRangeLabel((int)SelectedRange)} replay";
    }

    /// <summary>
    /// The points of one series to draw in [start, end], at most about <paramref name="pointBudget"/>,
    /// and where the line breaks for an outage. Outages are found here, on the stored samples:
    /// after min/max bucketing the spacing is uneven, and a guess from the bucketed points either
    /// joined two outages with a straight line or broke noisy data into fragments.
    /// </summary>
    private static IReadOnlyList<MetricPoint> BuildVisiblePoints(
        IReadOnlyList<MetricPoint> points,
        DateTimeOffset start,
        DateTimeOffset end,
        int pointBudget,
        out IReadOnlyList<int> runStarts)
    {
        runStarts = Array.Empty<int>();
        if (points.Count == 0)
        {
            return Array.Empty<MetricPoint>();
        }

        // Binary search for start index (points are sorted by timestamp)
        var lo = 0;
        var hi = points.Count - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (points[mid].Timestamp < start)
                lo = mid + 1;
            else
                hi = mid;
        }

        // Count in-window points without copying first
        var endIndex = lo;
        while (endIndex < points.Count && points[endIndex].Timestamp <= end)
        {
            endIndex++;
        }

        var windowCount = endIndex - lo;
        if (windowCount == 0)
        {
            return Array.Empty<MetricPoint>();
        }

        // Deterministic min/max bucket downsampling: fixed-width buckets on absolute time (not
        // point indices), min and max kept per bucket, so a window always draws the same way
        // whenever the render happens. Gaps narrower than a bucket and a half could not show.
        var bucketCount = Math.Max(2, pointBudget / 2);
        var windowMs = Math.Max(1d, (end - start).TotalMilliseconds);
        var bucketWidthMs = windowMs / bucketCount;
        var startMs = (double)start.ToUnixTimeMilliseconds();
        var gapStarts = ChartPointReducer.FindRunStarts(points, lo, endIndex, Math.Max(10_000d, bucketWidthMs * 1.5));
        var downsample = windowCount > pointBudget;

        var sampled = new List<MetricPoint>(Math.Min(windowCount, pointBudget + 8));
        var outputRunStarts = gapStarts.Count == 0 ? null : new List<int>(gapStarts.Count);
        var runFrom = lo;
        for (var run = 0; run <= gapStarts.Count; run++)
        {
            var runTo = run < gapStarts.Count ? gapStarts[run] : endIndex;
            if (run > 0)
            {
                outputRunStarts!.Add(sampled.Count);
            }

            if (!downsample)
            {
                for (var i = runFrom; i < runTo; i++)
                {
                    sampled.Add(points[i]);
                }
            }
            else
            {
                AppendBucketed(points, runFrom, runTo, startMs, bucketWidthMs, sampled);
            }

            runFrom = runTo;
        }

        if (outputRunStarts is not null)
        {
            runStarts = outputRunStarts;
        }

        return sampled.ToArray();
    }

    /// <summary>Min and max per time bucket over one run, plus its first and last point.</summary>
    private static void AppendBucketed(
        IReadOnlyList<MetricPoint> points,
        int from,
        int to,
        double startMs,
        double bucketWidthMs,
        List<MetricPoint> output)
    {
        MetricPoint? lastEmitted = null;
        void Emit(MetricPoint point)
        {
            if (!ReferenceEquals(point, lastEmitted))
            {
                output.Add(point);
                lastEmitted = point;
            }
        }

        // The run's first point anchors the line at the left edge of the run.
        Emit(points[from]);
        var currentBucket = long.MinValue;
        MetricPoint minPoint = points[from];
        MetricPoint maxPoint = points[from];
        for (var i = from; i < to; i++)
        {
            var point = points[i];
            var bucket = (long)Math.Floor((point.Timestamp.ToUnixTimeMilliseconds() - startMs) / bucketWidthMs);
            if (bucket != currentBucket)
            {
                if (currentBucket != long.MinValue)
                {
                    EmitBucket(minPoint, maxPoint, Emit);
                }

                currentBucket = bucket;
                minPoint = point;
                maxPoint = point;
                continue;
            }

            if (point.Value < minPoint.Value) minPoint = point;
            if (point.Value > maxPoint.Value) maxPoint = point;
        }

        EmitBucket(minPoint, maxPoint, Emit);

        // And its last point, for visual continuity up to the latest sample.
        Emit(points[to - 1]);
    }

    private static void EmitBucket(MetricPoint minPoint, MetricPoint maxPoint, Action<MetricPoint> emit)
    {
        if (minPoint.Timestamp <= maxPoint.Timestamp)
        {
            emit(minPoint);
            emit(maxPoint);
        }
        else
        {
            emit(maxPoint);
            emit(minPoint);
        }
    }

    private static int ResolveVisiblePointBudget(TimeSpan window) => window switch
    {
        _ when window >= TimeSpan.FromDays(365) => 120,
        _ when window >= TimeSpan.FromDays(90) => 150,
        _ when window >= TimeSpan.FromDays(30) => 180,
        _ when window >= TimeSpan.FromDays(7) => 220,
        _ when window >= TimeSpan.FromDays(2) => 260,
        _ when window >= TimeSpan.FromDays(1) => 280,
        _ when window >= TimeSpan.FromHours(12) => 320,
        _ when window >= TimeSpan.FromHours(1) => 380,
        _ => 450,
    };

    private static string FormatRangeLabel(int minutes)
    {
        if (minutes >= 525600)
        {
            return "1y";
        }

        if (minutes >= 129600)
        {
            return "90d";
        }

        if (minutes >= 43200)
        {
            return "30d";
        }

        if (minutes >= 10080)
        {
            return "7d";
        }

        if (minutes >= 7200)
        {
            return "5d";
        }

        if (minutes >= 2880)
        {
            return "2d";
        }

        if (minutes >= 1440)
        {
            return "24h";
        }

        if (minutes >= 720)
        {
            return "12h";
        }

        if (minutes >= 60)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
    }

    private sealed class SeriesBuffer
    {
        public SeriesBuffer(
            string key,
            string name,
            List<MetricPoint> points,
            SolidColorBrush strokeBrush,
            Brush fillBrush,
            DateTimeOffset lastTrimUtc)
        {
            Key = key;
            Name = name;
            Points = points;
            StrokeBrush = strokeBrush;
            FillBrush = fillBrush;
            LastTrimUtc = lastTrimUtc;
            BaseColor = strokeBrush.Color;
            RefreshColors();

            // Compute initial peak from loaded history
            var peak = 0d;
            foreach (var point in points)
            {
                if (point.Value > peak) peak = point.Value;
            }
            TrackedPeak = peak;
        }

        /// <summary>The colour the series was given, before the palette adapts it.</summary>
        public Windows.UI.Color BaseColor { get; }

        /// <summary>Recolours the line and fill in place for the current palette. Every chart,
        /// legend dot and process-row dot holding these brushes follows without a rebuild.</summary>
        public void RefreshColors()
        {
            var color = ThemeVisuals.AdaptSeriesColor(BaseColor);
            StrokeBrush.Color = color;
            if (FillBrush is LinearGradientBrush fill)
            {
                foreach (var stop in fill.GradientStops)
                {
                    stop.Color = ColorHelper.FromArgb(stop.Color.A, color.R, color.G, color.B);
                }
            }
        }

        public string Key { get; }

        public string Name { get; }

        public List<MetricPoint> Points { get; }

        public SolidColorBrush StrokeBrush { get; }

        public Brush FillBrush { get; }

        public DateTimeOffset LastTrimUtc { get; set; }

        /// <summary>Points before this are already minute averages (or coarser).</summary>
        public DateTimeOffset FoldedThroughUtc { get; set; } = DateTimeOffset.MinValue;

        /// <summary>Tracked maximum value across all points, updated on append. Avoids O(N) scans.</summary>
        public double TrackedPeak { get; set; }
    }
}

public sealed class ChartSeriesViewModel
{
    public ChartSeriesViewModel(string key, string name, IReadOnlyList<MetricPoint> points, Brush strokeBrush, Brush fillBrush, IReadOnlyList<int>? runStarts = null)
    {
        Key = key;
        Name = name;
        Points = points;
        StrokeBrush = strokeBrush;
        FillBrush = fillBrush;
        RunStarts = runStarts ?? Array.Empty<int>();
    }

    public string Key { get; }

    public string Name { get; }

    public IReadOnlyList<MetricPoint> Points { get; }

    /// <summary>
    /// Indices into <see cref="Points"/> where the line starts again after an outage. Found on the
    /// stored samples before downsampling, so the chart breaks exactly there and never has to
    /// guess from bucketed points.
    /// </summary>
    public IReadOnlyList<int> RunStarts { get; }

    public Brush StrokeBrush { get; }

    public Brush FillBrush { get; }
}

public sealed class PanelHoverInfo
{
    public PanelHoverInfo(DateTimeOffset timestamp, IReadOnlyList<HoverSeriesValue> values)
    {
        Timestamp = timestamp;
        Values = values;
    }

    public DateTimeOffset Timestamp { get; }

    public IReadOnlyList<HoverSeriesValue> Values { get; }
}

public sealed class HoverSeriesValue
{
    public HoverSeriesValue(string label, string value, Brush brush)
    {
        Label = label;
        Value = value;
        Brush = brush;
    }

    public string Label { get; }

    public string Value { get; }

    public Brush Brush { get; }
}

internal static class BrushFactory
{
    public static SolidColorBrush CreateBrush(string hex) => new(ParseColor(hex));

    public static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6)
        {
            value = $"FF{value}";
        }

        return ColorHelper.FromArgb(
            Convert.ToByte(value[0..2], 16),
            Convert.ToByte(value[2..4], 16),
            Convert.ToByte(value[4..6], 16),
            Convert.ToByte(value[6..8], 16));
    }
}
