using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MBrowser;

/// <summary>Borderless native window with resizing, snapping and custom caption buttons.</summary>
internal class BrowserWindow : Form
{
    private const int WmNcHitTest = 0x0084, WmNcCalcSize = 0x0083, WmNcPaint = 0x0085,
        WmNcActivate = 0x0086, WmStyleChanging = 0x007c, WmGetMinMaxInfo = 0x0024;
    private const int WsCaption = 0x00c00000, WsThickFrame = 0x00040000,
        WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000, WsSystemMenu = 0x00080000;
    private readonly ToolTip _windowTips = new();
    private readonly List<Control> _hiddenInFullscreen = [];
    private CaptionButton? _maximize;
    private Control? _titleFrame;
    protected int TitleBarHeight => _titleFrame?.Height ?? 48;
    protected event Action? FullScreenChanged;
    private Rectangle _restoreBounds;
    private FormWindowState _restoreState;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsFullScreen { get; private set; }

    protected BrowserWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        // Leave a narrow native client edge for hit testing even when child HWNDs fill the window.
        Padding = new Padding(5);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int rounded = 2;
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Keep the native move/resize/system-menu behavior, but never its caption.
            parameters.Style = (parameters.Style & ~WsCaption) |
                WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSystemMenu;
            return parameters;
        }
    }

    protected void InitializeWindowFrame(TabStrip tabs)
    {
        var frame = new ThemePanel { Dock = DockStyle.Top, Height = 48, BackColor = Palette.Background, AccessibleName = "Заголовок окна" };
        _titleFrame = frame;
        var chromeParent = tabs.Parent ?? this;
        var captions = new Panel { Dock = DockStyle.Right, Width = 138, BackColor = Color.Transparent };
        var minimize = new CaptionButton(CaptionGlyph.Minimize) { AccessibleName = "Свернуть", Dock = DockStyle.Left };
        _maximize = new CaptionButton(CaptionGlyph.Maximize) { AccessibleName = "Развернуть или восстановить", Dock = DockStyle.Left };
        var close = new CaptionButton(CaptionGlyph.Close) { AccessibleName = "Закрыть окно", Dock = DockStyle.Left };
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximize.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) => Close();
        _windowTips.SetToolTip(minimize, "Свернуть");
        _windowTips.SetToolTip(_maximize, "Развернуть / восстановить");
        _windowTips.SetToolTip(close, "Закрыть");
        captions.Controls.Add(close);
        captions.Controls.Add(_maximize);
        captions.Controls.Add(minimize);
        tabs.TitleAreaMouseDown += DragTitleArea;
        frame.MouseDown += (_, e) => DragTitleArea(e);
        tabs.Dock = DockStyle.Fill;
        chromeParent.Controls.Remove(tabs);
        frame.Controls.Add(tabs);
        frame.Controls.Add(captions);
        chromeParent.Controls.Add(frame);
        // Docking walks siblings in reverse order: the new top strip must remain last.
    }

    internal void ToggleFullScreen()
    {
        if (!IsFullScreen)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            IsFullScreen = true;
            Padding = Padding.Empty;
            WindowState = FormWindowState.Normal;
            _hiddenInFullscreen.Clear();
            foreach (Control control in Controls.Cast<Control>().Concat(_titleFrame?.Parent != this && _titleFrame is not null ? new[] { _titleFrame } : []))
                if (control.Visible && control.Tag as string != "fullscreen-visible" && control.Dock is DockStyle.Top or DockStyle.Bottom)
                {
                    _hiddenInFullscreen.Add(control);
                    control.Visible = false;
                }
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            IsFullScreen = false;
            Padding = new Padding(5);
            Bounds = _restoreBounds;
            WindowState = _restoreState == FormWindowState.Minimized ? FormWindowState.Normal : _restoreState;
            foreach (Control control in _hiddenInFullscreen) if (!control.IsDisposed) control.Visible = true;
            _hiddenInFullscreen.Clear();
        }
        _maximize?.Invalidate();
        FullScreenChanged?.Invoke();
    }

    private void ToggleMaximize()
    {
        if (IsFullScreen) { ToggleFullScreen(); return; }
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        _maximize?.Invalidate();
    }

    private void DragTitleArea(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || IsFullScreen) return;
        if (e.Clicks == 2) { ToggleMaximize(); return; }
        if (WindowState == FormWindowState.Maximized)
        {
            var cursor = Cursor.Position;
            double fraction = Math.Clamp((cursor.X - Left) / (double)Math.Max(1, Width), 0, 1);
            var previous = RestoreBounds;
            WindowState = FormWindowState.Normal;
            Location = new Point(cursor.X - (int)(previous.Width * fraction), cursor.Y - 24);
        }
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (nint)2, 0);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11) { ToggleFullScreen(); return true; }
        if (keyData == Keys.Escape && IsFullScreen) { ToggleFullScreen(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Padding = IsFullScreen || WindowState == FormWindowState.Maximized ? Padding.Empty : new Padding(5);
        _maximize?.Invalidate();
        Invalidate(true);
    }

    protected override void WndProc(ref Message message)
    {
        // Both NCCALCSIZE variants describe the entire client area. Letting the RECT
        // variant reach DefWindowProc can reserve a native frame during menu changes.
        if (message.Msg == WmNcCalcSize)
        {
            // Returning zero with wParam=TRUE preserves old client pixels. Our
            // cover backgrounds change everywhere on resize, so none remain valid.
            // https://learn.microsoft.com/windows/win32/winmsg/wm-nccalcsize
            message.Result = message.WParam != 0 ? (nint)0x0300 /* WVR_REDRAW */ : 0;
            return;
        }
        if (message.Msg == WmNcPaint)
        {
            message.Result = 0;
            return;
        }
        if (message.Msg == WmNcActivate)
        {
            // A menu or another window changes activation. DefWindowProc must keep
            // its activation bookkeeping, but lParam = -1 prevents a classic caption
            // being painted over our tab strip, even with desktop composition off.
            var originalParameter = message.LParam;
            message.LParam = -1;
            base.WndProc(ref message);
            message.LParam = originalParameter;
            return;
        }
        if (message.Msg == WmStyleChanging && message.WParam == -16)
        {
            // WinForms can refresh window styles when state or properties change.
            // Enforce the same borderless contract after handle creation as well.
            var styles = Marshal.PtrToStructure<StyleChanges>(message.LParam);
            styles.NewStyle &= ~WsCaption;
            Marshal.StructureToPtr(styles, message.LParam, false);
        }
        if (message.Msg == WmGetMinMaxInfo)
        {
            base.WndProc(ref message);
            var monitor = MonitorFromWindow(Handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var limits = Marshal.PtrToStructure<MinMaxInfo>(message.LParam);
                var area = IsFullScreen ? info.Monitor : info.WorkArea;
                limits.MaxPosition = new NativePoint(area.Left - info.Monitor.Left, area.Top - info.Monitor.Top);
                limits.MaxSize = new NativePoint(area.Right - area.Left, area.Bottom - area.Top);
                limits.MinTrackSize = new NativePoint(MinimumSize.Width, MinimumSize.Height);
                Marshal.StructureToPtr(limits, message.LParam, false);
            }
            message.Result = 0;
            return;
        }
        base.WndProc(ref message);
        if (message.Msg != WmNcHitTest || WindowState != FormWindowState.Normal || IsFullScreen) return;
        long position = message.LParam.ToInt64();
        var point = PointToClient(new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff)));
        int grip = Math.Max(5, 7 * DeviceDpi / 96);
        bool left = point.X < grip, right = point.X >= ClientSize.Width - grip;
        bool top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
        int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 1;
        if (hit != 1) message.Result = hit;
    }

    protected override void Dispose(bool disposing) { if (disposing) _windowTips.Dispose(); base.Dispose(disposing); }
    private enum CaptionGlyph { Minimize, Maximize, Close }

    private sealed class CaptionButton : Button
    {
        private readonly CaptionGlyph _glyph;
        private bool _hover;
        public CaptionButton(CaptionGlyph glyph)
        {
            _glyph = glyph;
            Width = 46;
            Height = 48;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            ForeColor = Palette.Text;
            TabStop = false;
            Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            base.OnPaintBackground(e);
            if (_hover) { using var fill = new SolidBrush(_glyph == CaptionGlyph.Close ? Color.FromArgb(193, 48, 73) : Palette.Raised); g.FillRectangle(fill, ClientRectangle); }
            using var pen = new Pen(_hover && _glyph == CaptionGlyph.Close ? Color.White : Palette.Text, 1.15f);
            int x = Width / 2 - 5, y = Height / 2 - 5;
            if (_glyph == CaptionGlyph.Minimize) g.DrawLine(pen, x, y + 5, x + 10, y + 5);
            else if (_glyph == CaptionGlyph.Close)
            {
                g.DrawLine(pen, x, y, x + 10, y + 10);
                g.DrawLine(pen, x + 10, y, x, y + 10);
            }
            else if (FindForm()?.WindowState == FormWindowState.Maximized)
            {
                g.DrawLines(pen, [new(x + 3, y + 7), new(x + 3, y - 1), new(x + 11, y - 1), new(x + 11, y + 7), new(x + 8, y + 7)]);
                g.DrawRectangle(pen, x, y + 2, 8, 8);
            }
            else g.DrawRectangle(pen, x, y, 10, 10);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleChanges { public int OldStyle, NewStyle; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, WorkArea; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
