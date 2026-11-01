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
                case "CTRL":  mods |= 0x0002; break;
                case "ALT":   mods |= 0x0001; break;
                case "SHIFT": mods |= 0x0004; break;
                case "WIN":   mods |= 0x0008; break;
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
        // Single letter A-Z
        if (key.Length == 1 && char.IsLetter(key[0]))
            return (uint)char.ToUpperInvariant(key[0]);

        // Function keys F1-F24 (VK_F1=0x70)
        if (key.Length >= 2 && key[0] is 'F' or 'f' && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1);

        // Single digit 0-9
        if (key.Length == 1 && char.IsDigit(key[0]))
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
