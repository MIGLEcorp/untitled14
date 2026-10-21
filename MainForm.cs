using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Feather;

internal readonly record struct MemorySnapshot(long WorkingSetBytes, long PrivateBytes, int ProcessCount);

internal sealed class MainForm : Form
{
    private readonly AppSession _session;
    private readonly TabStrip _tabs = new();
    private readonly TextBox _address = new();
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, BackColor = Palette.Background };
    private readonly HomeSurface _home = new();
    private readonly Label _status = new(), _memory = new();
    private readonly Button _back = Palette.Button("←", "Назад (Alt+←)");
    private readonly Button _forward = Palette.Button("→", "Вперёд (Alt+→)");
    private readonly Button _reload = Palette.Button("↻", "Обновить (F5)");
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _memoryTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 800 };
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Environment? _environment;
    private WebView2? _view;
    private bool _closing, _loading, _programmaticNavigation;
    private ulong _navigationId;
    private readonly bool _smoke;
    private readonly ContextMenuStrip _menu = new();

    internal string DataPath { get; }
    internal Task ReadyTask => _ready.Task;
    internal BrowserTab ActiveTab => _session.Tabs[_session.SelectedIndex];
    internal int TabCount => _session.Tabs.Count;
    internal int LiveViewCount => _viewport.Controls.OfType<WebView2>().Count(x => !x.IsDisposed);
    internal bool IsPageLoading => _loading;
    internal CoreWebView2? ActiveCore => _view?.CoreWebView2;

    public MainForm(string dataPath, bool smoke)
    {
        DataPath = dataPath;
        _smoke = smoke;
        _session = SessionStore.Load(Path.Combine(DataPath, "session.json"));
        Text = "Feather — лёгкий браузер";
        Size = new Size(1180, 800);
        MinimumSize = new Size(820, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        KeyPreview = true;

        BuildChrome();
        _tabs.TabSelected += index => RunUi(() => SelectTabAsync(index));
        _tabs.TabClosed += index => RunUi(() => CloseTabAsync(index));
        _tabs.NewTab += () => RunUi(() => OpenTabAsync());
        _home.Navigate += input => RunUi(() => NavigateInputAsync(input));
        _memoryTimer.Tick += (_, _) => UpdateMemory();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSession(); };
        Resize += (_, _) => SetMemoryTarget();
        Shown += async (_, _) =>
        {
            try
            {
                await TransitionAsync(() => LoadActiveAsync(true));
                _ready.TrySetResult();
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
                ShowError(ex);
            }
            _memoryTimer.Start();
            UpdateMemory();
        };
        RefreshChrome();
    }

    private void BuildChrome()
    {
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 10, 10, 10),
            ColumnCount = 7, RowCount = 1, BackColor = Palette.Surface
        };
        for (int i = 0; i < 4; i++) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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
        toolbar.Controls.Add(go, 5, 0);
        var menu = Palette.Button("☰", "Меню");
        menu.Click += (_, _) => _menu.Show(menu, new Point(0, menu.Height));
        toolbar.Controls.Add(menu, 6, 0);
        _menu.Items.Add("Новая вкладка                         Ctrl+T", null, (_, _) => RunUi(() => OpenTabAsync()));
        _menu.Items.Add("Выгрузить страницу / на главную", null, (_, _) => RunUi(HomeAsync));
        _menu.Items.Add("Загрузки                                        Ctrl+J", null, (_, _) => ShowDownloads());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("О браузере и экономии памяти", null, (_, _) => MessageBox.Show(this,
            "Feather 1.0\n\nВ памяти находится только одна страница. При переключении вкладка выгружается, а при возвращении загружается заново. Несохранённый ввод и состояние страницы теряются; фоновые видео и загрузки могут прерываться.\n\nСессия и cookies хранятся локально. Пустая стартовая страница не запускает движок. При сворачивании используется режим Low Memory.\n\nCtrl+L — адрес, Ctrl+T — вкладка, Ctrl+W — закрыть, Ctrl+Tab — следующая, F5 — обновить, Alt+←/→ — история.\n\nРасход памяти зависит от сайта; фиксированного лимита нет.", "О Feather", MessageBoxButtons.OK, MessageBoxIcon.Information));

        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 34, BackColor = Palette.Surface, ColumnCount = 2, Padding = new Padding(14, 0, 14, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390));
        _status.Dock = _memory.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _memory.TextAlign = ContentAlignment.MiddleRight;
        _status.ForeColor = Palette.Muted;
        _memory.ForeColor = Palette.Accent;
        _status.Font = _memory.Font = new Font("Segoe UI", 9);
        _status.AutoEllipsis = true;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_memory, 1, 0);
        _viewport.Controls.Add(_home);
        Controls.Add(_viewport);
        Controls.Add(footer);
        Controls.Add(toolbar);
        Controls.Add(_tabs);
    }

    private async Task TransitionAsync(Func<Task> action)
    {
        await _transition.WaitAsync();
        try { if (!_closing) await action(); }
        finally { _transition.Release(); }
    }

    internal Task NavigateInputAsync(string input)
    {
        if (!AddressParser.TryResolve(input, out var url))
        {
            _status.Text = "Введите адрес http/https или поисковый запрос.";
            return Task.CompletedTask;
        }
        return TransitionAsync(async () =>
        {
            ActiveTab.Visit(url);
            await LoadActiveAsync(true);
        });
    }

    internal Task OpenTabAsync(string? url = null) => TransitionAsync(async () =>
    {
        if (_session.Tabs.Count >= 100) { _status.Text = "Достигнут предел: 100 вкладок."; return; }
        var tab = new BrowserTab();
        if (url is not null && AddressParser.TryResolve(url, out var resolved)) tab.Visit(resolved);
        _session.Tabs.Add(tab);
        _session.SelectedIndex = _session.Tabs.Count - 1;
        await LoadActiveAsync(true);
        if (tab.CurrentUrl is null) FocusAddress();
    });

    internal Task SelectTabAsync(int index) => TransitionAsync(async () =>
    {
        if (index < 0 || index >= _session.Tabs.Count || index == _session.SelectedIndex) return;
        _session.SelectedIndex = index;
        await LoadActiveAsync(true);
    });

    internal Task CloseTabAsync(int index) => TransitionAsync(async () =>
    {
        if (index < 0 || index >= _session.Tabs.Count) return;
        bool wasActive = index == _session.SelectedIndex;
        _session.Tabs.RemoveAt(index);
        if (_session.Tabs.Count == 0) _session.Tabs.Add(new BrowserTab());
        if (index < _session.SelectedIndex) _session.SelectedIndex--;
        _session.SelectedIndex = Math.Min(_session.SelectedIndex, _session.Tabs.Count - 1);
        if (wasActive) await LoadActiveAsync(true);
        else RefreshChrome();
    });

    internal Task HomeAsync() => TransitionAsync(async () =>
    {
        ActiveTab.Visit("about:blank");
        await LoadActiveAsync(true);
    });

    internal Task GoBackAsync() => TransitionAsync(async () => { if (ActiveTab.CanGoBack) { ActiveTab.Back(); await LoadActiveAsync(true); } });
    internal Task GoForwardAsync() => TransitionAsync(async () => { if (ActiveTab.CanGoForward) { ActiveTab.Forward(); await LoadActiveAsync(true); } });

    private Task ReloadAsync() => TransitionAsync(async () =>
    {
        if (_loading && ActiveCore is not null)
        {
            ActiveCore.Stop();
            _loading = false;
            _status.Text = "Загрузка остановлена";
            RefreshChrome();
        }
        else if (ActiveCore is not null) ActiveCore.Reload();
        else await LoadActiveAsync(false);
    });

    private async Task LoadActiveAsync(bool replaceView)
    {
        string? url = ActiveTab.CurrentUrl;
        _address.Text = url is null or "about:blank" ? "" : url;
        bool isHome = url is null or "about:blank";
        if (replaceView || isHome) DestroyView();
        if (isHome)
        {
            ActiveTab.Title = "Новая вкладка";
            _home.Visible = true;
            _status.Text = "Стартовая страница · движок не запущен";
            RefreshChrome();
            UpdateMemory();
            return;
        }

        _home.Visible = false;
        _loading = true;
        _status.Text = "Загрузка страницы…";
        RefreshChrome();
        if (_view is null)
        {
            var tab = ActiveTab;
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Palette.Background, AccessibleName = "Содержимое страницы" };
            _view = view;
            _viewport.Controls.Add(view);
            view.BringToFront();
            try
            {
                _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataPath, "WebView2"), new CoreWebView2EnvironmentOptions { Language = "ru-RU" });
                if (_closing || view.IsDisposed) return;
                await view.EnsureCoreWebView2Async(_environment);
                if (_closing || view.IsDisposed || _view != view) return;
                ConfigureView(view, tab);
            }
            catch
            {
                DestroyView();
                _home.Visible = true;
                throw;
            }
        }
        if (_closing || ActiveCore is null) return;
        _programmaticNavigation = true;
        ActiveCore.Navigate(url!);
        SetMemoryTarget();
        _view!.Focus();
    }

    private void ConfigureView(WebView2 view, BrowserTab tab)
    {
        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        bool Current() => !_closing && _view == view && ActiveTab == tab;
        view.KeyDown += (_, e) => { if (HandleShortcut(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; } };
        core.NavigationStarting += (_, e) =>
        {
            if (!Current()) return;
            if (!IsWebAddress(e.Uri))
            {
                e.Cancel = true;
                _programmaticNavigation = false;
                _loading = false;
                _status.Text = "Этот тип адреса не поддерживается.";
                RefreshChrome();
                return;
            }
            if (_programmaticNavigation || e.IsRedirected) tab.ReplaceCurrent(e.Uri);
            else tab.Visit(e.Uri);
            _programmaticNavigation = false;
            _navigationId = e.NavigationId;
            _loading = true;
            _status.Text = "Загрузка страницы…";
            RefreshChrome();
        };
        core.SourceChanged += (_, _) =>
        {
            if (!Current() || !IsWebAddress(core.Source)) return;
            if (_loading) tab.ReplaceCurrent(core.Source);
            else tab.Visit(core.Source);
            RefreshChrome();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            if (!Current()) return;
            var title = core.DocumentTitle;
            tab.Title = string.IsNullOrWhiteSpace(title) ? new Uri(core.Source).Host : title[..Math.Min(300, title.Length)];
            RefreshChrome();
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!Current() || e.NavigationId != _navigationId) return;
            _loading = false;
            _status.Text = e.IsSuccess ? "Готово · фоновые вкладки выгружаются" : $"Страница не загружена: {e.WebErrorStatus}. Нажмите ↻ для повтора.";
            RefreshChrome();
            UpdateMemory();
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Current() && IsWebAddress(e.Uri))
            {
                string uri = e.Uri;
                BeginInvoke(() => RunUi(() => OpenTabAsync(uri)));
            }
        };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.DownloadStarting += (_, _) =>
        {
            if (Current()) _status.Text = "Загрузка файла · оставьте вкладку открытой до завершения (Ctrl+J).";
        };
        core.ProcessFailed += (_, e) =>
        {
            if (!Current()) return;
            Program.LogError(DataPath, new InvalidOperationException("WebView2 process failed: " + e.ProcessFailedKind));
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
            BeginInvoke(() =>
            {
                if (!Current()) return;
                DestroyView();
                _home.Visible = true;
                _status.Text = "Движок страницы завершился. Нажмите ↻, чтобы открыть её снова.";
                RefreshChrome();
            });
        };
    }

    private static bool IsWebAddress(string value) => value == "about:blank" ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");

    private void DestroyView()
    {
        var view = _view;
        _view = null;
        _loading = false;
        _programmaticNavigation = false;
        if (view is null) return;
        _viewport.Controls.Remove(view);
        view.Dispose();
    }

    private void SetMemoryTarget()
    {
        if (ActiveCore is null || _closing) return;
        try { ActiveCore.MemoryUsageTargetLevel = WindowState == FormWindowState.Minimized ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; }
        catch (NotImplementedException) { }
        catch (COMException) { }
        catch (InvalidOperationException) { }
    }

    internal MemorySnapshot ReadMemory()
    {
        var ids = new HashSet<int> { Environment.ProcessId };
        if (_environment is not null)
        {
            try { foreach (var process in _environment.GetProcessInfos()) ids.Add(process.ProcessId); }
            catch (COMException) { }
            catch (InvalidOperationException) { }
        }
        long working = 0, committed = 0;
        int count = 0;
        foreach (int id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                working += process.WorkingSet64;
                committed += process.PrivateMemorySize64;
                count++;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return new MemorySnapshot(working, committed, count);
    }

    private void UpdateMemory()
    {
        if (_closing) return;
        var memory = ReadMemory();
        int sleeping = _session.Tabs.Count(t => t != ActiveTab && t.CurrentUrl is not null and not "about:blank");
        _memory.Text = $"≈ {memory.WorkingSetBytes / 1048576d:N0} МБ  ·  {LiveViewCount} активная  /  {sleeping} спящих";
        _tips.SetToolTip(_memory, $"Приложение + процессы его WebView2: {memory.ProcessCount}.\nСумма рабочих наборов; общие страницы памяти могут считаться повторно.\nЧастная выделенная память: {memory.PrivateBytes / 1048576d:N0} МБ.\nЭто измерение, а не установленный лимит.");
    }

    private void RefreshChrome()
    {
        if (_closing) return;
        _tabs.Tabs = _session.Tabs;
        _tabs.SelectedIndex = _session.SelectedIndex;
        _tabs.RevealSelected();
        if (!_address.Focused) _address.Text = ActiveTab.CurrentUrl is null or "about:blank" ? "" : ActiveTab.CurrentUrl;
        _back.Enabled = ActiveTab.CanGoBack;
        _forward.Enabled = ActiveTab.CanGoForward;
        _reload.Text = _loading ? "×" : "↻";
        Text = ActiveTab.Title + " — Feather";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSession()
    {
        try { SessionStore.Save(Path.Combine(DataPath, "session.json"), _session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.LogError(DataPath, ex);
            _status.Text = "Не удалось сохранить сессию. Проверьте доступ к папке профиля.";
        }
    }

    private void FocusAddress() { _address.Focus(); _address.SelectAll(); }

    private void ShowDownloads()
    {
        try { ActiveCore?.OpenDefaultDownloadDialog(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { ShowError(ex); }
    }

    private bool HandleShortcut(Keys keys)
    {
        Action? action = keys switch
        {
            Keys.Control | Keys.L => FocusAddress,
            Keys.Alt | Keys.D => FocusAddress,
            Keys.Control | Keys.T => () => RunUi(() => OpenTabAsync()),
            Keys.Control | Keys.W => () => RunUi(() => CloseTabAsync(_session.SelectedIndex)),
            Keys.Control | Keys.Tab => () => RunUi(() => SelectTabAsync((_session.SelectedIndex + 1) % TabCount)),
            Keys.Control | Keys.Shift | Keys.Tab => () => RunUi(() => SelectTabAsync((_session.SelectedIndex + TabCount - 1) % TabCount)),
            Keys.Alt | Keys.Left => () => RunUi(GoBackAsync),
            Keys.Alt | Keys.Right => () => RunUi(GoForwardAsync),
            Keys.Alt | Keys.Home => () => RunUi(HomeAsync),
            Keys.F5 => () => RunUi(ReloadAsync),
            Keys.Control | Keys.R => () => RunUi(ReloadAsync),
            Keys.Control | Keys.J => ShowDownloads,
            _ => null
        };
        if (action is null) return false;
        // Leave WebView's synchronous keyboard callback before destroying a control.
        BeginInvoke(action);
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    private async void RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { if (!_closing) ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        Program.LogError(DataPath, ex);
        _loading = false;
        _status.Text = "Не удалось открыть страницу. Попробуйте ещё раз.";
        RefreshChrome();
        if (!_smoke) MessageBox.Show(this,
            ex is WebView2RuntimeNotFoundException
                ? "Не найден Microsoft Edge WebView2 Runtime. Установите его с https://developer.microsoft.com/microsoft-edge/webview2/ и запустите Feather снова."
                : "Не удалось открыть страницу.\n" + ex.Message,
            "Feather", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        _closing = true;
        _memoryTimer.Stop();
        _saveTimer.Stop();
        SaveSession();
        DestroyView();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _memoryTimer.Dispose();
            _saveTimer.Dispose();
            _tips.Dispose();
            _menu.Dispose();
            DestroyView();
        }
        base.Dispose(disposing);
    }
}
