using System.Text.RegularExpressions;

namespace SwissOlly.Core.Models;

public sealed class OverlayConfig
{
    private static readonly string[] DefaultCustomMetrics = ["fps", "cpu", "gpu", "gpu-temp", "memory"];
    private static readonly HashSet<string> ValidMetrics = new(DefaultCustomMetrics, StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = false;
    public OverlayMode Mode { get; set; } = OverlayMode.Advanced;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopRight;
    public string TextColor { get; set; } = "#66E7FF";
    public int FontSizePx { get; set; } = 14;
    public double Opacity { get; set; } = 0.9;
    public string HotkeyGesture { get; set; } = "Ctrl+Shift+F11";
    public List<string> CustomMetrics { get; set; } = ["fps", "cpu", "gpu", "gpu-temp", "memory"];

    public OverlayConfig Normalize()
    {
        if (FontSizePx is < 10 or > 32)
            FontSizePx = 14;

        if (!IsValidHexColor(TextColor))
            TextColor = "#66E7FF";

        if (!TryParseHotkeyGesture(HotkeyGesture, out _, out _))
            HotkeyGesture = "Ctrl+Shift+F11";

        if (!Enum.IsDefined(typeof(OverlayMode), Mode))
            Mode = OverlayMode.Advanced;

        if (!Enum.IsDefined(typeof(OverlayCorner), Corner))
            Corner = OverlayCorner.TopRight;

        Opacity = Math.Clamp(Opacity, 0.3, 1.0);

        CustomMetrics ??= [];
        CustomMetrics = CustomMetrics
            .Where(m => ValidMetrics.Contains(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (CustomMetrics.Count == 0)
            CustomMetrics = ["fps"];

        return this;
    }

    public static bool IsValidHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Regex.IsMatch(value.Trim(), @"^#[0-9A-Fa-f]{6}$");
    }

    /// <summary>"#RRGGBB" in upper case, accepting the value with or without its "#"; null when it
    /// isn't six hex digits. Colours are usually pasted without the "#".</summary>
    public static string? NormalizeHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var candidate = trimmed.StartsWith('#') ? trimmed : "#" + trimmed;
        return IsValidHexColor(candidate) ? candidate.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Why a hotkey can't be used, or null when it can. A global hotkey is taken from every app
    /// for as long as SwissOlly runs, so it needs Ctrl, Alt or Win (Shift alone would swallow
    /// capital letters) and must not be a shortcut people use all day.
    /// </summary>
    public static string? DescribeHotkeyProblem(string? gesture)
    {
        if (!TryParseHotkeyGesture(gesture, out var modifiers, out var vk))
        {
            return "The overlay hotkey needs Ctrl, Alt or Win plus a key, like Ctrl+Shift+F11.";
        }

        const uint Alt = 0x0001, Ctrl = 0x0002, Win = 0x0008;
        if ((modifiers & (Alt | Ctrl | Win)) == 0)
        {
            return "The overlay hotkey needs Ctrl, Alt or Win, not just Shift: Shift+a key would take that capital letter from every app.";
        }

        var isLetter = vk is >= 'A' and <= 'Z';
        var commonCtrl = modifiers == Ctrl && isLetter && "ACFNOPSTVWXYZ".Contains((char)vk);
        var commonAlt = modifiers == Alt && vk is 0x73 /* F4 */ or 0x09 /* Tab */ or 0x20 /* Space */;
        var windowsKeys = modifiers == Win;
        if (commonCtrl || commonAlt || windowsKeys)
        {
            return "That hotkey is already a common shortcut. Try a combination like Ctrl+Shift+F11 or Ctrl+Alt+O.";
        }

        return null;
    }

    // Returns true when the gesture is parseable; out params are the modifier mask and VK code.
    // Format: "Mod1+Mod2+Key" where mods are Ctrl/Alt/Shift/Win, key is letter, F1-F24, or punctuation.
    public static bool TryParseHotkeyGesture(string? gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        uint mods = 0;
        string? keyPart = null;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": mods |= 0x0002; break;
                case "ALT": mods |= 0x0001; break;
                case "SHIFT": mods |= 0x0004; break;
                case "WIN": mods |= 0x0008; break;
                default:
                    if (keyPart != null) return false; // two non-modifier parts
                    keyPart = part;
                    break;
            }
        }

        if (keyPart == null || mods == 0) return false;

        uint resolvedVk = ResolveVirtualKey(keyPart);
        if (resolvedVk == 0) return false;

        modifiers = mods;
        vk = resolvedVk;
        return true;
    }

    private static uint ResolveVirtualKey(string key)
    {
        // Single letter A-Z. ASCII only: an "é" mapped to a virtual key that never fires.
        if (key.Length == 1 && char.IsAsciiLetter(key[0]))
            return (uint)char.ToUpperInvariant(key[0]);

        // Function keys F1-F24 (VK_F1=0x70)
        if (key.Length >= 2 && key[0] is 'F' or 'f' && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1);

        // Single digit 0-9
        if (key.Length == 1 && char.IsAsciiDigit(key[0]))
            return (uint)key[0];

        // Common punctuation VK codes
        return key.ToUpperInvariant() switch
        {
            "OEM_PLUS" or "PLUS" => 0xBB,
            "OEM_MINUS" or "MINUS" => 0xBD,
            "OEM_COMMA" or "COMMA" => 0xBC,
            "OEM_PERIOD" or "PERIOD" => 0xBE,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "BACK" or "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            _ => 0,
        };
    }
}
