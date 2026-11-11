using Microsoft.UI.Xaml.Media;
using SwissOlly.App.ViewModels;
using Windows.UI;

namespace SwissOlly.App.Controls;

// Shared theme and brush-resolution helpers used by the shell and telemetry controls.
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

    // ── Brushes worked out from the palette ──
    //
    // A few surfaces draw a colour the palette has no brush for: a gauge face's gradient between
    // two surfaces, the faint fade at a plot's edges. Each is created once, shared by everything
    // that draws it, and recoloured in place by RecolorDerivedBrushes, the way App.SetBrushResource
    // recolours the palette brushes. A theme change then never has to find those elements; it used
    // to walk every card, chart and gauge to hand each a new brush.

    private static LinearGradientBrush? s_gaugeFaceBrush;
    private static LinearGradientBrush? s_plotLeadingFadeBrush;
    private static LinearGradientBrush? s_plotTrailingFadeBrush;

    /// <summary>A gauge's face: the raised surface fading into the card's.</summary>
    public static Brush GaugeFaceBrush => s_gaugeFaceBrush ??= RecolorGaugeFace(CreateGradient(new(1, 1)));

    /// <summary>The faint plot-coloured fade over a plot's left (leading) or right edge.</summary>
    public static Brush PlotEdgeFadeBrush(bool isLeadingEdge) => isLeadingEdge
        ? s_plotLeadingFadeBrush ??= RecolorPlotEdgeFade(CreateGradient(new(1, 0)), isLeadingEdge: true)
        : s_plotTrailingFadeBrush ??= RecolorPlotEdgeFade(CreateGradient(new(1, 0)), isLeadingEdge: false);

    /// <summary>
    /// Recolours every shared brush worked out from the palette or a series colour. Runs after the
    /// palette brushes and the series colours have changed; a brush not created yet takes the new
    /// palette when it is.
    /// </summary>
    public static void RecolorDerivedBrushes()
    {
        if (s_gaugeFaceBrush is not null)
        {
            RecolorGaugeFace(s_gaugeFaceBrush);
        }

        if (s_plotLeadingFadeBrush is not null)
        {
            RecolorPlotEdgeFade(s_plotLeadingFadeBrush, isLeadingEdge: true);
        }

        if (s_plotTrailingFadeBrush is not null)
        {
            RecolorPlotEdgeFade(s_plotTrailingFadeBrush, isLeadingEdge: false);
        }

        IconFactory.RecolorAccentShades();
    }

    private static LinearGradientBrush CreateGradient(Windows.Foundation.Point end) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = end,
        GradientStops = new GradientStopCollection
        {
            new GradientStop { Offset = 0d },
            new GradientStop { Offset = 1d },
        },
    };

    private static LinearGradientBrush RecolorGaugeFace(LinearGradientBrush brush)
    {
        brush.GradientStops[0].Color = ResolveColor("SurfaceElevatedBrush", "#222529");
        brush.GradientStops[1].Color = ResolveColor("SurfaceBrush", "#1C1F22");
        return brush;
    }

    private static LinearGradientBrush RecolorPlotEdgeFade(LinearGradientBrush brush, bool isLeadingEdge)
    {
        var plot = ResolveColor("SurfaceInsetBrush", "#141619");
        brush.GradientStops[0].Color = Color.FromArgb(isLeadingEdge ? (byte)40 : (byte)0, plot.R, plot.G, plot.B);
        brush.GradientStops[1].Color = Color.FromArgb(isLeadingEdge ? (byte)0 : (byte)40, plot.R, plot.G, plot.B);
        return brush;
    }
}
