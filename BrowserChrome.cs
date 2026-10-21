using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Feather;

internal static class Palette
{
    public static readonly Color Background = Color.FromArgb(16, 22, 30);
    public static readonly Color Surface = Color.FromArgb(24, 33, 43);
    public static readonly Color Raised = Color.FromArgb(34, 46, 58);
    public static readonly Color Text = Color.FromArgb(235, 242, 245);
    public static readonly Color Muted = Color.FromArgb(151, 168, 181);
    public static readonly Color Accent = Color.FromArgb(111, 227, 181);

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
}

internal sealed class TabStrip : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyList<BrowserTab> Tabs { get; set; } = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int SelectedIndex { get; set; }
    public event Action<int>? TabSelected;
    public event Action<int>? TabClosed;
    public event Action? NewTab;
    private int _firstVisible;
    private readonly List<(int Index, Rectangle Bounds, Rectangle Close)> _hits = [];
    private Rectangle _plus, _previous, _next;
    private readonly ToolTip _tip = new();
    private int _hovered = -1;

    public TabStrip()
    {
        Height = 48;
        Dock = DockStyle.Top;
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        DoubleBuffered = true;
        AccessibleName = "Вкладки. Ctrl+Tab — следующая, Ctrl+W — закрыть";
    }

    public void RevealSelected()
    {
        int capacity = Math.Max(1, (Width - 124) / 160);
        if (SelectedIndex < _firstVisible) _firstVisible = SelectedIndex;
        if (SelectedIndex >= _firstVisible + capacity) _firstVisible = SelectedIndex - capacity + 1;
        _firstVisible = Math.Max(0, Math.Min(_firstVisible, Tabs.Count - 1));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _hits.Clear();
        int capacity = Math.Max(1, (Width - 124) / 160);
        int count = Math.Min(capacity, Tabs.Count - _firstVisible);
        int width = Math.Min(220, Math.Max(120, (Width - 124) / Math.Max(1, count)));
        for (int slot = 0; slot < count; slot++)
        {
            int index = _firstVisible + slot;
            var bounds = new Rectangle(10 + slot * width, 7, width - 5, 35);
            var close = new Rectangle(bounds.Right - 28, bounds.Top + 6, 22, 22);
            bool active = index == SelectedIndex;
            using var brush = new SolidBrush(active ? Palette.Raised : Palette.Surface);
            g.FillRectangle(brush, bounds);
            using var dot = new SolidBrush(active ? Palette.Accent : Palette.Muted);
            g.FillEllipse(dot, bounds.Left + 12, bounds.Top + 14, 6, 6);
            TextRenderer.DrawText(g, Tabs[index].Title, Font,
                new Rectangle(bounds.Left + 27, bounds.Top, bounds.Width - 57, bounds.Height),
                active ? Palette.Text : Palette.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "×", Font, close, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (active)
            {
                using var pen = new Pen(Palette.Accent, 2);
                g.DrawLine(pen, bounds.Left + 1, bounds.Bottom, bounds.Right - 1, bounds.Bottom);
            }
            _hits.Add((index, bounds, close));
        }
        int end = 10 + count * width;
        _plus = new Rectangle(end, 8, 34, 34);
        _previous = new Rectangle(end + 37, 8, 29, 34);
        _next = new Rectangle(end + 68, 8, 29, 34);
        TextRenderer.DrawText(g, "+", Font, _plus, Palette.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Tabs.Count > capacity)
        {
            TextRenderer.DrawText(g, "‹", Font, _previous, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "›", Font, _next, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        foreach (var hit in _hits)
        {
            if (!hit.Bounds.Contains(e.Location)) continue;
            if (e.Button == MouseButtons.Middle || hit.Close.Contains(e.Location)) TabClosed?.Invoke(hit.Index);
            else if (e.Button == MouseButtons.Left) TabSelected?.Invoke(hit.Index);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_plus.Contains(e.Location)) NewTab?.Invoke();
        else if (_previous.Contains(e.Location)) { _firstVisible = Math.Max(0, _firstVisible - 1); Invalidate(); }
        else if (_next.Contains(e.Location)) { _firstVisible = Math.Min(Math.Max(0, Tabs.Count - 1), _firstVisible + 1); Invalidate(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = _hits.FirstOrDefault(x => x.Bounds.Contains(e.Location));
        int index = hit.Bounds.IsEmpty ? -1 : hit.Index;
        if (index == _hovered) return;
        _hovered = index;
        _tip.SetToolTip(this, index < 0 ? "Новая вкладка: Ctrl+T" :
            $"{Tabs[index].Title}\n{Tabs[index].CurrentUrl ?? "Стартовая страница"}" + (index == SelectedIndex ? "" : "\nВыгружена из памяти; откроется заново"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class HomeSurface : Panel
{
    private readonly Panel _content;
    private readonly TextBox _search;
    public event Action<string>? Navigate;

    public HomeSurface()
    {
        Dock = DockStyle.Fill;
        BackColor = Palette.Background;
        _content = new Panel { Size = new Size(680, 440), BackColor = Palette.Background };
        Controls.Add(_content);
        AddLabel("F E A T H E R   /   Л Ё Г К И Й  Б Р А У З Е Р", 0, 0, 680, 25, 10, Palette.Accent);
        AddLabel("Меньше памяти.\nБольше воздуха.", 0, 44, 680, 120, 34, Palette.Text, FontStyle.Bold);
        AddLabel("Одна активная страница. Остальные вкладки отдыхают.", 2, 179, 680, 30, 13, Palette.Muted);

        var searchBox = new Panel { Location = new Point(0, 232), Size = new Size(680, 54), BackColor = Palette.Raised, Padding = new Padding(18, 14, 12, 12) };
        _search = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Palette.Raised, ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 14), PlaceholderText = "Введите адрес или поисковый запрос",
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

        var links = new FlowLayoutPanel { Location = new Point(0, 304), Size = new Size(680, 42), FlowDirection = FlowDirection.LeftToRight };
        foreach (var (name, url) in new[] { ("Wikipedia ↗", "https://ru.wikipedia.org"), ("GitHub ↗", "https://github.com"), ("Поиск ↗", "https://www.google.com") })
        {
            var link = Palette.Button(name, name, 142);
            link.Font = new Font("Segoe UI", 10);
            link.Margin = new Padding(0, 0, 10, 0);
            link.Click += (_, _) => Navigate?.Invoke(url);
            links.Controls.Add(link);
        }
        _content.Controls.Add(links);
        AddLabel("ЭКОНОМИЯ ПАМЯТИ ВКЛЮЧЕНА", 2, 374, 680, 23, 9, Palette.Accent, FontStyle.Bold);
        AddLabel("При смене вкладки страница выгружается. Видео останавливается,\nнесохранённый ввод сбрасывается. Адреса и история сохраняются.", 2, 399, 680, 43, 10, Palette.Muted);
        Resize += (_, _) => CenterContent();
    }

    private void AddLabel(string text, int x, int y, int width, int height, float fontSize, Color color, FontStyle style = FontStyle.Regular)
    {
        _content.Controls.Add(new Label
        {
            Text = text, Location = new Point(x, y), Size = new Size(width, height),
            Font = new Font("Segoe UI", fontSize, style), ForeColor = color, BackColor = Color.Transparent
        });
    }

    private void CenterContent()
    {
        _content.Location = new Point(Math.Max(24, (ClientSize.Width - _content.Width) / 2), Math.Max(24, (ClientSize.Height - _content.Height) / 2 - 15));
    }
}
