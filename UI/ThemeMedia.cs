using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace MBrowser;

/// <summary>Lazy, bounded image cache. GIF frames advance on the UI thread only while the app is active.</summary>
internal static class ThemeMedia
{
    private sealed class Media : IDisposable
    {
        internal readonly MemoryStream Stream;
        internal readonly Image Image;
        internal readonly int Frames;
        internal readonly int[] Delays;
        internal readonly List<WeakReference<Control>> Owners = [];
        internal int Frame;
        internal long NextFrame;
        internal Media(string path)
        {
            Stream = new MemoryStream(File.ReadAllBytes(path));
            try
            {
                Image = Image.FromStream(Stream);
                Validate(Image);
                Frames = Image.RawFormat.Guid == ImageFormat.Gif.Guid ? Image.GetFrameCount(FrameDimension.Time) : 1;
                byte[] delays = Image.PropertyIdList.Contains(0x5100) ? Image.GetPropertyItem(0x5100)!.Value! : [];
                Delays = Enumerable.Range(0, Frames).Select(i => delays.Length >= (i + 1) * 4 ? Math.Clamp(BitConverter.ToInt32(delays, i * 4) * 10L, 100, 60000) : 100L).Select(x => (int)x).ToArray();
                NextFrame = Environment.TickCount64 + Delays[0];
            }
            catch { Image?.Dispose(); Stream.Dispose(); throw; }
        }
        public void Dispose() { Image.Dispose(); Stream.Dispose(); }
    }

    private static readonly Dictionary<string, Media?> Cache = [];
    private static readonly System.Windows.Forms.Timer Animation = new() { Interval = 100 };
    private static bool _active;
    static ThemeMedia() => Animation.Tick += (_, _) =>
    {
        foreach (var media in Cache.Values.OfType<Media>().Where(x => x.Frames > 1))
        {
            media.Owners.RemoveAll(x => !x.TryGetTarget(out var control) || control.IsDisposed);
            var visible = media.Owners.Select(x => x.TryGetTarget(out var control) ? control : null).OfType<Control>()
                .Where(x => x.Visible && x.FindForm() is { WindowState: not FormWindowState.Minimized }).ToArray();
            if (visible.Length == 0 || Environment.TickCount64 < media.NextFrame) continue;
            media.Frame = (media.Frame + 1) % media.Frames;
            media.Image.SelectActiveFrame(FrameDimension.Time, media.Frame);
            media.NextFrame = Environment.TickCount64 + media.Delays[media.Frame];
            foreach (var control in visible) control.Invalidate(true);
        }
    };

    internal static void SetActive(bool active)
    {
        _active = active;
        Animation.Enabled = active && Cache.Values.Any(x => x is { Frames: > 1 });
    }
    internal static void ReleaseAll()
    {
        Animation.Stop();
        foreach (var media in Cache.Values) media?.Dispose();
        Cache.Clear();
    }
    internal static void Release(ThemeArea area)
    {
        string path = Resolve(ThemeSettings.Layer(area).File);
        // Shared artwork remains cached while another surface uses the same file.
        if (Enum.GetValues<ThemeArea>().Any(other => other != area && Resolve(ThemeSettings.Layer(other).File) == path)) return;
        if (Cache.Remove(path, out var media)) media?.Dispose();
    }
    internal static string Resolve(string file) => string.IsNullOrEmpty(file) ? "" : Path.Combine(
        file.StartsWith("media-", StringComparison.Ordinal) ? Path.Combine(ThemeSettings.DataPath, "ThemeMedia") : Path.Combine(AppContext.BaseDirectory, "assets"), Path.GetFileName(file));

