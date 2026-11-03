using Microsoft.UI.Xaml.Media;
using SwissOlly.App.ViewModels;
using Windows.UI;

namespace SwissOlly.App.Controls;

// Shared theme and brush-resolution helpers used by the shell and telemetry controls.
// Surface gradients stay cached; theme resources define dark surfaces while light surfaces
// retain their per-surface lift values.
internal static class ThemeVisuals
{
    // Theme switches recolour the palette brushes in place (App.SetBrushResource) rather than
    // replacing them, so an instance found once stays correct for the life of the app. Caching it
    // saves a WinRT ResourceDictionary probe per call - about 150 per scrape before this. Only
    // hits are cached, so a resource added later is still picked up. UI thread only.
    private static readonly Dictionary<string, Brush> s_resourceBrushes = new(StringComparer.Ordinal);

    public static Brush ResolveBrush(string key, string fallbackHex)
    {
        if (s_resourceBrushes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            s_resourceBrushes[key] = brush;
            return brush;
        }

        return BrushFactory.CreateBrush(fallbackHex);
    }

    public static Color ResolveColor(string key, string fallbackHex)
    {
        if (s_resourceBrushes.TryGetValue(key, out var cached) && cached is SolidColorBrush cachedBrush)
        {
            return cachedBrush.Color;
        }

        if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            s_resourceBrushes[key] = brush;
            return brush.Color;
        }

