using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace SwissOlly.Overlay.Rendering;

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

    // Gap between the label column and the value column.
    private const int ColumnGap = 8;

    // Labels sit slightly back from the values so the numbers read first.
    private const float LabelAlpha = 0.72f;

    private Font? _font;
    private int _fontSizePx;
    private Bitmap? _bitmap;
    private OverlayLine[] _lastLines = [];
    private Color _lastColor;
    private float _lastOpacity;
    private bool _dirty = true;

    // Scratch surface for text measurement. Hoisted out of MeasureSize so a render pass doesn't
    // allocate a Bitmap + Graphics pair every time a value ticks.
    private Bitmap? _measureSurface;
    private Graphics? _measureGraphics;

    // Corner positioning is derived from the bitmap size, so a bitmap that shrinks when a value
    // loses a digit drags the whole HUD sideways. Track a high-water mark and never shrink within
    // a layout generation; ResetLayout() clears it when the font size or line count actually change.
    private int _widthHighWater;
    private int _heightHighWater;

    internal (Bitmap bmp, bool changed) Render(
        OverlayLine[] lines, Color textColor, int fontSizePx, float opacity)
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
            ResetLayout();
            changed = true;
        }

        if (lines.Length != _lastLines.Length)
            ResetLayout();

        var (w, h) = MeasureSize(lines, _font);

        w = Math.Max(w, _widthHighWater);
        h = Math.Max(h, _heightHighWater);
        _widthHighWater = w;
        _heightHighWater = h;
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
        byte labelAlpha = (byte)(255 * opacity * LabelAlpha);

        using var labelBrush = new SolidBrush(Color.FromArgb(labelAlpha, textColor.R, textColor.G, textColor.B));

        byte outlineAlpha = (byte)(220 * opacity);
        using var outlineBrush = new SolidBrush(Color.FromArgb(outlineAlpha, 6, 10, 16));

        float valueX = PaddingX + MeasureLabelColumn(lines, _font) + ColumnGap;
        float y = PaddingY;
        float lineH = _font.GetHeight(g);

        foreach (var line in lines)
        {
            // The outline is drawn for label and value together so glyphs that nearly touch across
            // the column gap still read as one outlined run rather than two overlapping halos.
            foreach (var (dx, dy) in OutlineOffsets)
            {
                g.DrawString(line.Label, _font, outlineBrush, PaddingX + dx, y + dy);
                g.DrawString(line.Value, _font, outlineBrush, valueX + dx, y + dy);
            }

            g.DrawString(line.Label, _font, labelBrush, PaddingX, y);

            var c = line.Color;
            using (var valueBrush = new SolidBrush(Color.FromArgb(textAlpha, c.R, c.G, c.B)))
                g.DrawString(line.Value, _font, valueBrush, valueX, y);

            y += lineH + LineSpacing;
        }

        // Premultiply alpha for UpdateLayeredWindow
        PremultiplyAlpha(_bitmap);

        _lastLines = (OverlayLine[])lines.Clone();
        _lastColor = textColor;
        _lastOpacity = opacity;
        _dirty = false;

        return (_bitmap, true);
    }

    internal void Invalidate() => _dirty = true;

    private void ResetLayout()
    {
        _widthHighWater = 0;
        _heightHighWater = 0;
    }

    /// <summary>Width of the widest label, which fixes the column every value starts at.</summary>
    private float MeasureLabelColumn(OverlayLine[] lines, Font font)
    {
        if (_measureGraphics == null)
        {
            _measureSurface = new Bitmap(1, 1);
            _measureGraphics = Graphics.FromImage(_measureSurface);
            _measureGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        float max = 0;
        foreach (var line in lines)
        {
            var sz = _measureGraphics.MeasureString(line.Label, font);
            if (sz.Width > max) max = sz.Width;
        }
        return max;
    }

    private (int w, int h) MeasureSize(OverlayLine[] lines, Font font)
    {
        if (lines.Length == 0) return (1, 1);

        if (_measureGraphics == null)
        {
            _measureSurface = new Bitmap(1, 1);
            _measureGraphics = Graphics.FromImage(_measureSurface);
            _measureGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }
        var g = _measureGraphics;

        float lineH = font.GetHeight(g);
        float labelColumn = MeasureLabelColumn(lines, font);
        float maxValueW = 0;
        foreach (var line in lines)
        {
            var sz = g.MeasureString(line.Value, font);
            if (sz.Width > maxValueW) maxValueW = sz.Width;
        }

        int w = (int)Math.Ceiling(labelColumn + ColumnGap + maxValueW) + PaddingX * 2;
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
        _measureGraphics?.Dispose();
        _measureSurface?.Dispose();
    }
}
