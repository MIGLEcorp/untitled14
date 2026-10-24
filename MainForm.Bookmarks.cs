using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private readonly ThemeBookmarks _bookmarkBar = new() { Dock = DockStyle.Top, Height = 37, WrapContents = false,
        AutoScroll = false, Padding = new Padding(8, 1, 8, 1), BackColor = Palette.Surface };
    private bool _layingOutBookmarks;
    private FaviconCache? _faviconCache;
    private FaviconCache Favicons => _faviconCache ??= new FaviconCache(DataPath);
    private sealed record BookmarkItem(string Title, string Url);
    private List<BookmarkItem> _bookmarks = [];

    private void InitializeBookmarksAndSuggestions()
    {
        _tabs.GetFavicon = tab => Favicons.GetCached(tab.CurrentUrl);
        RefreshBookmarks();
        _bookmarkBar.Layout += (_, _) => LayoutBookmarkItems();
        AttachSuggestions(_address);
        AttachSuggestions(_home.SearchBox);
        _viewport.Resize += (_, _) =>
        {
            if (_overlay is ThemeEditor editor)
            {
                editor.Size = new Size(Math.Min(1000, Math.Max(240, _viewport.Width - 24)), Math.Min(690, Math.Max(90, _viewport.Height - 24)));
                editor.Location = new Point(Math.Max(10, _viewport.Width - editor.Width - 12), 10);
                editor.UpdateChromeReference(new Size(_chromeHost.Width,TitleBarHeight + _toolbar.Height + _bookmarkBar.Height));
            }
            else CloseOverlay();
        };
    }

    internal void RefreshBookmarks()
    {
        _bookmarks = [];
        try
        {
            string path = Path.Combine(DataPath, "bookmarks.json");
            if (File.Exists(path) && new FileInfo(path).Length <= 32 * 1024 * 1024)
                _bookmarks = (JsonSerializer.Deserialize<List<BookmarkItem>>(File.ReadAllText(path)) ?? [])
                    .Where(item => item is not null && AddressParser.TryWebUrl(item.Url, out _)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _status.Text = "Не удалось прочитать закладки."; }
        _bookmarkBar.SuspendLayout();
        foreach (Control control in _bookmarkBar.Controls.Cast<Control>().ToArray()) control.Dispose();
        _bookmarkBar.Controls.Clear();
        foreach (var item in _bookmarks.Take(80))
        {
            var button = Palette.Button(item.Title, item.Title, Math.Clamp(TextRenderer.MeasureText(item.Title, Font).Width + 40, 65, 170));
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.BackColor = Color.Transparent;
            button.Height = 30;
            button.Font = new Font("Segoe UI", 9);
            button.Margin = new Padding(0, 0, 3, 0);
            button.Tag = item;
            button.Click += (_, _) => RunUi(() => NavigateInputAsync(item.Url));
            _tips.SetToolTip(button, item.Url);
            _bookmarkBar.Controls.Add(button);
            button.Image = Favicons.GetCached(item.Url);
        }
        var add = Palette.Button("＋", "Добавить или изменить закладки", 34);
        add.BackColor = Color.Transparent;
        add.Height = 30;
        add.Click += (_, _) => ShowBookmarks();
        _bookmarkBar.Controls.Add(add);
        var more = Palette.Button("»", "Остальные закладки", 34);
        more.BackColor = Color.Transparent;
        more.Height = 30;
        more.Tag = "overflow";
        more.Click += (_, _) => ShowMenuPage("Закладки", entry =>
        {
            foreach (var bookmark in _bookmarks.Skip(_bookmarkBar.Controls.Cast<Control>().Count(control => control.Tag is BookmarkItem && control.Visible)).Take(30))
                entry(bookmark.Title, () => RunUi(() => NavigateInputAsync(bookmark.Url)));
            entry("Все закладки…", ShowBookmarks);
        });
        _bookmarkBar.Controls.Add(more);
        _bookmarkBar.ResumeLayout();
        LayoutBookmarkItems();
    }

    private void LayoutBookmarkItems()
    {
        if (_layingOutBookmarks || _bookmarkBar.IsDisposed) return;
        _layingOutBookmarks = true;
        try
        {
            int used = 0, visible = 0, available = Math.Max(0, _bookmarkBar.ClientSize.Width - 100);
            foreach (Button button in _bookmarkBar.Controls.OfType<Button>())
            {
                if (button.Tag is BookmarkItem item)
                {
                    button.Visible = used + button.Width + 3 <= available;
                    if (!button.Visible) continue;
                    used += button.Width + 3; visible++;
                    if (!_smoke && button.Image is null) RunUi(async () =>
                    { var image = await Favicons.GetAsync(item.Url); if (!button.IsDisposed && image is not null) { button.Image = image; button.Invalidate(); } });
                }
                else if (button.Tag as string == "overflow") button.Visible = _bookmarks.Count > visible;
            }
        }
        finally { _layingOutBookmarks = false; }
    }
}
