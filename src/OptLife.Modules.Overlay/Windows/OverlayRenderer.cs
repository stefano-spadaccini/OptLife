using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace OptLife.Modules.Overlay.Windows;

/// <summary>
/// Draws the strip as a picture with see-through pixels: black behind (as dark as chosen), labels and numbers in the chosen
/// colours and font, an alert colour for values beyond their limit, a shadow or an outline that keeps text readable on a
/// bright scene. One value per line, all on one line, or a grid. The same picture is the editor's preview in the panel.
/// </summary>
internal static class OverlayRenderer
{
    private static readonly FontFamily LabelFamily = Family("Cascadia Code", "Cascadia Mono", "Consolas");

    /// <param name="scale">The person's size setting times the screen's own scaling (1 = 100%).</param>
    public static Bitmap Render(OverlayFrame frame, double scale)
    {
        float S(double v) => (float)(v * scale);
        var style = frame.Style;
        using var valueFamily = new FontFamily(Installed(style.Font) ?? "Segoe UI");
        using var labelFont = new Font(LabelFamily, S(10.5), FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font(valueFamily, S(15), FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

        float padX = S(11), padY = S(7), rowHeight = S(21), gap = S(14), between = S(18), inner = S(7);

        // Measuring needs a surface; a 1x1 one is enough.
        SizeF[] labels, values;
        using (var probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(probe))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            labels = [.. frame.Rows.Select(r => g.MeasureString(r.Label, labelFont, PointF.Empty, format))];
            values = [.. frame.Rows.Select(r => g.MeasureString(r.Value, valueFont, PointF.Empty, format))];
        }

        // A cell is a label and its number; the column grid lines them up.
        var count = frame.Rows.Count;
        var columns = style.Arrangement switch
        {
            OverlayArrangement.Row => count,
            OverlayArrangement.Grid => Math.Clamp(style.GridColumns, 1, Math.Max(1, count)),
            _ => 1,
        };
        var lines = (int)Math.Ceiling(count / (double)columns);
        float labelWidth = labels.Max(l => l.Width), valueWidth = values.Max(v => v.Width);
        var cellWidths = Enumerable.Range(0, columns).Select(c =>
        {
            if (style.Arrangement == OverlayArrangement.Row) return labels[c].Width + (labels[c].Width > 0 ? inner : 0) + values[c].Width;
            var inColumn = Enumerable.Range(0, count).Where(i => i % columns == c).ToList();
            return inColumn.Max(i => labels[i].Width) + gap + inColumn.Max(i => values[i].Width);
        }).ToArray();
        var width = padX * 2 + cellWidths.Sum() + between * (columns - 1);
        var height = padY * 2 + rowHeight * lines;

        var bitmap = new Bitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), PixelFormat.Format32bppPArgb);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.Clear(Color.Transparent);
        canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        canvas.SmoothingMode = SmoothingMode.None;

        var alpha = style.BackgroundPercent * 255 / 100;
        if (alpha > 0)
        {
            using var back = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            canvas.FillRectangle(back, 0, 0, bitmap.Width, bitmap.Height);
            using var edge = new Pen(Color.FromArgb(Math.Min(255, alpha + 50), 0x3D, 0x3D, 0x3D));
            canvas.DrawRectangle(edge, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
        }

        var valueColor = Parse(style.ValueColor, Color.White);
        var labelColor = Parse(style.LabelColor, Color.FromArgb(0xAA, 0xAA, 0xAA));
        var alertColor = Parse(style.AlertColor, Color.FromArgb(0xFF, 0xB3, 0x1F));
        // With little black behind, grey labels fade on a bright scene: a little lighter then.
        if (style.BackgroundPercent < 50 && labelColor.GetBrightness() < 0.75f) labelColor = Lighter(labelColor);
        // The edge: its own colour and thickness; 0 = none.
        var edgeWidth = Math.Clamp(style.OutlineWidth, 0, 4);
        var thick = Math.Max(1f, S(1));
        var edgeColor = Parse(style.OutlineColor, Color.Black);
        void Text(string text, Font font, Color color, float x, float baseline)
        {
            var y = baseline - Ascent(font);
            using var dark = new SolidBrush(Color.FromArgb(style.BackgroundPercent < 50 ? 230 : 170, edgeColor));
            switch (edgeWidth == 0 ? OverlayOutline.None : style.Outline)
            {
                case OverlayOutline.Border:
                    for (var r = 1; r <= edgeWidth; r++)
                    {
                        var d = thick * r;
                        foreach (var (dx, dy) in new[] { (-d, 0f), (d, 0f), (0f, -d), (0f, d), (-d, -d), (d, d), (-d, d), (d, -d) })
                            canvas.DrawString(text, font, dark, x + dx, y + dy, format);
                    }
                    break;
                case OverlayOutline.Shadow:
                    for (var r = 1; r <= edgeWidth; r++) canvas.DrawString(text, font, dark, x + thick * r, y + thick * r, format);
                    break;
            }
            using var brush = new SolidBrush(color);
            canvas.DrawString(text, font, brush, x, y, format);
        }

        for (var i = 0; i < count; i++)
        {
            var row = frame.Rows[i];
            var column = i % columns;
            var line = i / columns;
            var left = padX + cellWidths.Take(column).Sum() + between * column;
            var baseline = padY + rowHeight * line + rowHeight * 0.74f;
            var own = row.Color is { } c ? Parse(c, valueColor) : valueColor;
            Text(row.Label, labelFont, row.Alert ? alertColor : labelColor, left, baseline);
            // Numbers line up on the right of their cell (in a row: right after their label).
            var valueX = style.Arrangement == OverlayArrangement.Row ? left + labels[i].Width + (labels[i].Width > 0 ? inner : 0) : left + cellWidths[column] - values[i].Width;
            Text(row.Value, valueFont, row.Alert ? alertColor : own, valueX, baseline);
        }
        return bitmap;
    }

    private static Color Parse(string hex, Color fallback)
    {
        var h = hex.Trim().TrimStart('#');
        return h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? Color.FromArgb(v >> 16 & 0xFF, v >> 8 & 0xFF, v & 0xFF)
            : fallback;
    }

    private static Color Lighter(Color c) => Color.FromArgb(Math.Min(255, c.R + 46), Math.Min(255, c.G + 46), Math.Min(255, c.B + 46));

    private static float Ascent(Font font) =>
        font.Size * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);

    private static string? Installed(string name)
    {
        try
        {
            using var f = new FontFamily(name);
            return name;
        }
        catch (ArgumentException)
        {
            return null; // Not on this PC: the default.
        }
    }

    private static FontFamily Family(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                // Not installed on this PC: try the next one.
            }
        }
        return FontFamily.GenericSansSerif;
    }
}
