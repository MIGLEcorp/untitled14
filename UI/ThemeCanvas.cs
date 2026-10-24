using System.Drawing.Drawing2D;

namespace MBrowser;

/// <summary>Interactive browser mockup. The entire header shares one image and coordinate system.</summary>
internal sealed class ThemeCanvas : Control
{
    internal ThemeArea SelectedArea = ThemeArea.Chrome;
    internal Size ChromeReferenceSize = new(1180,143);
    internal event Action<ThemeArea>? AreaSelected;
    internal event Action<float, float>? PanChanged;
    internal event Action<float>? ZoomChanged;
    internal event Action<string>? ImageDropped;
    private Point _lastMouse;
    private bool _dragging;
    internal ThemeCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill; AllowDrop = true; TabStop = true;
        AccessibleName = "Макет темы. Выберите панель, перетащите фон; колёсико меняет масштаб";
        Cursor = Cursors.Hand;
        DragEnter += (_, e) => e.Effect = e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return;
            SelectArea(AreaAt(PointToClient(new Point(e.X, e.Y)))); ImageDropped?.Invoke(paths[0]);
        };
    }
    internal Rectangle WindowRectangle => new(16, 42, Math.Max(80, Width - 32), Math.Max(130, Height - 104));
    internal Rectangle ChromeRectangle => new(WindowRectangle.X, WindowRectangle.Y, WindowRectangle.Width,
        Math.Clamp((int)Math.Round(WindowRectangle.Width * ChromeReferenceSize.Height / (double)Math.Max(1,ChromeReferenceSize.Width)),32,WindowRectangle.Height/3));
    internal Rectangle HomeRectangle => Rectangle.FromLTRB(WindowRectangle.Left, ChromeRectangle.Bottom, WindowRectangle.Right, WindowRectangle.Bottom);
    internal Rectangle MenuRectangle => new(WindowRectangle.Right - Math.Min(146, WindowRectangle.Width / 3) - 9, HomeRectangle.Top + 9,
        Math.Min(146, WindowRectangle.Width / 3), Math.Min(238, Math.Max(80, HomeRectangle.Height - 18)));
    internal ThemeArea AreaAt(Point point) => MenuRectangle.Contains(point) ? ThemeArea.Menu : ChromeRectangle.Contains(point) ? ThemeArea.Chrome : ThemeArea.Home;
    internal Rectangle AreaRectangle(ThemeArea area) => area == ThemeArea.Chrome ? ChromeRectangle : area == ThemeArea.Menu ? MenuRectangle : HomeRectangle;
    internal void SelectArea(ThemeArea area) { SelectedArea = area; AreaSelected?.Invoke(area); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "МАКЕТ ТЕМЫ", Font, new Point(18, 13), Palette.Muted);
        var state = g.Save();
        using var shape = TabStrip.RoundedRectangle(WindowRectangle, 14);
        g.SetClip(shape);
        ThemeMedia.Draw(g, ChromeRectangle, ThemeArea.Chrome, this);
        ThemeMedia.Draw(g, HomeRectangle, ThemeArea.Home, this);
        int x = ChromeRectangle.Left, y = ChromeRectangle.Top, width = ChromeRectangle.Width;
        using var surface = new SolidBrush(Color.FromArgb(220, Palette.Surface));
        g.FillRectangle(surface, x + 7, y + 5, Math.Min(140, width / 3), 21);
        using var small = new Font(Font.FontFamily, 8);
        TextRenderer.DrawText(g, "Новая вкладка   ×", small, new Rectangle(x + 12, y + 7, 130, 20), Palette.Text, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "＋", Font, new Point(x + Math.Min(155, width / 3 + 12), y + 7), Palette.Text);
        int navigationY = y + ChromeRectangle.Height / 3 + 3;
        var address = new Rectangle(x + 62, navigationY, Math.Max(60, width - 116), 22);
        using (var path = TabStrip.RoundedRectangle(address, 8)) g.FillPath(surface, path);
        TextRenderer.DrawText(g, "‹   ›   ⌂", Font, new Point(x + 9, navigationY + 2), Palette.Text);
        TextRenderer.DrawText(g, "Поиск или адрес сайта", small, address, Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "☰", Font, new Point(x + width - 29, navigationY + 2), Palette.Text);
        if (ChromeRectangle.Height >= 65) TextRenderer.DrawText(g, "★  Закладки на главной     ♫  Музыка", small, new Point(x + 12, y + ChromeRectangle.Height * 2 / 3 + 3), Palette.Text);
        var search = new Rectangle(HomeRectangle.X + HomeRectangle.Width / 10, HomeRectangle.Y + HomeRectangle.Height / 3, HomeRectangle.Width * 3 / 5, 35);
        using (var path = TabStrip.RoundedRectangle(search, 12)) g.FillPath(surface, path);
        TextRenderer.DrawText(g, "Поиск в интернете", Font, search, Palette.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        var menu = MenuRectangle;
        using (var menuShape = TabStrip.RoundedRectangle(menu, 12))
        {
            var clip = g.Save(); g.SetClip(menuShape, CombineMode.Intersect);
            ThemeMedia.Draw(g, menu, ThemeArea.Menu, this); g.Restore(clip);
        }
        int line = menu.Y + 14;
        foreach (string label in new[] { "MBrowser", "Новая вкладка", "Закладки", "Пароли", "Оформление", "Расширения" })
        { TextRenderer.DrawText(g, label, small, new Rectangle(menu.X + 10, line, menu.Width - 18, 20), Palette.Text, TextFormatFlags.EndEllipsis); line += 31; }
        g.Restore(state);
        var selected = AreaRectangle(SelectedArea); selected.Inflate(-2,-2);
        using var border = new Pen(Palette.Accent, 2); g.DrawRectangle(border, selected);
        using var handle = new SolidBrush(Palette.Accent);
        foreach (var point in new[] { selected.Location, new Point(selected.Right, selected.Top), new Point(selected.Left, selected.Bottom), new Point(selected.Right, selected.Bottom) })
            g.FillEllipse(handle, point.X - 4, point.Y - 4, 8, 8);
        TextRenderer.DrawText(g, "Выберите область · перетащите фон · колёсико — масштаб", Font,
            new Rectangle(16, Height - 46, Width - 32, 42), Palette.Muted, TextFormatFlags.WordBreak);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !WindowRectangle.Contains(e.Location)) return;
        Focus(); SelectArea(AreaAt(e.Location)); _lastMouse = e.Location; _dragging = true; Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var bounds = AreaRectangle(SelectedArea);
        PanChanged?.Invoke((e.X - _lastMouse.X) / (float)bounds.Width, (e.Y - _lastMouse.Y) / (float)bounds.Height); _lastMouse = e.Location;
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _dragging = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ZoomChanged?.Invoke(e.Delta > 0 ? .1f : -.1f); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        { PanChanged?.Invoke(e.KeyCode == Keys.Left ? -.02f : e.KeyCode == Keys.Right ? .02f : 0, e.KeyCode == Keys.Up ? -.02f : e.KeyCode == Keys.Down ? .02f : 0); e.Handled = true; }
        else if (e.KeyCode is Keys.Add or Keys.Oemplus or Keys.Subtract or Keys.OemMinus) { ZoomChanged?.Invoke(e.KeyCode is Keys.Subtract or Keys.OemMinus ? -.1f : .1f); e.Handled = true; }
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
}
