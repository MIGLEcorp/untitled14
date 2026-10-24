using System.Text.Json;

namespace MBrowser.Features;

/// <summary>On-demand dialogs. The host can unload this assembly after each modal operation.</summary>
public sealed partial class BrowserLibrary
{
    private readonly BookmarkStore bookmarks;
    private readonly PasswordVault passwords;

    public BrowserLibrary(string dataPath)
    {
        FeatureUi.LoadTheme(dataPath);
        bookmarks = new BookmarkStore(Path.Combine(dataPath, "bookmarks.json"));
        passwords = new PasswordVault(Path.Combine(dataPath, "passwords.dpapi"));
    }

    public void ShowBookmarks(IWin32Window owner, string? currentUrl, string? title, Action<string> navigate)
    {
        using var dialog = FeatureUi.Dialog("Закладки");
        var list = FeatureUi.List("Название", "Адрес");
        var search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Поиск по названию или адресу", Height = 32 };
        var buttons = FeatureUi.Buttons();
        var add = FeatureUi.Button("Добавить страницу", () => Guard(dialog, () =>
        {
            var values = FeatureUi.EditFields(dialog, "Добавить закладку", [("Название", title ?? "", false), ("Адрес", currentUrl ?? "https://", false)]);
            if (values is null) return;
            if (!FeatureFiles.TryWebUrl(values[1], out _)) throw new InvalidDataException("Введите полный адрес http:// или https://.");
            bookmarks.Add([new Bookmark(values[0], values[1])]);
            Refresh();
        }));
        buttons.Controls.Add(add);
        buttons.Controls.Add(FeatureUi.Button("Открыть", Open));
        buttons.Controls.Add(FeatureUi.Button("Удалить", () => Guard(dialog, () =>
        {
            if (list.SelectedItems.Count == 0) return;
            bookmarks.Remove(((Bookmark)list.SelectedItems[0].Tag!).Url);
            Refresh();
        })));
        buttons.Controls.Add(FeatureUi.Button("Импорт", () => Guard(dialog, () =>
        {
            using var file = BookmarkFileDialog();
            if (file.ShowDialog(dialog) != DialogResult.OK) return;
            int count = bookmarks.ImportFile(file.FileName);
            Refresh();
            FeatureUi.Info(dialog, $"Добавлено закладок: {count}.");
        })));
        buttons.Controls.Add(FeatureUi.Button("Экспорт HTML", () => Guard(dialog, () =>
        {
            using var file = new SaveFileDialog { Filter = "Закладки HTML|*.html", FileName = "MBrowser-bookmarks.html", DefaultExt = "html" };
            if (file.ShowDialog(dialog) == DialogResult.OK) bookmarks.ExportHtml(file.FileName);
        })));
        dialog.Controls.Add(list);
        dialog.Controls.Add(search);
        dialog.Controls.Add(buttons);
        search.TextChanged += (_, _) => Guard(dialog, Refresh);
        list.DoubleClick += (_, _) => Open();
        Guard(dialog, Refresh);
        dialog.ShowDialog(owner);

        void Refresh()
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var item in bookmarks.Load().Where(item => (item.Title + " " + item.Url).Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
                    list.Items.Add(new ListViewItem([item.Title, item.Url]) { Tag = item });
            }
            finally { list.EndUpdate(); }
        }
        void Open()
        {
            if (list.SelectedItems.Count == 0) return;
            string url = ((Bookmark)list.SelectedItems[0].Tag!).Url;
            dialog.Close();
            navigate(url);
        }
    }

    public void ShowPasswords(IWin32Window owner)
    {
        using var dialog = FeatureUi.Dialog("Пароли");
        var list = FeatureUi.List("Сайт", "Имя пользователя", "Название");
        var note = FeatureUi.Note("Сохранённые и импортированные пароли защищены вашей учётной записью Windows. Единственная подходящая запись заполняется автоматически на странице входа. Нажмите на логин или пароль, чтобы выбрать учётную запись во всплывающем меню под полем. Автосохранение новых паролей работает через WebView2.");
        var buttons = FeatureUi.Buttons();
        buttons.Controls.Add(FeatureUi.Button("Добавить", () => Edit(null)));
        buttons.Controls.Add(FeatureUi.Button("Изменить / показать", () => { if (Selected() is { } entry) Edit(entry); }));
        buttons.Controls.Add(FeatureUi.Button("Удалить", () => Guard(dialog, () =>
        {
            if (Selected() is not { } entry) return;
            passwords.Save(passwords.Load().Where(item => item.Id != entry.Id));
            Refresh();
        })));
        buttons.Controls.Add(FeatureUi.Button("Импорт CSV", () => Guard(dialog, () =>
        {
            using var file = new OpenFileDialog { Filter = "Экспорт паролей CSV|*.csv|Все файлы|*.*", Title = "Выберите экспорт паролей Chrome, Edge или Firefox" };
            if (file.ShowDialog(dialog) != DialogResult.OK) return;
            int count = passwords.ImportCsv(file.FileName);
            Refresh();
            FeatureUi.Info(dialog, $"Добавлено паролей: {count}. Существующие записи сохранены. Исходный CSV содержит открытые пароли; удалите его после переноса, если он больше не нужен.");
        })));
        dialog.Controls.Add(list);
        dialog.Controls.Add(note);
        dialog.Controls.Add(buttons);
        list.DoubleClick += (_, _) => { if (Selected() is { } entry) Edit(entry); };
        Guard(dialog, Refresh);
        dialog.ShowDialog(owner);

        PasswordEntry? Selected() => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as PasswordEntry;
        void Refresh()
        {
            list.Items.Clear();
            foreach (var item in passwords.Load())
                list.Items.Add(new ListViewItem([item.Origin, item.Username, item.Title]) { Tag = item });
        }
        void Edit(PasswordEntry? entry) => Guard(dialog, () =>
        {
            var values = FeatureUi.EditFields(dialog, entry is null ? "Добавить пароль" : "Изменить пароль",
                [("Название", entry?.Title ?? "", false), ("Адрес сайта", entry?.Url ?? "https://", false), ("Имя пользователя", entry?.Username ?? "", false), ("Пароль", entry?.Password ?? "", true)]);
            if (values is null) return;
            if (!FeatureFiles.TryWebUrl(values[1], out string url) || values[3].Length == 0) throw new InvalidDataException("Введите полный адрес http:// или https:// и непустой пароль.");
            var entries = passwords.Load().ToList();
            entries.RemoveAll(item => item.Id == entry?.Id);
            entries.Add(new PasswordEntry(entry?.Id ?? Guid.NewGuid(), values[0], url, values[2], values[3]));
            passwords.Save(entries);
            Refresh();
        });
    }

    public void ShowImport(IWin32Window owner, Func<string, Task<string>> applyCookiesJson, Action<string> navigate)
    {
        using var dialog = FeatureUi.Dialog("Перенос из других браузеров");
        var profiles = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Height = 32, BackColor = FeatureUi.Surface, ForeColor = FeatureUi.Text };
        foreach (var profile in BrowserProfiles.Discover()) profiles.Items.Add(profile);
        if (profiles.Items.Count > 0) profiles.SelectedIndex = 0;
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14), AutoScroll = true };
        bool busy = false;
        var profileBookmarks = FeatureUi.Button("Закладки выбранного профиля", () => Guard(dialog, () =>
        {
            if (profiles.SelectedItem is not BrowserProfile profile || profile.BookmarkPath is null) throw new InvalidOperationException("В выбранном профиле не найдены закладки.");
            FeatureUi.Info(dialog, $"Добавлено закладок: {bookmarks.ImportFile(profile.BookmarkPath)}.");
        }));
        profileBookmarks.AutoSize = true;
        content.Controls.Add(profileBookmarks);
        var profileCookies = FeatureUi.Button("Cookie выбранного Firefox-профиля", () => { });
        profileCookies.Click += async (_, _) =>
        {
            if (profiles.SelectedItem is not BrowserProfile profile || !profile.IsFirefox || profile.CookiePath is null)
            { FeatureUi.Info(dialog, "Выберите профиль Firefox с cookies.sqlite. Для Chrome, Edge и других Chromium-браузеров используйте экспорт cookie в JSON или Netscape TXT."); return; }
            await ImportCookies(profile.CookiePath);
        };
        content.Controls.Add(profileCookies);
        content.Controls.Add(FeatureUi.Button("Закладки из HTML / JSON / SQLite", () => Guard(dialog, () =>
        {
            using var file = BookmarkFileDialog();
            if (file.ShowDialog(dialog) == DialogResult.OK) FeatureUi.Info(dialog, $"Добавлено закладок: {bookmarks.ImportFile(file.FileName)}.");
        })));
        var cookieFile = FeatureUi.Button("Cookie из JSON / Netscape TXT / Firefox SQLite", () => { });
        cookieFile.Click += async (_, _) =>
        {
            using var file = new OpenFileDialog { Title = "Импорт cookie", Filter = "Cookie JSON, TXT, Firefox SQLite|*.json;*.txt;*.sqlite|Все файлы|*.*" };
            if (file.ShowDialog(dialog) == DialogResult.OK) await ImportCookies(file.FileName);
        };
        content.Controls.Add(cookieFile);
        content.Controls.Add(FeatureUi.Button("Пароли из CSV", () => ShowPasswords(dialog)));
        var note = new Label
        {
            AutoSize = false, Width = 740, Height = 170,
            Text = "Закладки: Chrome, Edge, Brave, Vivaldi, Opera, Chromium и Firefox. Можно выбрать экспорт вручную.\r\n\r\nCookie Firefox читаются напрямую, включая новые версии базы. Разделённые cookie переносятся с исходным сайтом верхнего уровня; если движок не поддерживает контекст, запись пропускается с объяснением. Cookie отдельных контейнеров и приватных контекстов не смешиваются с обычными.\r\n\r\nCookie Chromium: нужен экспорт JSON или Netscape TXT. Импорт может перенести сеансы; сайты могут потребовать повторный вход. Если профиль занят, закройте исходный браузер. Исходные файлы не изменяются."
        };
        content.Controls.Add(note);
        dialog.Controls.Add(content);
        dialog.Controls.Add(profiles);
        dialog.FormClosing += (_, args) => { if (busy) args.Cancel = true; };
        dialog.ShowDialog(owner);

        async Task ImportCookies(string source)
        {
            if (busy) return;
            busy = true;
            content.Enabled = false;
            profiles.Enabled = false;
            dialog.UseWaitCursor = true;
            try
            {
                var imported = CookieImport.ReadFile(source);
                if (imported.Cookies.Count == 0) { FeatureUi.Info(dialog, $"Нет записей для импорта.\r\n{imported.SkipSummary}"); return; }
                string report = await applyCookiesJson(CookieImport.Serialize(imported.Cookies));
                if (imported.Skipped > 0) report += "\r\nПропущено при чтении: " + imported.Skipped + ". " + imported.SkipSummary;
                FeatureUi.Info(dialog, report + "\r\nОбновите открытую страницу.");
            }
            catch (Exception ex) { FeatureUi.Error(dialog, ex.Message); }
            finally { busy = false; content.Enabled = true; profiles.Enabled = true; dialog.UseWaitCursor = false; }
        }
    }

    private static OpenFileDialog BookmarkFileDialog() => new() { Title = "Импорт закладок", Filter = "Закладки HTML / JSON / Firefox SQLite|*.html;*.htm;*.json;*.sqlite;Bookmarks|Все файлы|*.*" };
    private static void Guard(IWin32Window owner, Action action)
    {
        try { action(); }
        catch (Exception ex) { FeatureUi.Error(owner, ex.Message); }
    }
}

