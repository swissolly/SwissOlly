using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Vaktr.Overlay.Rendering;

internal sealed class OverlayBitmapRenderer : IDisposable
{
    private const int PaddingX = 6;
    private const int PaddingY = 6;
    private const int LineSpacing = 4;

    // 8-direction 1px outline offsets — cheap way to reuse GDI+'s own glyph rendering
    // for an outlined look instead of a background box, guaranteed to match DrawString's
    // glyph shapes exactly (unlike a GraphicsPath-based stroke).
    private static readonly (float dx, float dy)[] OutlineOffsets =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),           (1, 0),
        (-1, 1),  (0, 1),  (1, 1),
    ];

    private Font? _font;
    private int _fontSizePx;
    private Bitmap? _bitmap;
    private string[] _lastLines = [];
    private Color _lastColor;
    private float _lastOpacity;
    private bool _dirty = true;

    internal (Bitmap bmp, bool changed) Render(
        string[] lines, Color textColor, int fontSizePx, float opacity)
    {
        bool changed = _dirty
            || lines.Length != _lastLines.Length
            || !lines.SequenceEqual(_lastLines)
            || textColor != _lastColor
            || fontSizePx != _fontSizePx
            || Math.Abs(opacity - _lastOpacity) > 0.001f;

        if (!changed && _bitmap != null)
            return (_bitmap, false);

        if (_font == null || fontSizePx != _fontSizePx)
        {
            _font?.Dispose();
            _font = new Font("Segoe UI", fontSizePx, FontStyle.Bold, GraphicsUnit.Pixel);
            _fontSizePx = fontSizePx;
            changed = true;
        }

        var (w, h) = MeasureSize(lines, _font);
        if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }

        using var g = Graphics.FromImage(_bitmap);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        byte textAlpha = (byte)(255 * opacity);
        using var textBrush = new SolidBrush(Color.FromArgb(textAlpha, textColor.R, textColor.G, textColor.B));

        byte outlineAlpha = (byte)(220 * opacity);
        using var outlineBrush = new SolidBrush(Color.FromArgb(outlineAlpha, 6, 10, 16));

        float y = PaddingY;
        float lineH = _font.GetHeight(g);
        foreach (var line in lines)
        {
            foreach (var (dx, dy) in OutlineOffsets)
                g.DrawString(line, _font, outlineBrush, PaddingX + dx, y + dy);

            g.DrawString(line, _font, textBrush, PaddingX, y);
            y += lineH + LineSpacing;
        }

        // Premultiply alpha for UpdateLayeredWindow
        PremultiplyAlpha(_bitmap);

        _lastLines = (string[])lines.Clone();
        _lastColor = textColor;
        _lastOpacity = opacity;
        _dirty = false;

        return (_bitmap, true);
    }

    internal void Invalidate() => _dirty = true;

    private (int w, int h) MeasureSize(string[] lines, Font font)
    {
        if (lines.Length == 0) return (1, 1);

        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float lineH = font.GetHeight(g);
        float maxW = 0;
        foreach (var line in lines)
        {
            var sz = g.MeasureString(line, font);
            if (sz.Width > maxW) maxW = sz.Width;
        }

        int w = (int)Math.Ceiling(maxW) + PaddingX * 2;
        int h = (int)Math.Ceiling(lineH * lines.Length + LineSpacing * (lines.Length - 1)) + PaddingY * 2;
        return (Math.Max(w, 1), Math.Max(h, 1));
    }

    private static unsafe void PremultiplyAlpha(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            byte* ptr = (byte*)data.Scan0;
            int count = bmp.Width * bmp.Height;
            for (int i = 0; i < count; i++, ptr += 4)
            {
                byte a = ptr[3];
                if (a == 255) continue;
                if (a == 0) { ptr[0] = ptr[1] = ptr[2] = 0; continue; }
                ptr[0] = (byte)(ptr[0] * a / 255);
                ptr[1] = (byte)(ptr[1] * a / 255);
                ptr[2] = (byte)(ptr[2] * a / 255);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _font?.Dispose();
        _bitmap?.Dispose();
    }
}
