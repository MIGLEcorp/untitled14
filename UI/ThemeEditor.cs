namespace MBrowser;

/// <summary>An in-window editor with live preview; closing without saving restores the original theme.</summary>
internal sealed class ThemeEditor : GlassPanel
{
    private readonly ThemeDocument _original = ThemeSettings.Document;
    private ThemeDocument _draft = ThemeSettings.Document;
    private readonly string _dataPath;
    private readonly Action _refresh;
    private readonly Action _close;
    private readonly ThemeCanvas _canvas = new();
    private static readonly ThemeArea[] Areas = [ThemeArea.Chrome, ThemeArea.Home, ThemeArea.Menu];
    private readonly TrackBar _zoom = new() { Minimum = 100, Maximum = 400, Width = 254, TickFrequency = 50, Height = 40 };
    private readonly Label _scale = new() { Width = 254, Height = 24, BackColor = Color.Transparent };
    private readonly ComboBox _area = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 254 };
    private readonly ComboBox _fit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 254 };
    private readonly TrackBar _tint = new() { Minimum = 0, Maximum = 255, Width = 254, TickFrequency = 32, Height = 45 };
    private readonly Label _file = new() { Width = 254, Height = 30, AutoEllipsis = true, BackColor = Color.Transparent };
    private readonly Label _amount = new() { Width = 254, Height = 24, BackColor = Color.Transparent };
    private readonly TextBox _name = new() { Width = 254, MaxLength = 80 };
    private bool _binding, _saved;

    internal ThemeEditor(string dataPath, Action refresh, Action close, Size? chromeSize = null)
    {
        UseArtwork = false;
        _dataPath = dataPath; _refresh = refresh; _close = close;
        _canvas.ChromeReferenceSize = chromeSize ?? new Size(1180,143);
        Size = new Size(1000, 690);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        layout.Controls.Add(_canvas, 0, 0);
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(8) };
        layout.Controls.Add(rows, 1, 0); Controls.Add(layout);
        void Label(string text, int height = 30, bool heading = false) => rows.Controls.Add(new Label
        { Text = text, Width = 254, Height = height, ForeColor = Palette.Text, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", heading ? 15 : 10, heading ? FontStyle.Bold : FontStyle.Regular) });
        void Button(string text, Action action)
        {
            var button = Palette.Button(text, text, 254);
            button.BackColor = Color.Transparent;
            button.Click += (_, _) => { try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { MessageBox.Show(this, ex.Message, "Тема MBrowser"); } };
            rows.Controls.Add(button);
        }
        Label("Редактор темы", 44, true);
        Label("Выберите область на макете. Перетаскивайте фон и меняйте масштаб колёсиком.", 64);
        _name.Text = _draft.Name; rows.Controls.Add(_name);
        _name.BackColor = Palette.Raised; _name.ForeColor = Palette.Text;
        _area.FlatStyle = _fit.FlatStyle = FlatStyle.Flat;
        _area.BackColor = _fit.BackColor = _tint.BackColor = Palette.Surface;
        _area.ForeColor = _fit.ForeColor = Palette.Text;
        _name.TextChanged += (_, _) => _draft = _draft with { Name = _name.Text };
        var presets = new FlowLayoutPanel { Width = 260, Height = 42, BackColor = Color.Transparent };
        foreach (var (title, theme) in new[] { ("Pink kawaii", BrowserTheme.Anime), ("Тёмная", BrowserTheme.Dark), ("Светлая", BrowserTheme.Light) })
        {
            var button = Palette.Button(title, title, 80);
            button.Font = new Font("Segoe UI",8.5f);
            button.BackColor = Color.Transparent;
            button.Click += (_, _) => { _draft = ThemeSettings.Preset(theme); _name.Text = _draft.Name; Preview(); BindLayer(); };
            presets.Controls.Add(button);
        }
        rows.Controls.Add(presets);
        Label("Фон панели", 35, true);
        _area.Items.AddRange(["Шапка целиком — одно изображение", "Главная страница", "Меню"]);
        _area.SelectedIndex = 0; rows.Controls.Add(_area);
        rows.Controls.Add(_file);
        Button("Выбрать PNG / JPG / GIF…", () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif", Title = "Фон выбранной панели" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            UpdateLayer(Layer with { File = ThemeMedia.Import(dialog.FileName, _dataPath) }); BindLayer();
        });
        Button("Убрать изображение с этой панели", () => { UpdateLayer(Layer with { File = "" }); BindLayer(); });
        _fit.Items.AddRange(["Заполнить с обрезкой", "Вписать целиком", "Повторять плиткой"]); rows.Controls.Add(_fit);
        rows.Controls.Add(_amount); rows.Controls.Add(_tint);
        _area.SelectedIndexChanged += (_, _) => { _canvas.SelectedArea = Area; BindLayer(); _canvas.Invalidate(); };
        _canvas.AreaSelected += area => { _area.SelectedIndex = Array.IndexOf(Areas, area); BindLayer(); };
        _canvas.PanChanged += (x,y) => UpdateLayer(Layer with { PanX = Math.Clamp(Layer.PanX + x,-1,1), PanY = Math.Clamp(Layer.PanY + y,-1,1) });
        _canvas.ZoomChanged += delta => { UpdateLayer(Layer with { Zoom = Math.Clamp(Layer.Zoom + delta,1,4) }); BindLayer(); };
        _canvas.ImageDropped += source =>
        {
            try { UpdateLayer(Layer with { File = ThemeMedia.Import(source,_dataPath) }); BindLayer(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { MessageBox.Show(this,ex.Message,"Тема MBrowser"); }
        };
        _fit.SelectedIndexChanged += (_, _) => { if (!_binding) UpdateLayer(Layer with { Fit = new[] { "cover", "contain", "tile" }[Math.Max(0, _fit.SelectedIndex)] }); };
        _tint.ValueChanged += (_, _) => { _amount.Text = $"Плотность цветного слоя: {_tint.Value * 100 / 255}%"; if (!_binding) UpdateLayer(Layer with { Tint = _tint.Value }); };
        _zoom.BackColor = Palette.Surface; rows.Controls.Add(_scale); rows.Controls.Add(_zoom);
        _zoom.ValueChanged += (_, _) => { _scale.Text = $"Масштаб: {_zoom.Value}%"; if (!_binding) UpdateLayer(Layer with { Zoom = _zoom.Value / 100f }); };
        Button("Сбросить положение и масштаб", () => { UpdateLayer(Layer with { Zoom = 1, PanX = 0, PanY = 0 }); BindLayer(); });
        Button("Экспорт PNG фона шапки…", () =>
        {
            using var dialog = new SaveFileDialog { Filter = "PNG|*.png", FileName = "MBrowser-header.png" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            Size exportSize = _canvas.ChromeReferenceSize;
            using var bitmap = new Bitmap(exportSize.Width,exportSize.Height); using var graphics = Graphics.FromImage(bitmap);
            ThemeMedia.Draw(graphics,new Rectangle(Point.Empty,bitmap.Size),ThemeArea.Chrome,_canvas);
            bitmap.Save(dialog.FileName,System.Drawing.Imaging.ImageFormat.Png);
        });
        Label("Цвета", 35, true);
        var swatches = new FlowLayoutPanel { Width = 260, Height = 108, BackColor = Color.Transparent };
        foreach (var (key, title) in new[] { ("Background", "Фон"), ("Surface", "Панели"), ("Raised", "Поля"), ("Text", "Текст"), ("Muted", "Подписи"), ("Accent", "Акцент") })
        {
            var swatch = new ColorSwatch(key,title) { Width = 78, Height = 48, AccessibleName = "Цвет: " + title, Margin = new Padding(2) };
            swatch.Click += (_, _) =>
            {
                var colors = ThemeSettings.Colors;
                using var dialog = new ColorDialog { FullOpen = true, Color = (Color)typeof(ThemeColors).GetProperty(key)!.GetValue(colors)! };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { _draft = _draft with { Colors = new(_draft.Colors) { [key] = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}" } }; Preview(); }
            };
            swatches.Controls.Add(swatch);
        }
        rows.Controls.Add(swatches);
        Button("Импортировать тему…", () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Тема MBrowser|*.mbtheme" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _draft = ThemePackage.Import(dialog.FileName, _dataPath); _name.Text = _draft.Name; Preview(); BindLayer(); }
        });
        Button("Экспортировать тему…", () =>
        {
            using var dialog = new SaveFileDialog { Filter = "Тема MBrowser|*.mbtheme", FileName = "My-theme.mbtheme" };
            if (dialog.ShowDialog(this) == DialogResult.OK) ThemePackage.Export(dialog.FileName, _draft);
        });
        Button("Сохранить тему", () => { ThemeSettings.Save(_draft, _dataPath); _saved = true; _refresh(); _close(); });
        Button("Отмена", close);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8), BackColor = Palette.Surface };
        foreach (var button in rows.Controls.Cast<Control>().TakeLast(2).ToArray())
        { rows.Controls.Remove(button); footer.Controls.Add(button); }
        Controls.Add(footer);
        BindLayer();
    }
    private ThemeArea Area => Areas[Math.Clamp(_area.SelectedIndex,0,Areas.Length-1)];
    internal void UpdateChromeReference(Size size) { _canvas.ChromeReferenceSize = size; _canvas.Invalidate(); }
    private ThemeLayer Layer => _draft.Layers.GetValueOrDefault(Area) ?? new();
    private void BindLayer()
    {
        _binding = true;
        _file.Text = string.IsNullOrEmpty(Layer.File) ? "Однотонный фон" : Layer.File.StartsWith("media-") ? "Собственное изображение · " + Path.GetExtension(Layer.File).ToUpperInvariant() : Layer.File;
        _fit.SelectedIndex = Layer.Fit == "tile" ? 2 : Layer.Fit == "contain" ? 1 : 0;
        _tint.Value = Layer.Tint; _amount.Text = $"Плотность цветного слоя: {_tint.Value * 100 / 255}%";
        _zoom.Value = Math.Clamp((int)(Layer.Zoom * 100),100,400); _scale.Text = $"Масштаб: {_zoom.Value}%";
        _binding = false;
    }
    private void UpdateLayer(ThemeLayer layer)
    { _draft = _draft with { Layers = new(_draft.Layers) { [Area] = layer } }; Preview(); }
    private void Preview() { ThemeSettings.Preview(_draft); _draft = ThemeSettings.Document; _refresh(); _canvas.Invalidate(); }
    private sealed class ColorSwatch(string key, string title) : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Palette.Surface);
            using var color = new SolidBrush((Color)typeof(ThemeColors).GetProperty(key)!.GetValue(ThemeSettings.Colors)!);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(color,Width/2-10,2,20,20);
            using var edge = new Pen(Palette.Muted); e.Graphics.DrawEllipse(edge,Width/2-10,2,20,20);
            TextRenderer.DrawText(e.Graphics,title,Font,new Rectangle(0,25,Width,22),Palette.Text,TextFormatFlags.HorizontalCenter);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_saved) { _saved = true; ThemeSettings.Preview(_original); _refresh(); }
        base.Dispose(disposing);
    }
}
