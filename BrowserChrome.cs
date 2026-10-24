using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Feather;

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
        var button = new Button
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
        DoubleBuffered = true;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "Вкладки. Ctrl+Tab — следующая, Ctrl+W — закрыть";
    }

    private int Capacity => Math.Max(1, (Width - 116) / 160);
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
            bool anime = ThemeSettings.Current == BrowserTheme.Anime;
            if (anime)
            {
                AnimeArtwork.DrawPortrait(g, new Rectangle(bounds.Left + 7, bounds.Top + 5, 25, 25), index);
                if (IsSleeping(index))
                    TextRenderer.DrawText(g, "z", Font, new Rectangle(bounds.Left + 24, bounds.Top + 17, 12, 16), Palette.Text);
            }
            else if (IsSleeping(index))
            {
                using var outline = new Pen(Palette.Muted, 1.3f);
                g.DrawEllipse(outline, bounds.Left + 12, bounds.Top + 14, 6, 6);
            }
            else
            {
                using var dot = new SolidBrush(active ? Palette.Accent : Palette.Muted);
                g.FillEllipse(dot, bounds.Left + 12, bounds.Top + 14, 6, 6);
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
            string state = IsSleeping(index) ? "Выгружена из памяти; при открытии страница загрузится заново" :
                index == SelectedIndex ? "Активная вкладка" : "В памяти; выгрузится после 5 минут без использования";
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
    private readonly Panel _content;
    private readonly TextBox _search;
    private readonly Label _themeLabel;
    public event Action<string>? Navigate;

    public HomeSurface()
    {
        Dock = DockStyle.Fill;
        BackColor = Palette.Background;
        DoubleBuffered = true;
        _content = new TransparentPanel { Size = new Size(540, 438), BackColor = Color.Transparent };
        Controls.Add(_content);
        _themeLabel = AddLabel("F E A T H E R  /  S A K U R A", 0, 0, 680, 25, 10, Palette.Accent, FontStyle.Bold);
        AddLabel("Твой уголок\nбольшого интернета.", 0, 42, 680, 128, 32, Palette.Text, FontStyle.Bold);
        AddLabel("Твои вкладки, любимые сайты и немного магии.", 2, 183, 540, 29, 12, Palette.Muted);

        var searchBox = new Panel { Location = new Point(0, 232), Size = new Size(540, 54), BackColor = Palette.Raised, Padding = new Padding(18, 14, 12, 12) };
        _search = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Palette.Raised, ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 14), PlaceholderText = "Адрес сайта или поисковый запрос",
            AccessibleName = "Адрес или запрос", Dock = DockStyle.Fill
        };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate?.Invoke(_search.Text); } };
        var go = Palette.Button("→", "Открыть", 40);
        go.BackColor = Palette.Raised;
        go.Dock = DockStyle.Right;
        go.Click += (_, _) => Navigate?.Invoke(_search.Text);
        searchBox.Controls.Add(_search);
        searchBox.Controls.Add(go);
        _content.Controls.Add(searchBox);

        var links = new FlowLayoutPanel { Location = new Point(0, 304), Size = new Size(680, 42), FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent };
        foreach (var (name, url) in new[] { ("Wikipedia ↗", "https://ru.wikipedia.org"), ("GitHub ↗", "https://github.com"), ("Поиск ↗", "https://www.google.com") })
        {
            var link = Palette.Button(name, name, 142);
            link.Font = new Font("Segoe UI", 10);
            link.Margin = new Padding(0, 0, 10, 0);
            link.Click += (_, _) => Navigate?.Invoke(url);
            links.Controls.Add(link);
        }
        _content.Controls.Add(links);
        AddLabel("ВКЛАДКИ ОТДЫХАЮТ ЧЕРЕЗ 5 МИНУТ", 2, 374, 680, 23, 9, Palette.Accent, FontStyle.Bold);
        AddLabel("Фоновые вкладки засыпают через 5 минут.\nМузыка, загрузки и изменённые формы остаются в памяти.", 2, 400, 540, 38, 9, Palette.Muted);
        Resize += (_, _) => CenterContent();
        VisibleChanged += (_, _) => { if (!Visible) AnimeArtwork.ReleaseBackdrop(); };
    }

    internal void RefreshTheme()
    {
        _themeLabel.Text = ThemeSettings.Current switch
        {
            BrowserTheme.Anime => "F E A T H E R  /  S A K U R A",
            BrowserTheme.Light => "F E A T H E R  /  D A Y L I G H T",
            _ => "F E A T H E R  /  M I D N I G H T"
        };
        if (ThemeSettings.Current != BrowserTheme.Anime) AnimeArtwork.ReleaseBackdrop();
        CenterContent();
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var colors = ThemeSettings.Colors;
        using var sky = new LinearGradientBrush(ClientRectangle, colors.Background, colors.Sky, 24f);
        g.FillRectangle(sky, ClientRectangle);
        if (ThemeSettings.Current != BrowserTheme.Anime) return;
        if (AnimeArtwork.DrawBackdrop(g, ClientRectangle)) return;
        float width = ClientSize.Width, height = ClientSize.Height;
        float moonX = width * .82f, moonY = height * .19f;
        using (var halo = new SolidBrush(Color.FromArgb(16, colors.Accent))) g.FillEllipse(halo, moonX - 114, moonY - 114, 228, 228);
        using (var moon = new SolidBrush(Color.FromArgb(90, colors.Accent))) g.FillEllipse(moon, moonX - 70, moonY - 70, 140, 140);
        using (var shade = new SolidBrush(colors.Sky)) g.FillEllipse(shade, moonX - 89, moonY - 86, 139, 139);
        DrawMountains(g, width, height, colors);
        DrawSakura(g, width, height, colors);
    }

    private static void DrawMountains(Graphics g, float width, float height, ThemeColors colors)
    {
        using var far = new SolidBrush(Color.FromArgb(42, colors.Accent));
        g.FillPolygon(far, new PointF[] { new(0, height), new(0, height * .88f), new(width * .24f, height * .64f), new(width * .43f, height * .85f), new(width * .72f, height * .45f), new(width, height * .8f), new(width, height) });
        using var snow = new SolidBrush(Color.FromArgb(24, colors.Text));
        g.FillPolygon(snow, new PointF[] { new(width * .72f, height * .45f), new(width * .66f, height * .54f), new(width * .70f, height * .525f), new(width * .73f, height * .55f), new(width * .76f, height * .51f) });
        using var near = new SolidBrush(Color.FromArgb(165, colors.Background));
        g.FillPolygon(near, new PointF[] { new(0, height), new(0, height * .95f), new(width * .2f, height * .83f), new(width * .4f, height * .96f), new(width * .67f, height * .8f), new(width, height * .93f), new(width, height) });
    }

    private static void DrawSakura(Graphics g, float width, float height, ThemeColors colors)
    {
        using var branch = new Pen(Color.FromArgb(75, colors.Accent), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawBezier(branch, width + 12, height * .05f, width * .94f, height * .12f, width * .9f, height * .2f, width * .76f, height * .1f);
        g.DrawBezier(branch, width * .91f, height * .13f, width * .91f, height * .24f, width * .84f, height * .29f, width * .82f, height * .3f);
        using var petals = new SolidBrush(Color.FromArgb(128, colors.Accent));
        using var centers = new SolidBrush(Color.FromArgb(180, colors.Text));
        foreach (var point in new (float X, float Y)[] { (.96f, .09f), (.92f, .15f), (.87f, .15f), (.83f, .13f), (.79f, .115f), (.90f, .22f), (.86f, .26f), (.82f, .30f) })
        {
            float x = width * point.X, y = height * point.Y;
            for (int i = 0; i < 5; i++)
            {
                double angle = i * Math.PI * 2 / 5;
                g.FillEllipse(petals, x + (float)Math.Cos(angle) * 5 - 4, y + (float)Math.Sin(angle) * 5 - 4, 8, 8);
            }
            g.FillEllipse(centers, x - 2, y - 2, 4, 4);
        }
        foreach (var point in new (float X, float Y)[] { (.78f, .39f), (.94f, .50f), (.86f, .63f), (.14f, .16f), (.18f, .86f), (.71f, .77f) })
            g.FillEllipse(petals, width * point.X, height * point.Y, 5, 9);
    }

    private Label AddLabel(string text, int x, int y, int width, int height, float fontSize, Color color, FontStyle style = FontStyle.Regular)
    {
        var label = new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), Font = new Font("Segoe UI", fontSize, style), ForeColor = color, BackColor = Color.Transparent };
        _content.Controls.Add(label);
        return label;
    }

    private void CenterContent() => _content.Location = new Point(
        ThemeSettings.Current == BrowserTheme.Anime ? Math.Max(28, ClientSize.Width / 22) : Math.Max(24, (ClientSize.Width - _content.Width) / 2),
        Math.Max(16, (ClientSize.Height - _content.Height) / 2 - 10));
    private sealed class TransparentPanel : Panel { public TransparentPanel() => DoubleBuffered = true; }
}
