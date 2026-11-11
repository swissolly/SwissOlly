using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using SwissOlly.App.ViewModels;

namespace SwissOlly.App.Controls;

internal static class IconFactory
{
    private static readonly FontFamily FluentIconFont = new("Segoe Fluent Icons");

    public static FrameworkElement CreateTile(string key, Brush accentBrush, double size = 44, double iconSize = 18)
    {
        var corner = Math.Clamp(size * 0.2, 4, 8);
        var shades = GetShades(accentBrush);

        // One structure for every palette, so a theme change only recolours it. Light palettes
        // clear the glow; the tile used to be built without it there.
        return new Border
        {
            Width = size,
            Height = size,
            Background = shades.TileFill,
            BorderBrush = shades.TileStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(corner),
            Child = new Grid
            {
                Children =
                {
                    new Border
                    {
                        Margin = new Thickness(2),
                        CornerRadius = new CornerRadius(Math.Max(10, corner - 2)),
                        Background = shades.Glow,
                        Opacity = 0.16,
                        IsHitTestVisible = false,
                    },
                    CreateIcon(key, shades.Icon, iconSize),
                },
            },
        };
    }

    public static FrameworkElement CreateIcon(string key, Brush accentBrush, double size = 18) =>
        CreateIcon(key, GetShades(accentBrush).Icon, size);

    private static FontIcon CreateIcon(string key, SolidColorBrush iconBrush, double size) =>
        new()
        {
            Glyph = ResolveGlyph(Normalize(key)),
            FontFamily = FluentIconFont,
            FontSize = size,
            Foreground = iconBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

    private static Windows.UI.Color DarkenColor(Windows.UI.Color color, double factor)
    {
        return Windows.UI.Color.FromArgb(
            color.A,
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor));
    }

    private static string ResolveGlyph(string key)
    {
        return key switch
        {
            "collection" => "", // Stopwatch: how often samples are taken
            "appearance" => "", // Color
            "retention" => "\uE81C", // History
            "storage" => "\uE8B7", // Folder
            "window" => "\uE7E8", // PowerButton: startup and close behaviour
            "cpu" => "\uEEA1", // CPU
            "memory" => "\uEEA0", // RAM
            "disk" => "\uEDA2", // HardDrive
            "drive" => "\uE8CE", // MapDrive
            "network" => "\uEDA3", // NetworkAdapter
            "gpu" => "\uE7F8", // Video
            "temperature" => "\uE9CA", // Thermometer
            "system" => "\uF182", // ScreenTime
            "overlay" => "", // Game
            _ => "\uF182",
        };
    }

    /// <summary>
    /// The colours icons and tiles draw with, worked out from their accent brush. Shared by every
    /// icon with that accent and recoloured in place after a theme change, so a theme switch never
    /// has to find icons and rebuild them. An entry lives as long as its accent brush.
    /// </summary>
    private static readonly ConditionalWeakTable<SolidColorBrush, AccentShades> s_shades = new();

    private static readonly SolidColorBrush s_fallbackAccent = BrushFactory.CreateBrush("#66E7FF");

    private static AccentShades GetShades(Brush accentBrush)
    {
        var accent = accentBrush as SolidColorBrush ?? s_fallbackAccent;
        if (!s_shades.TryGetValue(accent, out var shades))
        {
            shades = new AccentShades();
            shades.Recolor(accent.Color, IsLightPaletteActive(), ResolveColor("SurfaceInsetBrush", "#141619"));
            s_shades.Add(accent, shades);
        }

        return shades;
    }

    /// <summary>After a theme change, once the accent brushes hold their new colours.</summary>
    public static void RecolorAccentShades()
    {
        var isLight = IsLightPaletteActive();
        var inset = ResolveColor("SurfaceInsetBrush", "#141619");
        foreach (var (accent, shades) in s_shades)
        {
            shades.Recolor(accent.Color, isLight, inset);
        }
    }

    private sealed class AccentShades
    {
        private static readonly Windows.UI.Color GlowEdge = BrushFactory.ParseColor("#001018");
        private readonly GradientStop _glowCore = new() { Offset = 0d };
        private readonly GradientStop _glowMid = new() { Offset = 0.32d };
        private readonly GradientStop _glowEdge = new() { Offset = 1d };

        public AccentShades()
        {
            Glow = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.36),
                GradientOrigin = new Point(0.5, 0.36),
                RadiusX = 0.92,
                RadiusY = 0.92,
            };
            Glow.GradientStops.Add(_glowCore);
            Glow.GradientStops.Add(_glowMid);
            Glow.GradientStops.Add(_glowEdge);
        }

        public SolidColorBrush Icon { get; } = new();

        public SolidColorBrush TileFill { get; } = new();

        public SolidColorBrush TileStroke { get; } = new();

        public RadialGradientBrush Glow { get; }

        public void Recolor(Windows.UI.Color accent, bool isLight, Windows.UI.Color inset)
        {
            if (isLight)
            {
                // Bold, darker shades of the accent for strong contrast on light surfaces.
                var bold = DarkenColor(accent, 0.55);
                Icon.Color = DarkenColor(accent, 0.6);
                TileFill.Color = Windows.UI.Color.FromArgb(40, bold.R, bold.G, bold.B);
                TileStroke.Color = Windows.UI.Color.FromArgb(80, bold.R, bold.G, bold.B);
                TileStroke.Opacity = 1;
                _glowCore.Color = _glowMid.Color = _glowEdge.Color = Windows.UI.Color.FromArgb(0, accent.R, accent.G, accent.B);
                return;
            }

            Icon.Color = accent;
            TileFill.Color = inset;
            TileStroke.Color = accent;
            TileStroke.Opacity = 0.4;
            _glowCore.Color = _glowMid.Color = accent;
            _glowEdge.Color = GlowEdge;
        }
    }

    private static bool IsLightPaletteActive() => ThemeVisuals.IsLightPaletteActive();

    private static Windows.UI.Color ResolveColor(string key, string fallbackHex) => ThemeVisuals.ResolveColor(key, fallbackHex);

    private static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var normalized = key.Trim().ToLowerInvariant();
        if (normalized.Contains("storage"))
        {
            return "storage";
        }

        return normalized switch
        {
            "collection" or "clock" or "scrape" => "collection",
            "retention" or "history" or "bolt" => "retention",
            "storage" or "folder" => "storage",
            "cpu" => "cpu",
            "mem" or "memory" or "ram" => "memory",
            "disk" or "dsk" => "disk",
            "drive" or "drv" or "volume" => "drive",
            "network" or "net" or "wan" => "network",
            "temperature" or "temp" => "temperature",
            "gpu" or "graphics" => "gpu",
            "system" or "activity" or "host" => "system",
            _ => key.Trim().ToLowerInvariant(),
        };
    }
}
