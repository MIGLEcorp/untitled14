namespace MBrowser;

/// <summary>An in-window editor with live preview; closing without saving restores the original theme.</summary>
internal sealed class ThemeEditor : GlassPanel
{
    private readonly ThemeDocument _original = ThemeSettings.Document;
    private ThemeDocument _draft = ThemeSettings.Document;
    private readonly string _dataPath;
    private readonly Action _refresh;
    private readonly Action _close;
    private readonly ComboBox _area = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly ComboBox _fit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly TrackBar _tint = new() { Minimum = 0, Maximum = 255, Width = 430, TickFrequency = 32, Height = 45 };
    private readonly Label _file = new() { Width = 460, Height = 30, AutoEllipsis = true, BackColor = Color.Transparent };
    private readonly Label _amount = new() { Width = 460, Height = 24, BackColor = Color.Transparent };
    private readonly TextBox _name = new() { Width = 430, MaxLength = 80 };
    private bool _binding, _saved;

    internal ThemeEditor(string dataPath, Action refresh, Action close)
    {
        UseArtwork = false;
        _dataPath = dataPath; _refresh = refresh; _close = close;
        Size = new Size(570, 700);
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(8) };
        Controls.Add(rows);
        void Label(string text, int height = 30, bool heading = false) => rows.Controls.Add(new Label
        { Text = text, Width = 460, Height = height, ForeColor = Palette.Text, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", heading ? 15 : 10, heading ? FontStyle.Bold : FontStyle.Regular) });
        void Button(string text, Action action)
        {
            var button = Palette.Button(text, text, 460);
            button.BackColor = Color.Transparent;
            button.Click += (_, _) => { try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { MessageBox.Show(this, ex.Message, "Тема MBrowser"); } };
            rows.Controls.Add(button);
        }
        Label("Редактор темы", 44, true);
        Label("Изменения сразу видны в браузере. Закрытие отменяет их.", 42);
        _name.Text = _draft.Name; rows.Controls.Add(_name);
        _name.BackColor = Palette.Raised; _name.ForeColor = Palette.Text;
        _area.FlatStyle = _fit.FlatStyle = FlatStyle.Flat;
        _area.BackColor = _fit.BackColor = _tint.BackColor = Palette.Surface;
        _area.ForeColor = _fit.ForeColor = Palette.Text;
        _name.TextChanged += (_, _) => _draft = _draft with { Name = _name.Text };
        var presets = new FlowLayoutPanel { Width = 470, Height = 42, BackColor = Color.Transparent };
        foreach (var (title, theme) in new[] { ("Pink kawaii", BrowserTheme.Anime), ("Тёмная", BrowserTheme.Dark), ("Светлая", BrowserTheme.Light) })
        {
            var button = Palette.Button(title, title, 145);
            button.BackColor = Color.Transparent;
            button.Click += (_, _) => { _draft = ThemeSettings.Preset(theme); _name.Text = _draft.Name; Preview(); BindLayer(); };
            presets.Controls.Add(button);
        }
        rows.Controls.Add(presets);
        Label("Фон панели", 35, true);
        _area.Items.AddRange(["Главная", "Вкладки", "Адресная панель", "Закладки", "Меню и всплывающие панели"]);
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
        _area.SelectedIndexChanged += (_, _) => BindLayer();
        _fit.SelectedIndexChanged += (_, _) => { if (!_binding) UpdateLayer(Layer with { Fit = new[] { "cover", "contain", "tile" }[Math.Max(0, _fit.SelectedIndex)] }); };
        _tint.ValueChanged += (_, _) => { _amount.Text = $"Плотность цветного слоя: {_tint.Value * 100 / 255}%"; if (!_binding) UpdateLayer(Layer with { Tint = _tint.Value }); };
        Label("Цвета", 35, true);
        foreach (var (key, title) in new[] { ("Background", "Фон окна"), ("Surface", "Панели"), ("Raised", "Поля и активная вкладка"), ("Text", "Текст"), ("Muted", "Вторичный текст"), ("Accent", "Акцент") })
            Button(title + "…", () =>
            {
                var colors = ThemeSettings.Colors;
                using var dialog = new ColorDialog { FullOpen = true, Color = (Color)typeof(ThemeColors).GetProperty(key)!.GetValue(colors)! };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { _draft = _draft with { Colors = new(_draft.Colors) { [key] = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}" } }; Preview(); }
            });
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
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 86, Padding = new Padding(8), BackColor = Palette.Surface };
        foreach (var button in rows.Controls.Cast<Control>().TakeLast(2).ToArray())
        { rows.Controls.Remove(button); footer.Controls.Add(button); }
        Controls.Add(footer);
        BindLayer();
    }
    private ThemeArea Area => (ThemeArea)Math.Max(0, _area.SelectedIndex);
    private ThemeLayer Layer => _draft.Layers.GetValueOrDefault(Area) ?? new();
    private void BindLayer()
    {
        _binding = true;
        _file.Text = string.IsNullOrEmpty(Layer.File) ? "Однотонный фон" : Layer.File.StartsWith("media-") ? "Собственное изображение · " + Path.GetExtension(Layer.File).ToUpperInvariant() : Layer.File;
        _fit.SelectedIndex = Layer.Fit == "tile" ? 2 : Layer.Fit == "contain" ? 1 : 0;
        _tint.Value = Layer.Tint; _amount.Text = $"Плотность цветного слоя: {_tint.Value * 100 / 255}%";
        _binding = false;
    }
    private void UpdateLayer(ThemeLayer layer)
    { _draft = _draft with { Layers = new(_draft.Layers) { [Area] = layer } }; Preview(); }
    private void Preview() { ThemeSettings.Preview(_draft); _refresh(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_saved) { _saved = true; ThemeSettings.Preview(_original); _refresh(); }
        base.Dispose(disposing);
    }
}
