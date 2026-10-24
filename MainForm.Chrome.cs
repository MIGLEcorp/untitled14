namespace MBrowser;

internal sealed partial class MainForm
{
    private void BuildChrome()
    {
        var toolbar = new ThemeToolbar
        {
            Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 10, 10, 10),
            ColumnCount = 8, RowCount = 1, BackColor = Palette.Surface
        };
        for (int i = 0; i < 4; i++) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        _back.Click += (_, _) => RunUi(GoBackAsync);
        _forward.Click += (_, _) => RunUi(GoForwardAsync);
        _reload.Click += (_, _) => RunUi(ReloadAsync);
        var home = Palette.Button("⌂", "Стартовая страница (Alt+Home)");
        home.Click += (_, _) => RunUi(HomeAsync);
        toolbar.Controls.Add(_back, 0, 0);
        toolbar.Controls.Add(_forward, 1, 0);
        toolbar.Controls.Add(_reload, 2, 0);
        toolbar.Controls.Add(home, 3, 0);
        foreach (var button in new[] { _back, _forward, _reload, home }) _tips.SetToolTip(button, button.AccessibleName);

        var addressBox = new GlassPanel { UseArtwork = false, Dock = DockStyle.Fill, BackColor = Palette.Raised, Padding = new Padding(12, 7, 12, 6), Margin = new Padding(5, 0, 7, 0) };
        _address.Dock = DockStyle.Fill;
        _address.BorderStyle = BorderStyle.None;
        _address.BackColor = Palette.Raised;
        _address.ForeColor = Palette.Text;
        _address.Font = new Font("Segoe UI", 11);
        _address.PlaceholderText = "Адрес сайта или поиск в Google";
        _address.AccessibleName = "Адрес сайта (Ctrl+L)";
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            RunUi(() => NavigateInputAsync(_address.Text));
        };
        addressBox.Controls.Add(_address);
        toolbar.Controls.Add(addressBox, 4, 0);
        var go = Palette.Button("Найти", "Перейти по адресу или искать", 67);
        go.Font = new Font("Segoe UI", 10);
        go.Click += (_, _) => RunUi(() => NavigateInputAsync(_address.Text));
        var bookmark = Palette.Button("☆", "Закладки (Ctrl+D)");
        bookmark.Click += (_, _) => ShowBookmarks();
        toolbar.Controls.Add(bookmark, 5, 0);
        toolbar.Controls.Add(go, 6, 0);
        var menu = Palette.Button("☰", "Меню");
        menu.Click += (_, _) => ShowMainMenu();
        toolbar.Controls.Add(menu, 7, 0);
        foreach (Control control in toolbar.Controls) if (control is Button) control.BackColor = Color.Transparent;
        _viewport.Controls.Add(_home);
        Controls.Add(_viewport);
        Controls.Add(_bookmarkBar);
        Controls.Add(toolbar);
        Controls.Add(_tabs);
        toolbar.Tag = "fullscreen-visible";
        _bookmarkBar.Tag = "fullscreen-visible";
        InitializeBookmarksAndSuggestions();
    }
}