        return BrushFactory.ParseColor(fallbackHex);
    }

    public static bool IsLightPaletteActive()
    {
        var color = ResolveColor("AppBackdropBrush", "#030812");
        var luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luminance >= 170d;
    }

    /// <summary>
    /// A chart colour as the current palette should draw it. The series colours are tuned for
    /// dark plots; on a light one they measured 1.1-2.5:1 against the plot (the CPU line all but
    /// vanished), so there they are darkened, hue and saturation kept, until they reach 3:1, the
    /// WCAG floor for graphics. Dark palettes get the colour unchanged.
    /// </summary>
    public static Color AdaptSeriesColor(Color color) =>
        IsLightPaletteActive() ? AdaptSeriesColorForLightPlot(color, ResolveColor("SurfaceInsetBrush", "#F1F2F4")) : color;

    /// <summary>The same adaptation against a given light plot colour, for previews of a palette
    /// that isn't the current one.</summary>
    public static Color AdaptSeriesColorForLightPlot(Color color, Color plot)
    {
        var key = ((long)PackRgb(color) << 24) | (uint)PackRgb(plot);
        if (s_adaptedSeriesColors.TryGetValue(key, out var adapted))
        {
            return adapted;
        }

        adapted = DarkenToContrast(color, plot, 3.2);
        s_adaptedSeriesColors[key] = adapted;
        return adapted;
    }

    private static readonly Dictionary<long, Color> s_adaptedSeriesColors = [];

    private static int PackRgb(Color color) => (color.R << 16) | (color.G << 8) | color.B;

    private static Color DarkenToContrast(Color color, Color background, double target)
    {
        var (hue, saturation, lightness) = ToHsl(color);
        var candidate = color;
        while (ContrastRatio(candidate, background) < target && lightness > 0.12)
        {
            lightness -= 0.02;
            candidate = FromHsl(hue, saturation, lightness, color.A);
        }

        return candidate;
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Hue, saturation and lightness, each 0 to 1.</summary>
    internal static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        var hue = max == r ? ((g - b) / delta) + (g < b ? 6 : 0)
            : max == g ? ((b - r) / delta) + 2
            : ((r - g) / delta) + 4;
        return (hue / 6, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness, byte alpha)
    {
        if (saturation == 0)
        {
            var gray = (byte)Math.Round(lightness * 255);
            return Color.FromArgb(alpha, gray, gray, gray);
        }

        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1d / 6) return p + ((q - p) * 6 * t);
            if (t < 1d / 2) return q;
            if (t < 2d / 3) return p + ((q - p) * ((2d / 3) - t) * 6);
            return p;
        }

        var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - (lightness * saturation);
        var p = (2 * lightness) - q;
        return Color.FromArgb(
            alpha,
            (byte)Math.Round(HueToRgb(p, q, hue + (1d / 3)) * 255),
            (byte)Math.Round(HueToRgb(p, q, hue) * 255),
            (byte)Math.Round(HueToRgb(p, q, hue - (1d / 3)) * 255));
    }

    // Supports both #RRGGBB and #AARRGGBB — BrushFactory.ParseColor only supports the former.
    public static Color ParseColor(string hex)
    {
        var normalized = hex.Trim().TrimStart('#');
        return normalized.Length switch
        {
            6 => Color.FromArgb(255,
                Convert.ToByte(normalized[..2], 16),
                Convert.ToByte(normalized.Substring(2, 2), 16),
                Convert.ToByte(normalized.Substring(4, 2), 16)),
            8 => Color.FromArgb(
                Convert.ToByte(normalized[..2], 16),
                Convert.ToByte(normalized.Substring(2, 2), 16),
                Convert.ToByte(normalized.Substring(4, 2), 16),
                Convert.ToByte(normalized.Substring(6, 2), 16)),
            _ => Color.FromArgb(0, 0, 0, 0),
        };
    }

    // ── Cached surface gradient (ShellWindow / TelemetryChart / TelemetryPanelCard) ──

    private static readonly Dictionary<string, LinearGradientBrush> s_liftGradientCache = new(StringComparer.Ordinal);
    private static bool s_lastCachedLightMode;

    // CreateSurfaceLiftGradient already self-invalidates on theme change, but callers that want
    // to force a clean slate on an explicit theme switch (e.g. before a synchronous chrome refresh
    // pass) can call this directly.
    public static void InvalidateGradientCache() => s_liftGradientCache.Clear();

    public static Brush CreateSurfaceLiftGradient(string startHex, string endHex)
    {
        var isLight = IsLightPaletteActive();

        // Invalidate cache on theme change
        if (isLight != s_lastCachedLightMode)
        {
            s_liftGradientCache.Clear();
            s_lastCachedLightMode = isLight;
        }

        var cacheKey = string.Concat(startHex, "|", endHex);
        if (s_liftGradientCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var startColor = isLight
            ? LiftToLight(startHex)
            : ResolveColor("SurfaceElevatedBrush", "#222529");
        var endColor = isLight
            ? LiftToLight(endHex)
            : ResolveColor("SurfaceBrush", "#1C1F22");
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop { Color = startColor, Offset = 0d },
                new GradientStop { Color = endColor, Offset = 1d },
            },
        };

        s_liftGradientCache[cacheKey] = brush;
        return brush;
    }

    // Union of the three previously-separate LiftToLight tables (ShellWindow's was the superset;
    // TelemetryChart's and TelemetryPanelCard's subsets were verified to agree on every shared key
    // before merging — no behavior change).
    public static Color LiftToLight(string darkHex)
    {
        return darkHex.TrimStart('#').ToUpperInvariant() switch
        {
            // Shell surfaces
            "08111C" => BrushFactory.ParseColor("#F0F4F9"),
            "0D1A29" => BrushFactory.ParseColor("#E7EEF6"),

            // Card surfaces — pure white, stands out from gray-blue backdrop
            "0E1B2C" or "0E1A2C" or "101B2D" or "101C2E" => BrushFactory.ParseColor("#FFFFFF"),
            "13253A" or "142436" or "15273D" or "16273D" => BrushFactory.ParseColor("#F8FAFD"),

            // Summary/control card surfaces
            "0F1C2D" => BrushFactory.ParseColor("#FAFCFF"),
            "15283F" or "14263A" => BrushFactory.ParseColor("#F0F4FA"),

            // Badge tile — slightly tinted so it's visible on white cards
            "102131" or "17304A" => BrushFactory.ParseColor("#E0EAF4"),

            // Chart frame — light gray inset, clearly distinct from white card
            "0E1A2B" or "13263B" or "0C1824" or "111F30" => BrushFactory.ParseColor("#EDF1F7"),

            // Legend row idle — subtle gray
            "101C2D" or "132438" => BrushFactory.ParseColor("#F0F4F9"),
            // Legend row hover — noticeably darker
            "162A3E" or "1C3350" => BrushFactory.ParseColor("#E0E8F2"),

            // Range shell
            "102031" or "15283E" => BrushFactory.ParseColor("#ECF0F6"),

            // Chart plot background — light gray, not white (creates inset feel)
            "0B1726" or "12243A" or "0A141F" or "0F1E2E" => BrushFactory.ParseColor("#EEF2F8"),

            // Card hover — visible shift from white
            "102133" or "162A40" or "122034" or "1A2C46" => BrushFactory.ParseColor("#E4ECF4"),

            // Control editor cards
            "0F1B2D" or "13243A" => BrushFactory.ParseColor("#F4F7FC"),

            // Process section — gray inset
            "0D1824" or "12202F" => BrushFactory.ParseColor("#EBF0F6"),

            // Scale pill / misc
            "101D2E" or "152840" => BrushFactory.ParseColor("#E6ECF4"),

            // Tooltip
            "15283B" or "203851" => BrushFactory.ParseColor("#FFFFFF"),

            _ => BrushFactory.ParseColor("#F2F6FB"),
        };
    }

    // ── Theme-resource-based gradient (ActionChip / InlineTextEntry / IconFactory) ──

    public static Brush CreateThemeResourceGradient(string startHex, string endHex)
    {
        if (IsLightPaletteActive())
        {
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop { Color = ResolveColor("SurfaceElevatedBrush", "#F4F8FC"), Offset = 0d },
                    new GradientStop { Color = ResolveColor("SurfaceStrongBrush", "#EDF4FB"), Offset = 1d },
                },
            };
        }

        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop { Color = ResolveColor("SurfaceElevatedBrush", "#222529"), Offset = 0d },
                new GradientStop { Color = ResolveColor("SurfaceStrongBrush", "#292D31"), Offset = 1d },
            },
        };
    }
}
