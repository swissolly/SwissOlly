using SwissOlly.Core.Models;

namespace SwissOlly.App.Controls;

/// <summary>
/// Every theme's palette and how Settings presents it. App applies these palettes and the theme
/// picker draws its previews from the same values, so a preview cannot drift from its theme (the
/// picker used to keep its own copy of each accent, and Nord's had already drifted).
/// </summary>
/// <remarks>
/// Every palette keeps the same structural relationships - backdrop darkest, cards lifted one
/// step, strokes as hairlines, secondary and muted text at least 4.5:1 on every surface - so a new
/// palette is a recolour, never a re-layout. Warning and Critical stay in the amber/red family in
/// all of them because they encode threshold meaning, not style; chart series keep their hues for
/// the same reason, darkened on light palettes (ThemeVisuals.AdaptSeriesColor).
/// </remarks>
internal static class ThemeCatalog
{
    /// <summary>All themes in the order the picker shows them: dark first, then light.</summary>
    public static IReadOnlyList<ThemeInfo> All { get; } =
    [
        // Palette field order: AppBackdrop, ShellBackground, ShellStroke, Surface, SurfaceElevated,
        // SurfaceStrong, SurfaceInset, SurfaceStroke, SurfaceHighlight, PanelOverlay, SurfaceGrid,
        // TextPrimary, TextSecondary, TextMuted, Accent, AccentStrong, AccentSoft, AccentHalo,
        // WarningHalo, Warning, Critical, OverlayScrim.
        new(ThemeMode.Dark, "Dark", "Neutral graphite", IsLight: false, new(
            "#111315", "#17191C", "#202327", "#1C1F22", "#222529", "#292D31",
            "#141619", "#2C3035", "#2BFFFFFF", "#25282C", "#2B3036",
            "#F4F5F7", "#C4C8CE", "#90959D",
            "#79B8FF", "#B6D9FF", "#294A6B",
            "#2479B8FF", "#20E6A34B", "#F2BF66", "#FF897D", "#B0080A0C")),

        // True black backdrop: OLED pixels switch off. The chrome is monochrome so the charts are
        // the only colour on screen.
        new(ThemeMode.Obsidian, "Obsidian", "True black for OLED", IsLight: false, new(
            "#000000", "#050506", "#17171A", "#0A0A0B", "#111113", "#18181B",
            "#030304", "#1F1F23", "#22FFFFFF", "#131315", "#1F1F24",
            "#F4F4F5", "#BFBFC7", "#8C8C96",
            "#E4E4E7", "#FFFFFF", "#2A2A2F",
            "#1FFFFFFF", "#20E6A34B", "#F2BF66", "#FF897D", "#C8000000")),

        new(ThemeMode.Midnight, "Midnight", "Blue-black and cyan", IsLight: false, new(
            "#070B14", "#0C1220", "#182033", "#101728", "#17203A", "#1E294A",
            "#0A0F1C", "#1F2A42", "#2BFFFFFF", "#141C30", "#1E2840",
            "#EEF2FA", "#BCC7DD", "#8993AA",
            "#5CC8F5", "#A5E2FA", "#1B3A58",
            "#245CC8F5", "#20E6A34B", "#F2BF66", "#FF897D", "#B0060A12")),

        new(ThemeMode.Nord, "Nord", "Soft arctic blue-grey", IsLight: false, new(
            "#2E3440", "#333B48", "#434C5E", "#3B4252", "#3E4656", "#424A5B",
            "#2B313C", "#4A5467", "#2BFFFFFF", "#3E4657", "#49536A",
            "#ECEFF4", "#D8DEE9", "#B3BBC9",
            "#8DC3D2", "#B4DAE5", "#3B5A68",
            "#2488C0D0", "#20E6A34B", "#EBCB8B", "#EDAAB3", "#B0242933")),

        new(ThemeMode.Violet, "Violet", "Plum and lavender", IsLight: false, new(
            "#110E16", "#17131E", "#271F33", "#1C1726", "#231D2F", "#2B2439",
            "#13101A", "#2E2640", "#2BFFFFFF", "#211B2C", "#2C2440",
            "#F3EFF8", "#CBC2DA", "#958CA5",
            "#B18AF0", "#D4BDFF", "#38295C",
            "#24B18AF0", "#20E6A34B", "#F2BF66", "#FF897D", "#B00C0A12")),

        // Neon magenta on deep indigo. Critical stays coral, well clear of the magenta accent.
        new(ThemeMode.Synthwave, "Synthwave", "Neon pink on indigo", IsLight: false, new(
            "#0C0817", "#110C20", "#221A3B", "#151027", "#1C1532", "#241B3F",
            "#0E0A1C", "#2B2147", "#2BFFFFFF", "#1A1430", "#2D2350",
            "#F8F3FF", "#D3C7EE", "#9C8FC2",
            "#FF5CD1", "#FFA3E6", "#4A1D4E",
            "#24FF5CD1", "#20E6A34B", "#F2BF66", "#FF897D", "#B0080512")),

        new(ThemeMode.Forest, "Forest", "Deep green and mint", IsLight: false, new(
            "#0D120F", "#121815", "#1E2A23", "#161D19", "#1C2520", "#232E27",
            "#0F1411", "#26322B", "#2BFFFFFF", "#1A231E", "#243029",
            "#EFF5F1", "#C2D0C7", "#8B978F",
            "#6FD8A4", "#A8EFC9", "#1E4433",
            "#246FD8A4", "#20E6A34B", "#F2BF66", "#FF897D", "#B00A0F0C")),

        new(ThemeMode.Ember, "Ember", "Warm charcoal and amber", IsLight: false, new(
            "#14100E", "#1A1512", "#2A221D", "#1F1915", "#261E19", "#2E251F",
            "#17120F", "#312822", "#2BFFFFFF", "#241C17", "#2F2620",
            "#F7F2EC", "#D2C6BA", "#9A8D81",
            "#F0A04B", "#FFC888", "#4A3321",
            "#24F0A04B", "#20E6A34B", "#F2BF66", "#FF897D", "#B00F0C0A")),

        new(ThemeMode.Light, "Light", "Clean and neutral", IsLight: true, new(
            "#F2F3F5", "#FFFFFF", "#E4E6E9", "#FFFFFF", "#FAFAFB", "#F4F5F6",
            "#F1F2F4", "#E7E8EA", "#C8FFFFFF", "#F4F5F6", "#E5E7EA",
            "#1C1D1F", "#45484D", "#686D73",
            "#1769C2", "#0D4F99", "#E5EFFB",
            "#1A1769C2", "#18C08326", "#9D5E00", "#BD4238", "#80E7E8EA")),

        // Cool, slightly blue whites with an indigo accent.
        new(ThemeMode.Frost, "Frost", "Cool white and indigo", IsLight: true, new(
            "#E9EEF6", "#F7F9FC", "#D9E0EC", "#F9FBFE", "#F3F6FB", "#EAF0F8",
            "#EEF2F8", "#DBE2EE", "#C8FFFFFF", "#EEF2F8", "#DDE4EF",
            "#141B2D", "#3D4760", "#5A6480",
            "#4F46E5", "#3730A3", "#E3E4FB",
            "#1A4F46E5", "#18C08326", "#94580A", "#B53C33", "#80DBE2EE")),

        new(ThemeMode.Sandstone, "Sandstone", "Warm paper and sage", IsLight: true, new(
            "#EDE9E3", "#FAF8F5", "#E0DAD1", "#FAF8F5", "#F5F2EC", "#EDE8E0",
            "#F0ECE5", "#E2DCD3", "#C8FFFFFF", "#EFEBE4", "#E4DED5",
            "#241F1A", "#4A443C", "#6D665D",
            "#3F6B5C", "#2C5347", "#DDE9E4",
            "#1A3F6B5C", "#18C08326", "#915C11", "#AF4437", "#80E2DCD3")),

        // Blush surfaces with a raspberry accent; Critical is a brick red so it never reads as
        // the accent.
        new(ThemeMode.Blossom, "Blossom", "Blush and raspberry", IsLight: true, new(
            "#F6EEF1", "#FCF8F9", "#EBDDE2", "#FFFBFC", "#FBF4F6", "#F5EBEF",
            "#F7EFF2", "#EDDFE4", "#C8FFFFFF", "#F6EDF0", "#EEDFE5",
            "#2A1A20", "#57434B", "#735C65",
            "#C2185B", "#8E1043", "#F9DCE7",
            "#1AC2185B", "#18C08326", "#8F5A10", "#AC3B32", "#80EDDFE4")),
    ];

    private static readonly Dictionary<ThemeMode, ThemeInfo> s_byMode = All.ToDictionary(theme => theme.Mode);

    /// <summary>The theme's entry; an undefined value falls back to Dark, as config loading does.</summary>
    public static ThemeInfo Get(ThemeMode mode) =>
        s_byMode.TryGetValue(mode, out var theme) ? theme : s_byMode[ThemeMode.Dark];
}

internal sealed record ThemeInfo(ThemeMode Mode, string Label, string Description, bool IsLight, ThemePalette Palette);

internal sealed record ThemePalette(
    string AppBackdrop,
    string ShellBackground,
    string ShellStroke,
    string Surface,
    string SurfaceElevated,
    string SurfaceStrong,
    string SurfaceInset,
    string SurfaceStroke,
    string SurfaceHighlight,
    string PanelOverlay,
    string SurfaceGrid,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string Accent,
    string AccentStrong,
    string AccentSoft,
    string AccentHalo,
    string WarningHalo,
    string Warning,
    string Critical,
    string OverlayScrim);
