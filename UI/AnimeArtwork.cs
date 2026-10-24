using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Feather;

/// <summary>One local illustration shared by chrome. Full-size pixels are released when home is hidden.</summary>
internal static class AnimeArtwork
{
    private static Bitmap? _backdrop;
    private static readonly Bitmap?[] Portraits = new Bitmap?[2];
    private static string PathName => Path.Combine(AppContext.BaseDirectory, "assets", "anime-sakura.png");

    private static Bitmap? Load()
    {
        if (!File.Exists(PathName)) return null;
        using var stream = File.OpenRead(PathName);
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    internal static void ReleaseBackdrop() { _backdrop?.Dispose(); _backdrop = null; }

    internal static Image? Portrait(int index)
    {
        index &= 1;
        if (Portraits[index] is not null) return Portraits[index];
        using var source = Load();
        if (source is null) return null;
        var target = new Bitmap(80, 80);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // Runtime source rectangles keep one editable original asset, without duplicate full-size images.
        var crop = index == 0 ? new RectangleF(.49f, .015f, .24f, .36f) : new RectangleF(.745f, .14f, .25f, .375f);
        graphics.DrawImage(source, new Rectangle(0, 0, 80, 80), crop.X * source.Width, crop.Y * source.Height,
            crop.Width * source.Width, crop.Height * source.Height, GraphicsUnit.Pixel);
        Portraits[index] = target;
        return target;
    }

    internal static void DrawPortrait(Graphics graphics, Rectangle bounds, int index)
    {
        if (ThemeSettings.Current != BrowserTheme.Anime || Portrait(index) is not { } portrait) return;
        var saved = graphics.Save();
        using var circle = new GraphicsPath();
        circle.AddEllipse(bounds);
        graphics.SetClip(circle, CombineMode.Intersect);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(portrait, bounds);
        graphics.Restore(saved);
        using var outline = new Pen(Color.FromArgb(140, Palette.Accent), 1);
        graphics.DrawEllipse(outline, bounds);
    }

    internal static bool DrawBackdrop(Graphics graphics, Rectangle bounds)
    {
        _backdrop ??= Load();
        if (_backdrop is null) return false;
        float scale = Math.Max(bounds.Width / (float)_backdrop.Width, bounds.Height / (float)_backdrop.Height);
        float width = _backdrop.Width * scale, height = _backdrop.Height * scale;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(_backdrop, bounds.Left + (bounds.Width - width) / 2, bounds.Top + (bounds.Height - height) / 2, width, height);
        using var shade = new LinearGradientBrush(bounds, Palette.Background, Color.Transparent, 0f);
        shade.InterpolationColors = new ColorBlend
        {
            Colors = [Color.FromArgb(240, Palette.Background), Color.FromArgb(220, Palette.Background), Color.FromArgb(50, Palette.Background), Color.Transparent],
            Positions = [0, .36f, .65f, 1]
        };
        graphics.FillRectangle(shade, bounds);
        return true;
    }
}

internal sealed class AnimeBadge : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Character { get; set; }
    internal AnimeBadge() { DoubleBuffered = true; AccessibleName = "Персонаж темы Sakura"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ThemeSettings.Current == BrowserTheme.Anime)
            AnimeArtwork.DrawPortrait(e.Graphics, new Rectangle(3, 3, Math.Max(1, Math.Min(Width, Height) - 6), Math.Max(1, Math.Min(Width, Height) - 6)), Character);
    }
}

internal sealed class AnimeMenuBanner : Control
{
    internal AnimeMenuBanner() { Size = new Size(365, 100); DoubleBuffered = true; AccessibleName = "Feather Sakura"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.Surface);
        AnimeArtwork.DrawPortrait(e.Graphics, new Rectangle(Width - 164, 10, 76, 76), 0);
        AnimeArtwork.DrawPortrait(e.Graphics, new Rectangle(Width - 86, 10, 76, 76), 1);
        using var brand = new Font("Segoe UI", 17, FontStyle.Bold);
        using var sub = new Font("Segoe UI", 9);
        TextRenderer.DrawText(e.Graphics, "FEATHER", brand, new Rectangle(14, 20, 180, 34), Palette.Text);
        TextRenderer.DrawText(e.Graphics, "S A K U R A", sub, new Rectangle(16, 57, 160, 24), Palette.Accent);
    }
}
