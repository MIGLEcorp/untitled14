using System.Drawing.Drawing2D;

namespace Feather;

/// <summary>Original, human-drawn artwork. Attribution is shipped next to the images.</summary>
internal static class AnimeArtwork
{
    private static Image? _wallpaper;
    internal static string FileName { get; set; } = "rain-city.jpg";
    internal static void ReleaseBackdrop() { _wallpaper?.Dispose(); _wallpaper = null; }
    internal static Image? Wallpaper
    {
        get
        {
            if (_wallpaper is not null) return _wallpaper;
            string path = Path.Combine(AppContext.BaseDirectory, "assets", FileName);
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream);
            double scale = Math.Min(1, Math.Min(2048d / image.Width, 1152d / image.Height));
            _wallpaper = new Bitmap(image, new Size(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))));
            return _wallpaper;
        }
    }
    internal static bool DrawBackdrop(Graphics g, Rectangle bounds)
    {
        if (bounds.Width < 1 || bounds.Height < 1 || Wallpaper is not { } image) return false;
        float scale = Math.Max(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
        float width = image.Width * scale, height = image.Height * scale;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2, width, height);
        return true;
    }
}

internal class GlassPanel : Panel
{
    internal GlassPanel()
    {
        DoubleBuffered = true;
        Padding = new Padding(14);
        BackColor = Palette.Surface;
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 2 || Height <= 2) return;
        using var shape = TabStrip.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(18, (Math.Min(Width, Height) - 1) / 2));
        var previous = Region;
        Region = new Region(shape);
        previous?.Dispose();
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Surface);
        if (ThemeSettings.Current == BrowserTheme.Anime) AnimeArtwork.DrawBackdrop(e.Graphics, ClientRectangle);
        using var tint = new SolidBrush(Color.FromArgb(218, Palette.Surface));
        e.Graphics.FillRectangle(tint, ClientRectangle);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 2 || Height <= 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = TabStrip.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(18, (Math.Min(Width, Height) - 1) / 2));
        using var border = new Pen(Color.FromArgb(65, Palette.Text));
        e.Graphics.DrawPath(border, shape);
    }
}

internal sealed class RoundedButton : Button
{
    internal RoundedButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = TabStrip.RoundedRectangle(new Rectangle(1, 1, Width - 3, Height - 3), 11);
        bool hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        using var fill = new SolidBrush(BackColor.A == 0 ? Color.FromArgb(hovered ? 165 : 80, Palette.Surface) : hovered ? Palette.Raised : BackColor);
        e.Graphics.FillPath(fill, shape);
        var textArea = Width <= 50 ? ClientRectangle : new Rectangle(8, 0, Width - 16, Height);
        if (Image is not null) { e.Graphics.DrawImage(Image, 10, (Height - 18) / 2, 18, 18); textArea.X += 26; textArea.Width -= 26; }
        TextRenderer.DrawText(e.Graphics, Text, Font, textArea, Enabled ? ForeColor : Palette.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
            (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
    }
}