    internal static void Validate(Image image)
    {
        int frames = image.RawFormat.Guid == ImageFormat.Gif.Guid ? image.GetFrameCount(FrameDimension.Time) : 1;
        if (image.Width > 8192 || image.Height > 8192 || (long)image.Width * image.Height > 32_000_000 ||
            frames > 500 || (long)image.Width * image.Height * frames > 80_000_000)
            throw new InvalidDataException("Изображение слишком большое. До 32 Мп; GIF — до 80 млн пикселей во всех кадрах.");
        if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid && image.RawFormat.Guid != ImageFormat.Gif.Guid)
            throw new InvalidDataException("Поддерживаются PNG, JPEG и GIF.");
    }
    internal static string Import(string source, string dataPath)
    {
        if (new FileInfo(source).Length > 16 * 1024 * 1024) throw new InvalidDataException("Файл должен быть меньше 16 МБ.");
        byte[] bytes = File.ReadAllBytes(source);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Файл должен быть меньше 16 МБ.");
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        Validate(image);
        string extension = image.RawFormat.Guid == ImageFormat.Gif.Guid ? ".gif" : image.RawFormat.Guid == ImageFormat.Png.Guid ? ".png" : ".jpg";
        string file = "media-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + extension;
        string folder = Path.Combine(dataPath, "ThemeMedia");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, file), bytes);
        return file;
    }

    internal static void Draw(Graphics graphics, Rectangle bounds, ThemeArea area, Control owner)
    {
        var crop = graphics.Save();
        try { graphics.SetClip(bounds, CombineMode.Intersect); DrawSurface(graphics, bounds, area, owner); }
        finally { graphics.Restore(crop); }
    }
    private static void DrawSurface(Graphics graphics, Rectangle bounds, ThemeArea area, Control owner)
    {
        using (var background = new SolidBrush(area == ThemeArea.Home ? Palette.Background : Palette.Surface)) graphics.FillRectangle(background, bounds);
        var layer = ThemeSettings.Layer(area);
        string path = Resolve(layer.File);
        if (bounds.Width < 1 || bounds.Height < 1 || path.Length == 0) return;
        if (!Cache.TryGetValue(path, out var media))
        {
            try { media = File.Exists(path) && new FileInfo(path).Length <= 16 * 1024 * 1024 ? new Media(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException) { media = null; }
            Cache[path] = media;
        }
        if (media is null) return;
        if (media.Frames > 1 && !media.Owners.Any(x => x.TryGetTarget(out var control) && control == owner))
        {
            media.Owners.Add(new(owner));
            SetActive(_active);
        }
        var image = media.Image;
        var state = graphics.Save();
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (layer.Fit == "tile")
        {
            using var texture = new TextureBrush(image, WrapMode.Tile);
            using var transform = new Matrix();
            transform.Scale(layer.Zoom,layer.Zoom);
            transform.Translate(bounds.X + layer.PanX * bounds.Width,bounds.Y + layer.PanY * bounds.Height,MatrixOrder.Append);
            texture.Transform = transform;
            graphics.FillRectangle(texture, bounds);
        }
        else
        {
            float scale = (layer.Fit == "contain" ? Math.Min(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height) : Math.Max(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height)) * layer.Zoom;
            var target = new RectangleF(bounds.X + (bounds.Width - image.Width * scale) / 2 + layer.PanX * bounds.Width,
                bounds.Y + (bounds.Height - image.Height * scale) / 2 + layer.PanY * bounds.Height, image.Width * scale, image.Height * scale);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(image, Rectangle.Round(target), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        using var tint = new SolidBrush(Color.FromArgb(layer.Tint, Palette.Surface));
        graphics.FillRectangle(tint, bounds);
        graphics.Restore(state);
    }

    internal static void DrawChromeSlice(Graphics graphics, Control owner)
    {
        Control? host = owner;
        while (host is not null && host is not ChromeHost) host = host.Parent;
        if (host is null) { Draw(graphics, owner.ClientRectangle, ThemeArea.Chrome, owner); return; }
        Point offset = host.PointToClient(owner.PointToScreen(Point.Empty));
        var state = graphics.Save();
        graphics.TranslateTransform(-offset.X, -offset.Y);
        Draw(graphics, host.ClientRectangle, ThemeArea.Chrome, owner);
        graphics.Restore(state);
    }
}

internal sealed class ChromeHost : ThemePanel
{
    internal ChromeHost() { Area = ThemeArea.Chrome; Tag = "fullscreen-visible"; Dock = DockStyle.Top; Height = 143; }
}

internal class ThemePanel : Panel
{
    internal ThemeArea Area = ThemeArea.Tabs;
    internal ThemePanel() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Area is ThemeArea.Home or ThemeArea.Menu) ThemeMedia.Draw(e.Graphics, ClientRectangle, Area, this);
        else ThemeMedia.DrawChromeSlice(e.Graphics, this);
    }
}
internal sealed class ThemeToolbar : TableLayoutPanel
{
    internal ThemeToolbar() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnPaintBackground(PaintEventArgs e) => ThemeMedia.DrawChromeSlice(e.Graphics, this);
}
internal sealed class ThemeBookmarks : FlowLayoutPanel
{
    internal ThemeBookmarks() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnPaintBackground(PaintEventArgs e) => ThemeMedia.DrawChromeSlice(e.Graphics, this);
}
