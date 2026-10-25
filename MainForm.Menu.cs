namespace MBrowser;

internal sealed partial class MainForm
{
    private Control? _overlay;
    internal bool IsInternalMenuVisible => _menu is { IsDisposed: false, Visible: true };
    internal Rectangle InternalMenuBounds => _menu?.Bounds ?? Rectangle.Empty;
    internal Control? InternalMenuControlForTest => _menu;

    internal void CloseOverlay()
    {
        var overlay = _overlay;
        _overlay = null;
        _menu = null;
        if (overlay is null) return;
        _viewport.Controls.Remove(overlay);
        overlay.Dispose();
        if (!_home.Visible) ThemeMedia.Release(ThemeArea.Home);
    }

    private void ShowOverlay(Control panel, Point location)
    {
        CloseOverlay();
        panel.Size = new Size(Math.Min(panel.Width, Math.Max(240, _viewport.Width - 24)), Math.Min(panel.Height, Math.Max(90, _viewport.Height - 24)));
        panel.Location = new Point(Math.Clamp(location.X, 10, Math.Max(10, _viewport.Width - panel.Width - 10)),
            Math.Clamp(location.Y, 10, Math.Max(10, _viewport.Height - panel.Height - 10)));
        _overlay = panel;
        _viewport.Controls.Add(panel);
        panel.BringToFront();
        UiMotion.Reveal(panel);
    }

    internal void ShowMainMenu()
    {
        if (IsInternalMenuVisible) { CloseOverlay(); return; }
        ShowMenuPage("MBrowser", rows =>
        {
            rows("Новая вкладка", () => RunUi(() => OpenTabAsync()));
            rows("Открыть файл…", OpenDocument);
            rows(IsFullScreen ? "Выйти из полного экрана" : "Полный экран", ToggleFullScreen);
            rows("Закладки", ShowBookmarks);
            rows("Пароли", ShowPasswords);
            rows("Импорт данных…", ShowImport);
            rows("Загрузки", ShowDownloads);
            rows("Оформление  ›", ShowThemeMenu);
            rows("Браузер по умолчанию…", BrowserRegistration.OpenSettings);
            rows("Подсказки DuckDuckGo: " + (_preferences.NetworkSuggestions ? "вкл." : "выкл."), () =>
            { _preferences.NetworkSuggestions = !_preferences.NetworkSuggestions; _preferences.Save(DataPath); });
            rows("Заморозить фоновые вкладки", () => RunUi(UnloadBackgroundTabsAsync));
            rows("Разрешения сайтов  ›", ShowPermissionSettings);
            rows("Расширения  ›", () => RunUi(ShowExtensionsAsync));
            rows("Переводчик  ›", ShowTranslatorMenu);
            rows("Открыть в WebKit", () => RunUi(() => Engine.WebKitPreviewLauncher.LaunchAsync(this, ActiveTab.CurrentUrl, DataPath)));
            rows("Выход", Close);
        });
    }

    private void ShowMenuPage(string title, Action<Action<string, Action>> populate)
    {
        DismissPasswordPopup();
        var panel = new GlassPanel { Size = new Size(352, 580) };
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Color.Transparent };
        var heading = new Label { Text = title, Width = 300, Height = 38, BackColor = Color.Transparent,
            ForeColor = Palette.Text, Font = new Font("Segoe UI", 15, FontStyle.Bold) };
        content.Controls.Add(heading);
        populate((text, action) =>
        {
            var button = Palette.Button(text, text, 300);
            button.Height = 36;
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.BackColor = Color.Transparent;
            button.Font = new Font("Segoe UI", 10);
            button.Margin = new Padding(0, 1, 0, 1);
            button.Click += (_, _) => { CloseOverlay(); action(); };
            content.Controls.Add(button);
        });
        panel.Controls.Add(content);
        ShowOverlay(panel, new Point(_viewport.Width - panel.Width - 12, 10));
        _menu = panel;
    }

    internal void ShowThemeMenu()
    {
        CloseOverlay();
        var editor = new ThemeEditor(DataPath, ApplyCurrentTheme, CloseOverlay,
            new Size(_chromeHost.Width,TitleBarHeight + _toolbar.Height + _bookmarkBar.Height));
        ShowOverlay(editor, new Point(_viewport.Width - editor.Width - 12, 10));
        _menu = editor;
    }
}
