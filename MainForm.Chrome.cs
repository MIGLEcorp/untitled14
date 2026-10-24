namespace Feather;

internal sealed partial class MainForm
{
    private void BuildChrome()
    {
        var toolbar = new TableLayoutPanel
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

        var addressBox = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Raised, Padding = new Padding(12, 7, 12, 6), Margin = new Padding(5, 0, 7, 0) };
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
        menu.Click += (_, _) => _menu.Show(menu, new Point(0, menu.Height));
        toolbar.Controls.Add(menu, 7, 0);
        var animeBanner = new ToolStripControlHost(new AnimeMenuBanner()) { AutoSize = false, Size = new Size(365, 100), Margin = Padding.Empty, Padding = Padding.Empty };
        _menu.Items.Add(animeBanner);
        _menu.Opening += (_, _) =>
        {
            DismissPasswordPopup();
            animeBanner.Visible = ThemeSettings.Current == BrowserTheme.Anime;
            Palette.Apply(_menu);
        };
        _menu.Items.Add("Новая вкладка                         Ctrl+T", null, (_, _) => RunUi(() => OpenTabAsync()));
        _menu.Items.Add("Выгрузить страницу / на главную", null, (_, _) => RunUi(HomeAsync));
        _menu.Items.Add("Загрузки                                        Ctrl+J", null, (_, _) => ShowDownloads());
        _menu.Items.Add("Полный экран                              F11", null, (_, _) => ToggleFullScreen());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Закладки                                    Ctrl+D", null, (_, _) => ShowBookmarks());
        _menu.Items.Add("Пароли                                Ctrl+Shift+P", null, (_, _) => ShowPasswords());
        _menu.Items.Add("Подсказки паролей", null, (_, _) => RunUi(FillPasswordAsync));
        _menu.Items.Add("Импорт из другого браузера…", null, (_, _) => ShowImport());
        var themes = new ToolStripMenuItem("Тема оформления");
        foreach (var (name, theme) in new[] { ("Сакура / аниме", BrowserTheme.Anime), ("Тёмная", BrowserTheme.Dark), ("Светлая", BrowserTheme.Light) })
        {
            var item = new ToolStripMenuItem(name) { Tag = theme };
            item.Click += (_, _) => { try { SelectTheme(theme); } catch (Exception ex) { ShowError(ex); } };
            themes.DropDownItems.Add(item);
        }
        themes.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in themes.DropDownItems) item.Checked = (BrowserTheme)item.Tag! == ThemeSettings.Current;
        };
        _menu.Items.Add(themes);
        var idle = new ToolStripMenuItem("Выгружать фоновые вкладки через");
        foreach (int minutes in new[] { 1, 5, 15, 30 })
        {
            var item = new ToolStripMenuItem($"{minutes} мин.") { Tag = minutes };
            item.Click += (_, _) => SetUnloadTimeout(minutes);
            idle.DropDownItems.Add(item);
        }
        idle.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in idle.DropDownItems) item.Checked = (int)item.Tag! == _preferences.UnloadAfterMinutes;
        };
        _menu.Items.Add(idle);
        _menu.Items.Add("Освободить память фоновых вкладок", null, (_, _) => RunUi(UnloadBackgroundTabsAsync));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Открыть в WebKit — эксперимент", null, (_, _) => RunUi(() =>
            Engine.WebKitPreviewLauncher.LaunchAsync(this, ActiveTab.CurrentUrl, DataPath)));
        _menu.Items.Add("О браузере и экономии памяти", null, (_, _) => MessageBox.Show(this,
            "Feather 2.0\n\nФоновые страницы сохраняют состояние при переключении и выгружаются после простоя (по умолчанию 5 минут). При нехватке мест выгружаются самые старые: по умолчанию до 8 обычных страниц в памяти. Музыка, скачивания и изменённые формы защищены. После выгрузки страница загружается заново; сохраняются адрес, история и прокрутка, но не DOM/JS.\n\nWebView2 запускается при первом сайте. Библиотека данных загружается отдельным модулем по запросу. WebKit запускается отдельным экспериментальным окном с собственным профилем.\n\nПароли WebView2 сохраняются с запросом. Импортированные пароли защищены Windows DPAPI. Одна учётная запись заполняется автоматически; несколько предлагаются во всплывающей подсказке у поля входа.\n\nF11 — полный экран. Расход памяти зависит от сайта.", "О Feather", MessageBoxButtons.OK, MessageBoxIcon.Information));

        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 34, BackColor = Palette.Surface, ColumnCount = 3, Padding = new Padding(9, 0, 14, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390));
        _status.Dock = _memory.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _memory.TextAlign = ContentAlignment.MiddleRight;
        _status.ForeColor = Palette.Muted;
        _memory.ForeColor = Palette.Accent;
        _status.Font = _memory.Font = new Font("Segoe UI", 9);
        _status.AutoEllipsis = true;
        footer.Controls.Add(new AnimeBadge { Character = 1, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 0);
        footer.Controls.Add(_status, 1, 0);
        footer.Controls.Add(_memory, 2, 0);
        _viewport.Controls.Add(_home);
        Controls.Add(_viewport);
        Controls.Add(footer);
        Controls.Add(toolbar);
        Controls.Add(_tabs);
    }

}


