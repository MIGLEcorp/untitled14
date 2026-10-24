using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace MBrowser;

/// <summary>Native chrome and a start page that never needs to launch a web engine.</summary>
internal static class Palette
{
    public static Color Background => ThemeSettings.Colors.Background;
    public static Color Surface => ThemeSettings.Colors.Surface;
    public static Color Raised => ThemeSettings.Colors.Raised;
    public static Color Text => ThemeSettings.Colors.Text;
    public static Color Muted => ThemeSettings.Colors.Muted;
    public static Color Accent => ThemeSettings.Colors.Accent;

    public static Button Button(string text, string accessibleName, int width = 40)
    {
        var button = new RoundedButton
        {
            Text = text, AccessibleName = accessibleName, Width = width, Height = 34,
            FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Text,
            Font = new Font("Segoe UI", 11), Cursor = Cursors.Hand, TabStop = true,
            Margin = new Padding(3, 0, 3, 0), UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Raised;
        return button;
    }

    /// <summary>Preserves semantic color roles while changing the entire native control tree.</summary>
    public static void Apply(Control root)
    {
        if (root.GetType().Namespace?.StartsWith("Microsoft.Web.", StringComparison.Ordinal) == true) return;
        root.BackColor = ThemeSettings.Translate(root.BackColor);
        root.ForeColor = ThemeSettings.Translate(root.ForeColor);
        if (root is Button button)
        {
            button.FlatAppearance.MouseOverBackColor = Raised;
            button.FlatAppearance.MouseDownBackColor = Raised;
        }
        if (root is ToolStrip strip)
        {
            strip.BackColor = Surface;
            strip.ForeColor = Text;
            strip.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColors());
            foreach (ToolStripItem item in strip.Items) ApplyMenuItem(item);
        }
        if (root is HomeSurface home) home.RefreshTheme();
        foreach (Control child in root.Controls) Apply(child);
        root.Invalidate();
    }

    private static void ApplyMenuItem(ToolStripItem item)
    {
        item.BackColor = Surface;
        item.ForeColor = Text;
        if (item is not ToolStripDropDownItem parent) return;
        parent.DropDown.BackColor = Surface;
        parent.DropDown.ForeColor = Text;
        parent.DropDown.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColors());
        foreach (ToolStripItem child in parent.DropDownItems) ApplyMenuItem(child);
    }

    private sealed class ThemeMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Raised;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => Raised;
        public override Color MenuItemBorder => Raised;
        public override Color MenuItemSelectedGradientBegin => Raised;
        public override Color MenuItemSelectedGradientEnd => Raised;
        public override Color SeparatorDark => Raised;
        public override Color SeparatorLight => Surface;
    }
}

internal sealed class TabStrip : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyList<BrowserTab> Tabs { get; set; } = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Guid, bool>? IsTabLoaded { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<BrowserTab, Image?>? GetFavicon { get; set; }
    public event Action<int>? TabSelected;
    public event Action<int>? TabClosed;
    public event Action? NewTab;
    internal event Action<MouseEventArgs>? TitleAreaMouseDown;
    private int _firstVisible;
    private readonly List<(int Index, Rectangle Bounds, Rectangle Close)> _hits = [];
    private Rectangle _plus, _previous, _next;
    private readonly ToolTip _tip = new();
    private int _hovered = -2;

    public TabStrip()
    {
        Height = 48;
        Dock = DockStyle.Top;
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Вкладки. Ctrl+Tab — следующая, Ctrl+W — закрыть";
    }

    private int Capacity => Math.Max(1, (Width - 116) / 160);
    protected override void OnPaintBackground(PaintEventArgs e) => ThemeMedia.Draw(e.Graphics, ClientRectangle, ThemeArea.Tabs, this);
    private bool HasOverflow => Tabs.Count > Capacity;

