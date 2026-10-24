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
        check("Theme editor colors and five independent layers survive a reload", () =>
        {
            ThemeSettings.Load(profile);
            var doc = ThemeSettings.Preset(BrowserTheme.Anime) with { Name = "Розовая тема", Colors = new() { ["Accent"] = "#F080B0" } };
            doc.Layers[ThemeArea.Toolbar] = new("kawaii-chrome.jpg", "tile", 99);
            ThemeSettings.Save(doc, profile); ThemeSettings.Load(profile);
            Assert(ThemeSettings.Document.Name == doc.Name && ThemeSettings.Colors.Accent.ToArgb() == Color.FromArgb(240,128,176).ToArgb(), "Theme colors/name lost");
            Assert(ThemeSettings.Layer(ThemeArea.Toolbar).Fit == "tile" && ThemeSettings.Layer(ThemeArea.Toolbar).Tint == 99 && ThemeSettings.Document.Layers.Count == 5, "Independent layers lost");
            File.WriteAllText(Path.Combine(profile, "theme.json"), "\"Dark\""); ThemeSettings.Load(profile);
            Assert(ThemeSettings.Current == BrowserTheme.Dark, "Legacy theme not migrated");
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
