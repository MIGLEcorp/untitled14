using System.Runtime.InteropServices;
namespace Feather;
internal static class WindowFrameTests
{
    internal static async Task RunAsync(MainForm form, Action<bool,string> check)
    {
        var bounds=form.Bounds;
        form.ShowMainMenu();
        check(form.IsInternalMenuVisible, "Menu is a visible child panel inside the browser");
        check(form.InternalMenuBounds.Left>=0 && form.InternalMenuBounds.Top>=0, "Menu is positioned in the content client area");
        check((GetWindowLongPtr(form.Handle,-16).ToInt64() & 0x00c00000)==0, "Opening the internal menu does not restore a native caption");
        if (form.InternalMenuControlForTest is { } panel)
            using (var image = new Bitmap(panel.Width, panel.Height))
            { panel.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save("preview-menu.png", System.Drawing.Imaging.ImageFormat.Png); }
        form.ToggleFullScreen();
        await Task.Delay(100);
        form.ShowMainMenu();
        check(form.IsInternalMenuVisible && form.Bounds==Screen.FromControl(form).Bounds, "Internal menu opens while fullscreen fills the monitor");
        form.ToggleFullScreen();
        await Task.Delay(100);
        check(form.Bounds==bounds, "Fullscreen restores the previous window bounds");
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window,int index);
}
