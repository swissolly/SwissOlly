using Microsoft.UI.Xaml.Media;
using Vaktr.App.ViewModels;
using Windows.UI;

namespace Vaktr.App.Controls;

// Shared theme/brush-resolution helpers previously duplicated near-verbatim across
// ShellWindow.Polished.UI.cs, TelemetryChart.cs, TelemetryPanelCard.cs, ActionChip.cs,
// InlineTextEntry.cs, and IconFactory.cs. Two distinct gradient patterns exist in the app
// (diffed entry-by-entry before merging — no behavior change from consolidation):
//   - CreateSurfaceLiftGradient: cached, per-hex-pair LiftToLight table (cards/chart/panels)
//   - CreateThemeResourceGradient: uncached, always resolves from SurfaceElevatedBrush/
//     SurfaceStrongBrush theme resources regardless of the hex pair passed in (chips/entries/icons)
internal static class ThemeVisuals
{
    public static Brush ResolveBrush(string key, string fallbackHex)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return BrushFactory.CreateBrush(fallbackHex);
    }

    public static Color ResolveColor(string key, string fallbackHex)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
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

    // ── Pattern A: cached, per-surface LiftToLight gradient (ShellWindow / TelemetryChart / TelemetryPanelCard) ──

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

        LinearGradientBrush brush;
        if (isLight)
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop { Color = LiftToLight(startHex), Offset = 0d },
                    new GradientStop { Color = LiftToLight(endHex), Offset = 1d },
                },
            };
        }
        else
        {
            brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop { Color = BrushFactory.ParseColor(startHex), Offset = 0d },
                    new GradientStop { Color = BrushFactory.ParseColor(endHex), Offset = 1d },
                },
            };
        }

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

    // ── Pattern B: uncached, theme-resource-based gradient (ActionChip / InlineTextEntry / IconFactory) ──

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
                new GradientStop { Color = BrushFactory.ParseColor(startHex), Offset = 0d },
                new GradientStop { Color = BrushFactory.ParseColor(endHex), Offset = 1d },
            },
        };
    }
}