internal static class FeatureUi
{
    internal static Color Background { get; private set; } = Color.FromArgb(21, 19, 35);
    internal static Color Surface { get; private set; } = Color.FromArgb(32, 28, 49);
    internal static Color Raised { get; private set; } = Color.FromArgb(48, 41, 68);
    internal static Color Text { get; private set; } = Color.FromArgb(249, 243, 252);

    internal static void LoadTheme(string dataPath)
    {
        string path = Path.Combine(dataPath, "theme.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return;
            string? theme = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
            (Background, Surface, Raised, Text) = theme?.ToLowerInvariant() switch
            {
                "light" => (Color.FromArgb(247, 245, 249), Color.FromArgb(237, 232, 241), Color.FromArgb(226, 218, 233), Color.FromArgb(42, 32, 53)),
                "dark" => (Color.FromArgb(16, 22, 30), Color.FromArgb(24, 33, 43), Color.FromArgb(34, 46, 58), Color.FromArgb(235, 242, 245)),
                _ => (Color.FromArgb(21, 19, 35), Color.FromArgb(32, 28, 49), Color.FromArgb(48, 41, 68), Color.FromArgb(249, 243, 252))
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal static Form Dialog(string title) => new()
    {
        Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(850, 560), MinimumSize = new Size(640, 400),
        BackColor = Background, ForeColor = Text, Font = new Font("Segoe UI", 10),
        ShowIcon = false, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false
    };

    internal static ListView List(params string[] columns)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            BackColor = Surface, ForeColor = Text, BorderStyle = BorderStyle.None };
        foreach (string column in columns) list.Columns.Add(column, Math.Max(180, 790 / columns.Length));
        return list;
    }

    internal static FlowLayoutPanel Buttons() => new() { Dock = DockStyle.Bottom, Height = 88, Padding = new Padding(8), WrapContents = true };
    internal static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 5, 8, 5), FlatStyle = FlatStyle.Flat, BackColor = Raised };
        button.Click += (_, _) => action();
        return button;
    }

    internal static Label Note(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 76, Padding = new Padding(10), AutoEllipsis = true };
    internal static void Info(IWin32Window owner, string text) => MessageBox.Show(owner, text, "MBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
    internal static void Error(IWin32Window owner, string text) => MessageBox.Show(owner, text, "Не удалось выполнить действие", MessageBoxButtons.OK, MessageBoxIcon.Error);

    internal static string[]? EditFields(IWin32Window owner, string title, (string Label, string Value, bool Secret)[] fields)
    {
        using var dialog = Dialog(title);
        dialog.Size = new Size(600, 170 + fields.Length * 70);
        dialog.MinimumSize = dialog.Size;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = fields.Length * 2 + 1 };
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            content.Controls.Add(new Label { Text = field.Label, AutoSize = true, Margin = new Padding(0, 6, 0, 4) });
            var input = new TextBox { Text = field.Value, Dock = DockStyle.Top, UseSystemPasswordChar = field.Secret, BackColor = Surface, ForeColor = Text };
            content.Controls.Add(input);
            inputs.Add(input);
        }
        var show = new CheckBox { Text = "Показать пароль", AutoSize = true };
        if (fields.Any(field => field.Secret))
        {
            show.CheckedChanged += (_, _) => { for (int i = 0; i < fields.Length; i++) if (fields[i].Secret) inputs[i].UseSystemPasswordChar = !show.Checked; };
            content.Controls.Add(show);
        }
        var buttons = Buttons();
        var save = Button("Сохранить", () => { dialog.DialogResult = DialogResult.OK; dialog.Close(); });
        buttons.Controls.Add(save);
        buttons.Controls.Add(Button("Отмена", () => { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }));
        dialog.AcceptButton = save;
        dialog.Controls.Add(content);
        dialog.Controls.Add(buttons);
        return dialog.ShowDialog(owner) == DialogResult.OK ? inputs.Select(input => input.Text).ToArray() : null;
    }
}
