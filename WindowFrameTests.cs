using System.Reflection;
using System.Runtime.InteropServices;

namespace Feather;

/// <summary>Exercises native custom-frame behavior on the isolated smoke-test window.</summary>
internal static class WindowFrameTests
{
    internal static async Task RunAsync(MainForm form, Action<bool, string> check)
    {
        var menu = typeof(MainForm).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as ContextMenuStrip
            ?? throw new InvalidOperationException("The browser menu was not found for the frame regression check.");
        var originalState = form.WindowState;
        var originalBounds = originalState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        bool originallyFullscreen = form.IsFullScreen;

        void CheckFrame(string phase)
        {
            long style = GetWindowLongPtr(form.Handle, -16).ToInt64();
            check((style & 0x00c00000) == 0, $"{phase}: native caption style remains absent");
            check(form.RectangleToScreen(form.ClientRectangle) == form.Bounds,
                $"{phase}: custom client area fills the entire native window");
        }

        try
        {
            if (form.IsFullScreen) form.ToggleFullScreen();
            form.WindowState = FormWindowState.Normal;
            await Task.Delay(100);
            CheckFrame("Before opening browser menu");

            // Use the actual menu, including its normal Opening handlers and child HWNDs.
            menu.Show(form, new Point(Math.Max(5, form.ClientSize.Width - 400), 110));
            await Task.Delay(100);
            check(menu.Visible, "The actual browser menu opens during the frame regression check");
            CheckFrame("With browser menu open");
            SendMessage(form.Handle, 0x0086, 0, 0); // WM_NCACTIVATE: inactive.
            SendMessage(form.Handle, 0x0086, 1, 0); // WM_NCACTIVATE: active again.
            SendMessage(form.Handle, 0x0085, 1, 0); // WM_NCPAINT: force a native frame repaint.
            CheckFrame("After menu activation and native repaint");

            // Read the visible native surface without triggering a repaint that could
            // hide a classic caption painted over the custom tab strip.
            nint dc = GetWindowDC(form.Handle);
            try
            {
                uint pixel = GetPixel(dc, Math.Max(5, form.Width / 2), 2);
                var expected = Palette.Background;
                uint background = (uint)(expected.R | expected.G << 8 | expected.B << 16);
                check(dc != 0 && pixel == background,
                    "Menu activation leaves the custom top edge visible instead of painting a native caption");
            }
            finally { if (dc != 0) ReleaseDC(form.Handle, dc); }
            menu.Close();

            // The RECT (wParam FALSE) path is also used by native frame recalculation.
            nint rectangle = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            try
            {
                var requested = new NativeRect { Left = 10, Top = 20, Right = 610, Bottom = 420 };
                Marshal.StructureToPtr(requested, rectangle, false);
                SendMessage(form.Handle, 0x0083, 0, rectangle);
                var calculated = Marshal.PtrToStructure<NativeRect>(rectangle);
                check(calculated.Equals(requested),
                    "Native RECT recalculation does not reserve space for a system caption or border");
            }
            finally { Marshal.FreeHGlobal(rectangle); }

            var normalBounds = form.Bounds;
            form.WindowState = FormWindowState.Maximized;
            await Task.Delay(100);
            check(form.Bounds == Screen.FromControl(form).WorkingArea,
                "Custom maximize fits the monitor work area");
            CheckFrame("Maximized custom frame");
            form.WindowState = FormWindowState.Normal;
            await Task.Delay(100);
            check(form.Bounds == normalBounds, "Custom restore returns to the previous window bounds");
            form.ToggleFullScreen();
            await Task.Delay(100);
            check(form.IsFullScreen && form.Bounds == Screen.FromControl(form).Bounds,
                "Custom fullscreen fits the entire monitor");
            CheckFrame("Fullscreen custom frame");
            form.ToggleFullScreen();
            await Task.Delay(100);
            check(!form.IsFullScreen && form.Bounds == normalBounds,
                "Leaving custom fullscreen restores the previous window bounds");
        }
        finally
        {
            menu.Close();
            if (form.IsFullScreen) form.ToggleFullScreen();
            form.WindowState = FormWindowState.Normal;
            form.Bounds = originalBounds;
            form.WindowState = originalState;
            if (originallyFullscreen) form.ToggleFullScreen();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint dc, int x, int y);
}
