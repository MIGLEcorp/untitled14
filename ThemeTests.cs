using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MBrowser;

internal static class ThemeTests
{
    private const string AnimatedFixture = "R0lGODlhAgACAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAgACAAAIBgABCAQQEAAh+QQBCgABACwAAAAAAgACAIEAAP8AAAAAAAAAAAAIBgABCAQQEAA7";

    internal static void RunCore(Action<string, Action> check, string folder)
    {
        string profile = Path.Combine(folder, "theme-profile");
        Directory.CreateDirectory(profile);
        check("Theme colors, unified header and image transforms survive a reload", () =>
        {
            ThemeSettings.Load(profile);
            var doc = ThemeSettings.Preset(BrowserTheme.Anime) with { Name = "Розовая тема", Colors = new() { ["Accent"] = "#F080B0" } };
            doc.Layers[ThemeArea.Chrome] = new("sakura-header.png", "tile", 99, 1.5f, .2f, -.1f);
            ThemeSettings.Save(doc, profile); ThemeSettings.Load(profile);
            Assert(ThemeSettings.Document.Name == doc.Name && ThemeSettings.Colors.Accent.ToArgb() == Color.FromArgb(240,128,176).ToArgb(), "Theme colors/name lost");
            Assert(ThemeSettings.Layer(ThemeArea.Chrome).Fit == "tile" && ThemeSettings.Layer(ThemeArea.Chrome).Tint == 99 && ThemeSettings.Document.Layers.Count == 3 && ThemeSettings.Layer(ThemeArea.Chrome).Zoom == 1.5f, "Unified header or transform lost");
            var migrated = ThemeSettings.Normalize(new() { Layers = new() { [ThemeArea.Tabs] = new("sakura-header.png"), [ThemeArea.Toolbar] = new("legacy.png", "cover", 71) } });
            Assert(migrated.Layers[ThemeArea.Chrome].File == "legacy.png" && !migrated.Layers.ContainsKey(ThemeArea.Tabs), "Old split header was not migrated");
            File.WriteAllText(Path.Combine(profile, "theme.json"), "\"Dark\""); ThemeSettings.Load(profile);
            Assert(ThemeSettings.Current == BrowserTheme.Dark, "Legacy theme not migrated");
        });
        check("Header strips sample one PNG in shared coordinates without seams or overpainting", () =>
        {
            ThemeSettings.Load(profile);
            string fixture = Path.Combine(profile,"gradient.png");
            using (var source = new Bitmap(240,143))
            { for (int y=0;y<143;y++) for (int x=0;x<240;x++) source.SetPixel(x,y,Color.FromArgb(x,y,(x+y)%255)); source.Save(fixture,ImageFormat.Png); }
            var document = ThemeSettings.Preset(BrowserTheme.Anime);
            document.Layers[ThemeArea.Chrome] = new(ThemeMedia.Import(fixture,profile),"cover",0,1.5f,.1f,-.05f);
            ThemeSettings.Preview(document);
            using var host = new ChromeHost { Size = new Size(240,143) };
            using var tabs = new ThemePanel { Bounds = new Rectangle(0,0,240,48) };
            using var toolbar = new ThemeToolbar { Bounds = new Rectangle(0,48,240,58) };
            using var bookmarks = new ThemeBookmarks { Bounds = new Rectangle(0,106,240,37) };
            host.Controls.Add(tabs); host.Controls.Add(toolbar); host.Controls.Add(bookmarks);
            using var entire = new Bitmap(240,143);
            using (var graphics=Graphics.FromImage(entire)) ThemeMedia.Draw(graphics,host.ClientRectangle,ThemeArea.Chrome,host);
            foreach (Control strip in new Control[] { tabs,toolbar,bookmarks })
            {
                using var slice = new Bitmap(strip.Width,strip.Height);
                using (var graphics=Graphics.FromImage(slice)) ThemeMedia.DrawChromeSlice(graphics,strip);
                for(int y=3;y<slice.Height-3;y+=7) for(int x=3;x<slice.Width-3;x+=13)
                    Assert(slice.GetPixel(x,y)==entire.GetPixel(x,y+strip.Top),"Header image restarted in a child strip");
            }
            using var canvas = new Bitmap(100,100);
            using (var graphics=Graphics.FromImage(canvas)) { graphics.Clear(Color.Lime); ThemeMedia.Draw(graphics,new Rectangle(20,20,50,50),ThemeArea.Chrome,host); }
            Assert(canvas.GetPixel(3,3).ToArgb()==Color.Lime.ToArgb() && canvas.GetPixel(90,90).ToArgb()==Color.Lime.ToArgb(),"Preview overpainted another area");
            document.Layers[ThemeArea.Chrome] = document.Layers[ThemeArea.Chrome] with { Fit="tile", Zoom=1, PanX=0, PanY=0 };
            ThemeSettings.Preview(document);
            using (var graphics=Graphics.FromImage(entire)) ThemeMedia.Draw(graphics,host.ClientRectangle,ThemeArea.Chrome,host);
            int before=entire.GetPixel(100,70).ToArgb();
            document.Layers[ThemeArea.Chrome] = document.Layers[ThemeArea.Chrome] with { Zoom=2, PanX=.1f };
            ThemeSettings.Preview(document);
            using (var graphics=Graphics.FromImage(entire)) ThemeMedia.Draw(graphics,host.ClientRectangle,ThemeArea.Chrome,host);
            Assert(entire.GetPixel(100,70).ToArgb()!=before,"Tile mode ignored image transforms");
        });
        check("Portable theme packages copy real images and never extract arbitrary paths", () =>
        {
            ThemeSettings.Load(profile);
            string gif = Path.Combine(profile, "fixture.gif"); File.WriteAllBytes(gif, Convert.FromBase64String(AnimatedFixture));
            string file = ThemeMedia.Import(gif, profile);
            var doc = ThemeSettings.Preset(BrowserTheme.Anime);
            doc.Layers[ThemeArea.Menu] = new(file, "contain", 54);
            string package = Path.Combine(folder, "portable.mbtheme");
            ThemePackage.Export(package, doc);
            // This entry must never be extracted, even though it is a valid ZIP member.
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) writer.Write("unsafe");
            string next = Path.Combine(folder, "theme-import");
            var imported = ThemePackage.Import(package, next);
            Assert(imported.Layers[ThemeArea.Menu].Tint == 54, "Layer opacity lost");
            string importedGif = Path.Combine(next, "ThemeMedia", imported.Layers[ThemeArea.Menu].File);
            Assert(File.ReadAllBytes(importedGif).SequenceEqual(File.ReadAllBytes(gif)), "GIF was flattened or lost");
            Assert(!File.Exists(Path.Combine(folder, "escape.txt")), "ZIP traversal extracted");
            var normalized = ThemeSettings.Normalize(new ThemeDocument { Layers = new() { [ThemeArea.Home] = new("../../secret.png", "invalid", -1) } });
            Assert(normalized.Layers[ThemeArea.Home] == new ThemeLayer("secret.png", "cover", 0), "Unsafe theme settings survived normalization");
        });
        check("Theme importer rejects corrupt images and incomplete packages", () =>
        {
            string bad = Path.Combine(folder, "bad.png"); File.WriteAllText(bad, "not an image");
            bool rejected = false;
            try { ThemeMedia.Import(bad, profile); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid image accepted");
            string zipPath = Path.Combine(folder, "missing.mbtheme");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("theme.json").Open())) writer.Write(JsonSerializer.Serialize(ThemeSettings.Preset(BrowserTheme.Anime)));
            rejected = false;
            try { ThemePackage.Import(zipPath, profile); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Missing image silently accepted");
        });
        check("Legacy theme packages keep their embedded images while merging the header", () =>
        {
            string package = Path.Combine(folder,"legacy.mbtheme");
            var old = new ThemeDocument { Layers = new() { [ThemeArea.Home] = new("kawaii-home.jpg"),
                [ThemeArea.Toolbar] = new("kawaii-chrome.jpg"), [ThemeArea.Menu] = new("kawaii-menu.jpg") } };
            using (var archive = ZipFile.Open(package,ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("theme.json").Open())) writer.Write(JsonSerializer.Serialize(old));
                foreach (string name in new[] { "kawaii-home.jpg","kawaii-menu.jpg","kawaii-chrome.jpg" })
                { using var entry = archive.CreateEntry("media/" + name).Open(); entry.Write(Convert.FromBase64String(AnimatedFixture)); }
            }
            var imported = ThemePackage.Import(package,profile);
            Assert(imported.Layers.Count==3 && imported.Layers[ThemeArea.Chrome].File.StartsWith("media-"),"Legacy package image not imported");
        });
        ThemeSettings.Load(profile);
    }

    internal static async Task RunUiAsync(MainForm form, string profile, Action<bool,string> check)
    {
        var original = ThemeSettings.Document;
        var home = Descendants(form).OfType<HomeSurface>().Single();
        var initialSize = form.Size;
        bool fullInvalidation = false;
        InvalidateEventHandler invalidation = (_, e) => { if (e.InvalidRect.Contains(home.ClientRectangle)) fullInvalidation = true; };
        home.Invalidated += invalidation;
        try
        {
            bool allResizesInvalidated = true;
            foreach (var size in new[] { new Size(880, 660), new Size(1130, 760), new Size(920, 680), initialSize })
            {
                fullInvalidation = false;
                form.Size = size; await Task.Delay(100);
                allResizesInvalidated &= fullInvalidation;
            }
            check(allResizesInvalidated, "All four resizes invalidate the entire wallpaper, including the old search position");
            nint memory = Marshal.AllocHGlobal(128);
            try
            {
                Marshal.Copy(new byte[128], 0, memory, 128);
                check(SendMessage(form.Handle, 0x0083, 1, memory) == 0x300, "Native resizing requests a complete redraw instead of copying stale client pixels");
            }
            finally { Marshal.FreeHGlobal(memory); }
            check(Path.GetFileName(Application.ExecutablePath) == "MBrowser.exe" && form.Icon is not null && form.Text.Contains("MBrowser"), "Executable name, window title and application icon use MBrowser branding");
            form.ShowThemeMenu();
            var editor = form.InternalMenuControlForTest!;
            var canvas = Descendants(editor).OfType<ThemeCanvas>().Single();
            var header = canvas.ChromeRectangle;
            int PointParameter(int x,int y) => (y << 16) | (x & 0xffff);
            int cx=header.X+header.Width/2,cy=header.Y+header.Height/2;
            SendMessage(canvas.Handle,0x0201,1,PointParameter(cx,cy));
            SendMessage(canvas.Handle,0x0200,1,PointParameter(cx+25,cy+6));
            SendMessage(canvas.Handle,0x0202,0,PointParameter(cx+25,cy+6));
            check(ThemeSettings.Layer(ThemeArea.Chrome).PanX>0 && canvas.SelectedArea==ThemeArea.Chrome,"Dragging the graphical canvas moves the entire header background");
            SendMessage(canvas.Handle,0x0100,(nint)Keys.Add,0);
            check(ThemeSettings.Layer(ThemeArea.Chrome).Zoom>1,"The graphical canvas supports keyboard image zoom");
            var pink = Descendants(editor).OfType<Button>().Single(x => x.Text == "Pink kawaii");
            var dark = Descendants(editor).OfType<Button>().Single(x => x.Text == "Тёмная");
            dark.PerformClick();
            check(ThemeSettings.Current == BrowserTheme.Dark, "Theme editor previews changes immediately");
            form.Size = new Size(1000, 700); await Task.Delay(70);
            check(form.InternalMenuControlForTest == editor && !editor.IsDisposed, "Theme editor stays open and fits after resizing");
            form.CloseOverlay();
            check(ThemeSettings.Document.Name == original.Name && ThemeSettings.Current == original.Base, "Closing the editor without saving restores the original theme");
            form.Size = initialSize;
            form.ShowThemeMenu();
            editor = form.InternalMenuControlForTest!;
            Descendants(editor).OfType<Button>().Single(x => x.Text == "Pink kawaii").PerformClick();
            editor.Refresh(); await Task.Delay(80);
            using (var bitmap = Capture(editor)) bitmap.Save("preview-theme.png", ImageFormat.Png);
            Descendants(editor).OfType<Button>().Single(x => x.Text == "Сохранить тему").PerformClick();
            check(!form.IsInternalMenuVisible && File.Exists(Path.Combine(profile, "theme.json")), "Saving commits the preview to the profile and closes the editor");

            string gif = Path.Combine(profile, "animation.gif"); File.WriteAllBytes(gif, Convert.FromBase64String(AnimatedFixture));
            string file = ThemeMedia.Import(gif, profile);
            var animated = original with { Layers = new(original.Layers) { [ThemeArea.Home] = new(file, "cover", 0) } };
            ThemeSettings.Preview(animated);
            using var image = new Bitmap(20,20);
            using var graphics = Graphics.FromImage(image);
            ThemeMedia.Draw(graphics, new Rectangle(0,0,20,20), ThemeArea.Home, home);
            int first = image.GetPixel(10,10).ToArgb();
            ThemeMedia.SetActive(true);
            bool advanced = false;
            for (int i = 0; i < 12 && !advanced; i++)
            { await Task.Delay(50); ThemeMedia.Draw(graphics, new Rectangle(0,0,20,20), ThemeArea.Home, home); advanced = image.GetPixel(10,10).ToArgb() != first; }
            check(advanced, "Imported GIF backgrounds advance actual image frames");
            ThemeMedia.SetActive(false);
            ThemeMedia.Draw(graphics, new Rectangle(0,0,20,20), ThemeArea.Home, home); first = image.GetPixel(10,10).ToArgb();
            await Task.Delay(250);
            ThemeMedia.Draw(graphics, new Rectangle(0,0,20,20), ThemeArea.Home, home);
            check(image.GetPixel(10,10).ToArgb() == first, "GIF animation pauses while the browser is inactive");
        }
        finally
        {
            home.Invalidated -= invalidation;
            form.CloseOverlay(); form.Size = initialSize;
            ThemeSettings.Save(original, profile); Palette.Apply(form); ThemeMedia.SetActive(true);
        }
    }
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));
    internal static Bitmap Capture(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        using var graphics = Graphics.FromImage(bitmap);
        nint dc = graphics.GetHdc();
        try { if (!PrintWindow(control.Handle, dc, 2)) throw new InvalidOperationException("Cannot render the application's own window."); }
        finally { graphics.ReleaseHdc(dc); }
        return bitmap;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll", EntryPoint="SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
}
