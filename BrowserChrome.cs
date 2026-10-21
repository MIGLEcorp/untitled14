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
        AccessibleName = "التبويبات. Ctrl+Tab — التالي، Ctrl+W — إغلاق";
    }

    public void RevealSelected()
    {
        int capacity = Math.Max(1, (Width - 124) / 160);
        if (SelectedIndex < _firstVisible) _firstVisible = SelectedIndex;
        if (SelectedIndex >= _firstVisible + capacity) _firstVisible = SelectedIndex - capacity + 1;
        _firstVisible = Math.Max(0, Math.Min(_firstVisible, Tabs.Count - 1));
        Invalidate();
    }

    /// <summary>Mirrors an x coordinate so the strip runs right to left like the rest of the Arabic chrome.</summary>
    private int Mirror(int x, int width) => Width - x - width;

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
            var bounds = new Rectangle(Mirror(10 + slot * width, width - 5), 7, width - 5, 35);
            var close = new Rectangle(bounds.Left + 6, bounds.Top + 6, 22, 22);
            bool active = index == SelectedIndex;
            using var brush = new SolidBrush(active ? Palette.Raised : Palette.Surface);
            g.FillRectangle(brush, bounds);
            using var dot = new SolidBrush(active ? Palette.Accent : Palette.Muted);
            g.FillEllipse(dot, bounds.Right - 18, bounds.Top + 14, 6, 6);
            TextRenderer.DrawText(g, Tabs[index].Title, Font,
                new Rectangle(bounds.Left + 30, bounds.Top, bounds.Width - 57, bounds.Height),
                active ? Palette.Text : Palette.Muted,
                TextFormatFlags.RightToLeft | TextFormatFlags.HorizontalCenter |
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
        _plus = new Rectangle(Mirror(end, 34), 8, 34, 34);
        _previous = new Rectangle(Mirror(end + 37, 29), 8, 29, 34);
        _next = new Rectangle(Mirror(end + 68, 29), 8, 29, 34);
        TextRenderer.DrawText(g, "+", Font, _plus, Palette.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Tabs.Count > capacity)
        {
            TextRenderer.DrawText(g, "›", Font, _previous, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "‹", Font, _next, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
        _tip.SetToolTip(this, index < 0 ? "تبويب جديد: Ctrl+T" :
            $"{Tabs[index].Title}\n{Tabs[index].CurrentUrl ?? "الصفحة الرئيسية"}" + (index == SelectedIndex ? "" : "\nمُفرَغ من الذاكرة؛ يُفتح من جديد"));
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

    /// <summary>Raised by the search box: typed text passes the Arabic-only language gate first.</summary>
    public event Action<string>? Submit;

    /// <summary>Raised by the shortcut links: a click opens the address without typing it.</summary>
    public event Action<string>? OpenLink;

    /// <summary>The start page search box; typed text is checked for foreign scripts by the main window.</summary>
    internal TextBox InputBox => _search;

    public HomeSurface()
    {
        Dock = DockStyle.Fill;
        BackColor = Palette.Background;
        RightToLeft = RightToLeft.Yes;
        _content = new Panel { Size = new Size(680, 440), BackColor = Palette.Background, RightToLeft = RightToLeft.Yes };
        Controls.Add(_content);
        AddLabel("F E A T H E R   /   م ت ص ف ن   خ ف ي ف", 0, 0, 680, 25, 10, Palette.Accent, FontStyle.Regular, ContentAlignment.MiddleRight);
        AddLabel("ذاكرة أقل.\nمساحة أكثر.", 0, 44, 680, 120, 34, Palette.Text, FontStyle.Bold, ContentAlignment.MiddleRight);
        AddLabel("صفحة نشطة واحدة فقط، وبقية التبويبات ساكنة.", 2, 179, 680, 30, 13, Palette.Muted, FontStyle.Regular, ContentAlignment.MiddleRight);

        var searchBox = new Panel { Location = new Point(0, 232), Size = new Size(680, 54), BackColor = Palette.Raised, Padding = new Padding(12, 14, 18, 12) };
        _search = new TextBox
        {
            BorderStyle = BorderStyle.None, BackColor = Palette.Raised, ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 14), PlaceholderText = "أدخل عنوانًا أو عبارة بحث",
            AccessibleName = "عنوان أو عبارة بحث", Dock = DockStyle.Fill, RightToLeft = RightToLeft.Yes
        };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit?.Invoke(_search.Text); } };
        var go = Palette.Button("←", "فتح", 40);
        go.BackColor = Palette.Raised;
        go.Dock = DockStyle.Left;
        go.Click += (_, _) => Submit?.Invoke(_search.Text);
        searchBox.Controls.Add(_search);
        searchBox.Controls.Add(go);
        _content.Controls.Add(searchBox);

        var links = new FlowLayoutPanel
        {
            Location = new Point(0, 304), Size = new Size(680, 42),
            FlowDirection = FlowDirection.LeftToRight, RightToLeft = RightToLeft.Yes
        };
        foreach (var (name, url) in new[] { ("ويكيبيديا ↗", "https://ar.wikipedia.org"), ("جيت هب ↗", "https://github.com"), ("بحث ↗", "https://www.google.com") })
        {
            var link = Palette.Button(name, name, 142);
            link.Font = new Font("Segoe UI", 10);
            link.Margin = new Padding(0, 0, 10, 0);
            link.Click += (_, _) => OpenLink?.Invoke(url);
            links.Controls.Add(link);
        }
        _content.Controls.Add(links);
        AddLabel("ترشيد الذاكرة مُفعّل", 2, 374, 680, 23, 9, Palette.Accent, FontStyle.Bold, ContentAlignment.MiddleRight);
        AddLabel("عند التبديل بين التبويبات تُفرَغ الصفحة، يتوقف الفيديو،\nويُفقد الإدخال غير المحفوظ. تُحفظ العناوين والسجل.", 2, 399, 680, 43, 10, Palette.Muted, FontStyle.Regular, ContentAlignment.MiddleRight);
        Resize += (_, _) => CenterContent();
    }

    private void AddLabel(string text, int x, int y, int width, int height, float fontSize, Color color, FontStyle style = FontStyle.Regular, ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        // RightToLeft.Yes mirrors horizontal alignment, so a label that must sit on the visual
        // right edge asks for the left one; call sites keep the visual meaning.
        if (align is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight)
            align = align switch
            {
                ContentAlignment.TopRight => ContentAlignment.TopLeft,
                ContentAlignment.MiddleRight => ContentAlignment.MiddleLeft,
                _ => ContentAlignment.BottomLeft
            };
        _content.Controls.Add(new Label
        {
            Text = text, Location = new Point(x, y), Size = new Size(width, height),
            Font = new Font("Segoe UI", fontSize, style), ForeColor = color, BackColor = Color.Transparent,
            TextAlign = align, RightToLeft = RightToLeft.Yes
        });
    }

    private void CenterContent()
    {
        _content.Location = new Point(Math.Max(24, (ClientSize.Width - _content.Width) / 2), Math.Max(24, (ClientSize.Height - _content.Height) / 2 - 15));
    }
}