    public void RevealSelected()
    {
        if (SelectedIndex < _firstVisible) _firstVisible = SelectedIndex;
        if (SelectedIndex >= _firstVisible + Capacity) _firstVisible = SelectedIndex - Capacity + 1;
        _firstVisible = Math.Clamp(_firstVisible, 0, Math.Max(0, Tabs.Count - 1));
        _hovered = -2;
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); RevealSelected(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _hits.Clear();
        int count = Math.Min(Capacity, Math.Max(0, Tabs.Count - _firstVisible));
        int width = Math.Min(220, Math.Max(100, (Width - 116) / Math.Max(1, count)));
        for (int slot = 0; slot < count; slot++)
        {
            int index = _firstVisible + slot;
            var bounds = new Rectangle(8 + slot * width, 7, width - 5, 35);
            var close = new Rectangle(bounds.Right - 28, bounds.Top + 6, 22, 22);
            bool active = index == SelectedIndex;
            using var shape = RoundedRectangle(bounds, 9);
            using var brush = new SolidBrush(active ? Palette.Raised : Palette.Surface);
            g.FillPath(brush, shape);
            bool anime = false;
            if (GetFavicon?.Invoke(Tabs[index]) is { } icon)
                g.DrawImage(icon, new Rectangle(bounds.Left + 9, bounds.Top + 9, 18, 18));
            else
            {
                using var dot = new SolidBrush(IsSleeping(index) ? Palette.Muted : Palette.Accent);
                g.FillEllipse(dot, bounds.Left + 13, bounds.Top + 14, 6, 6);
            }
            TextRenderer.DrawText(g, Tabs[index].Title, Font,
                new Rectangle(bounds.Left + (anime ? 40 : 27), bounds.Top, bounds.Width - (anime ? 70 : 57), bounds.Height),
                active ? Palette.Text : Palette.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var closePen = new Pen(Palette.Muted, 1.3f))
            {
                g.DrawLine(closePen, close.Left + 8, close.Top + 8, close.Left + 14, close.Top + 14);
                g.DrawLine(closePen, close.Left + 14, close.Top + 8, close.Left + 8, close.Top + 14);
            }
            if (active)
            {
                using var pen = new Pen(Palette.Accent, 2);
                g.DrawLine(pen, bounds.Left + 10, bounds.Bottom, bounds.Right - 10, bounds.Bottom);
            }
            _hits.Add((index, bounds, close));
        }
        int end = 8 + count * width;
        _plus = new Rectangle(end, 8, 34, 34);
        _previous = HasOverflow ? new Rectangle(end + 37, 8, 29, 34) : Rectangle.Empty;
        _next = HasOverflow ? new Rectangle(end + 68, 8, 29, 34) : Rectangle.Empty;
        TextRenderer.DrawText(g, "+", Font, _plus, Palette.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (HasOverflow)
        {
            TextRenderer.DrawText(g, "‹", Font, _previous, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "›", Font, _next, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private bool IsSleeping(int index) => Tabs[index].CurrentUrl is not null and not "about:blank" &&
        IsTabLoaded is not null && !IsTabLoaded(Tabs[index].Id);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        foreach (var hit in _hits)
        {
            if (!hit.Bounds.Contains(e.Location)) continue;
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Left && hit.Close.Contains(e.Location)) TabClosed?.Invoke(hit.Index);
            else if (e.Button == MouseButtons.Left) TabSelected?.Invoke(hit.Index);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_plus.Contains(e.Location)) NewTab?.Invoke();
        else if (_previous.Contains(e.Location)) { _firstVisible = Math.Max(0, _firstVisible - 1); Invalidate(); }
        else if (_next.Contains(e.Location)) { _firstVisible = Math.Min(Math.Max(0, Tabs.Count - Capacity), _firstVisible + 1); Invalidate(); }
        else TitleAreaMouseDown?.Invoke(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = _hits.FirstOrDefault(x => x.Bounds.Contains(e.Location));
        int index = hit.Bounds.IsEmpty ? -1 : hit.Index;
        int hoverKey = index >= 0 ? index : _plus.Contains(e.Location) ? -3 : _previous.Contains(e.Location) ? -4 : _next.Contains(e.Location) ? -5 : -1;
        if (hoverKey == _hovered) return;
        _hovered = hoverKey;
        string hint = _plus.Contains(e.Location) ? "Новая вкладка: Ctrl+T" : "Перетащите, чтобы переместить окно; двойной щелчок — развернуть";
        if (_previous.Contains(e.Location)) hint = "Предыдущие вкладки";
        if (_next.Contains(e.Location)) hint = "Следующие вкладки";
        if (index >= 0 && index < Tabs.Count)
        {
            string state = IsSleeping(index) ? "Спящая вкладка" :
                index == SelectedIndex ? "Активная вкладка" : "В памяти; может быть заморожена, когда браузер не используется";
            hint = $"{Tabs[index].Title}\n{Tabs[index].CurrentUrl ?? "Стартовая страница"}\n{state}";
        }
        _tip.SetToolTip(this, hint);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Tabs.Count == 0) return;
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End)
        {
            int next = e.KeyCode switch
            {
                Keys.Left => (SelectedIndex + Tabs.Count - 1) % Tabs.Count,
                Keys.Right => (SelectedIndex + 1) % Tabs.Count,
                Keys.Home => 0,
                _ => Tabs.Count - 1
            };
            TabSelected?.Invoke(next);
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override AccessibleObject CreateAccessibilityInstance() => new TabsAccessibility(this);

    private sealed class TabsAccessibility(TabStrip owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.Tabs.Count + 1;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index <= owner.Tabs.Count ? new TabAccessibility(owner, index) : null;
    }

    private sealed class TabAccessibility(TabStrip owner, int index) : AccessibleObject
    {
        private bool IsNew => index == owner.Tabs.Count;
        public override string? Name { get => IsNew ? "Новая вкладка" : index < owner.Tabs.Count ? owner.Tabs[index].Title : "Закрытая вкладка"; set { } }
        public override AccessibleRole Role => IsNew ? AccessibleRole.PushButton : AccessibleRole.PageTab;
        public override AccessibleObject? Parent => owner.AccessibilityObject;
        public override string? DefaultAction => IsNew ? "Открыть новую вкладку" : "Выбрать вкладку";
        public override Rectangle Bounds
        {
            get
            {
                Rectangle rectangle = IsNew ? owner._plus : owner._hits.FirstOrDefault(x => x.Index == index).Bounds;
                return rectangle.IsEmpty ? Rectangle.Empty : owner.RectangleToScreen(rectangle);
            }
        }
        public override AccessibleStates State => AccessibleStates.Focusable | AccessibleStates.Selectable |
            (!IsNew && index == owner.SelectedIndex ? AccessibleStates.Selected : AccessibleStates.None) |
            (Bounds.IsEmpty ? AccessibleStates.Offscreen : AccessibleStates.None);
        public override void DoDefaultAction()
        {
            if (IsNew) owner.NewTab?.Invoke();
            else if (index < owner.Tabs.Count) owner.TabSelected?.Invoke(index);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _tip.Dispose(); base.Dispose(disposing); }

    internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class HomeSurface : Panel
{
    private readonly GlassPanel _searchBox = new() { UseArtwork = false, Size = new Size(620, 62), Padding = new Padding(22, 20, 20, 16) };
    internal TextBox SearchBox { get; } = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        BackColor = Palette.Surface, ForeColor = Palette.Text, Font = new Font("Segoe UI", 13), PlaceholderText = "Поиск или адрес сайта" };
    public event Action<string>? Navigate;
    public HomeSurface()
    {
        Dock = DockStyle.Fill;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _searchBox.Controls.Add(SearchBox);
        Controls.Add(_searchBox);
        SearchBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate?.Invoke(SearchBox.Text); } };
        Resize += (_, _) => CenterSearch();
    }
    private void CenterSearch()
    {
        _searchBox.Width = Math.Min(620, Math.Max(280, Width - 64));
        _searchBox.Location = new Point((Width - _searchBox.Width) / 2, Math.Max(32, Height / 3 - 30));
        // Moving a child HWND and resizing the cover crop both invalidate old pixels.
        // A full repaint prevents Win32's copied client bits from surviving a resize.
        Invalidate(true);
    }
    internal void RefreshTheme() { CenterSearch(); Invalidate(true); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        ThemeMedia.Draw(e.Graphics, ClientRectangle, ThemeArea.Home, this);
    }
}
