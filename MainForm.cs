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
    private readonly Button _back = Palette.Button("→", "رجوع (Alt+←)");
    private readonly Button _forward = Palette.Button("←", "التالي (Alt+→)");
    private readonly Button _reload = Palette.Button("↻", "تحديث (F5)");
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
    private bool _syncAddress, _gateOpen;
    private Control? _gateWarnedFor;

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
        Text = "Feather — متصفح خفيف";
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
        _home.Submit += input => SubmitInput(_home.InputBox, input);
        _home.OpenLink += url => RunUi(() => NavigateInputAsync(url));
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
        // Arabic chrome: mirrored toolbar columns put history buttons on the right and the menu on the left.
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 10, 10, 10),
            ColumnCount = 7, RowCount = 1, BackColor = Palette.Surface
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        _back.Click += (_, _) => RunUi(GoBackAsync);
        _forward.Click += (_, _) => RunUi(GoForwardAsync);
        _reload.Click += (_, _) => RunUi(ReloadAsync);
        var home = Palette.Button("⌂", "الصفحة الرئيسية (Alt+Home)");
        home.Click += (_, _) => RunUi(HomeAsync);
        var addressBox = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Raised, Padding = new Padding(12, 7, 12, 6), Margin = new Padding(7, 0, 5, 0) };
        _address.Dock = DockStyle.Fill;
        _address.BorderStyle = BorderStyle.None;
        _address.BackColor = Palette.Raised;
        _address.ForeColor = Palette.Text;
        _address.Font = new Font("Segoe UI", 11);
        _address.RightToLeft = RightToLeft.Yes;
        _address.PlaceholderText = "عنوان موقع أو بحث في Google";
        _address.AccessibleName = "عنوان الموقع (Ctrl+L)";
        // Any foreign letter typed or pasted here opens the language gate window.
        _address.TextChanged += (_, _) => HandleTypedInput(_address, _address.Text);
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SubmitInput(_address, _address.Text);
        };
        addressBox.Controls.Add(_address);
        _home.InputBox.TextChanged += (_, _) => HandleTypedInput(_home.InputBox, _home.InputBox.Text);
        var go = Palette.Button("انتقال", "الانتقال إلى عنوان أو بحث", 67);
        go.Font = new Font("Segoe UI", 10);
        go.Click += (_, _) => SubmitInput(_address, _address.Text);
        var menu = Palette.Button("☰", "القائمة");
        menu.Click += (_, _) =>
        {
            _menu.Show(menu, new Point(0, menu.Height));
            if (_menu.Left < 0) _menu.Left = 0;
        };
        toolbar.Controls.Add(menu, 0, 0);
        toolbar.Controls.Add(go, 1, 0);
        toolbar.Controls.Add(addressBox, 2, 0);
        toolbar.Controls.Add(home, 3, 0);
        toolbar.Controls.Add(_reload, 4, 0);
        toolbar.Controls.Add(_forward, 5, 0);
        toolbar.Controls.Add(_back, 6, 0);
        foreach (var button in new[] { _back, _forward, _reload, home, go, menu }) _tips.SetToolTip(button, button.AccessibleName);

        _menu.RightToLeft = RightToLeft.Yes;
        _menu.Items.Add("تبويب جديد                                       Ctrl+T", null, (_, _) => RunUi(() => OpenTabAsync()));
        _menu.Items.Add("إفراغ الصفحة / الصفحة الرئيسية", null, (_, _) => RunUi(HomeAsync));
        _menu.Items.Add("التنزيلات                                             Ctrl+J", null, (_, _) => ShowDownloads());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("عن المتصفح وترشيد الذاكرة", null, (_, _) => MessageBox.Show(this,
            "Feather 1.0\n\nلا يوجد في الذاكرة إلا صفحة واحدة. عند التبديل بين التبويبات تُفرَغ الصفحة ثم تُحمَّل من جديد، ويفقد الإدخال غير المحفوظ حالته، وقد تنقطع الفيديوهات والتنزيلات.\n\nتُحفظ الجلسة وملفات الارتباط محليًا. الصفحة الرئيسية الفارغة لا تشغّل المحرك، وعند تصغير النافذة يُستخدم وضع ذاكرة منخفضة.\n\nCtrl+L — العنوان، Ctrl+T — تبويب جديد، Ctrl+W — إغلاق، Ctrl+Tab — التالي، F5 — تحديث، Alt+←/→ — سجل التنقل.\n\nيعتمد استهلاك الذاكرة على الموقع، ولا يوجد حد ثابت.", "عن Feather", MessageBoxButtons.OK, MessageBoxIcon.Information));

        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 34, BackColor = Palette.Surface, ColumnCount = 2, Padding = new Padding(14, 0, 14, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 390));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _status.Dock = _memory.Dock = DockStyle.Fill;
        // Status text hugs the right edge, the memory counter the left one: the mirrored footer.
        // RightToLeft.Yes flips horizontal alignment, hence the mirrored TextAlign values.
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _memory.TextAlign = ContentAlignment.MiddleRight;
        _status.RightToLeft = _memory.RightToLeft = RightToLeft.Yes;
        _status.ForeColor = Palette.Muted;
        _memory.ForeColor = Palette.Accent;
        _status.Font = _memory.Font = new Font("Segoe UI", 9);
        _status.AutoEllipsis = true;
        footer.Controls.Add(_memory, 0, 0);
        footer.Controls.Add(_status, 1, 0);
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
            _status.Text = "أدخل عنوان http/https أو عبارة بحث.";
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
        if (_session.Tabs.Count >= 100) { _status.Text = "تم بلوغ الحد الأقصى: 100 تبويب."; return; }
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
            _status.Text = "تم إيقاف التحميل";
            RefreshChrome();
        }
        else if (ActiveCore is not null) ActiveCore.Reload();
        else await LoadActiveAsync(false);
    });

    private async Task LoadActiveAsync(bool replaceView)
    {
        string? url = ActiveTab.CurrentUrl;
        SetAddress(url is null or "about:blank" ? "" : url);
        bool isHome = url is null or "about:blank";
        if (replaceView || isHome) DestroyView();
        if (isHome)
        {
            ActiveTab.Title = "تبويب جديد";
            _home.Visible = true;
            _status.Text = "الصفحة الرئيسية · لم يُشغَّل المحرك";
            RefreshChrome();
            UpdateMemory();
            return;
        }

        _home.Visible = false;
        _loading = true;
        _status.Text = "جارٍ تحميل الصفحة…";
        RefreshChrome();
        if (_view is null)
        {
            var tab = ActiveTab;
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Palette.Background, AccessibleName = "محتوى الصفحة" };
            _view = view;
            _viewport.Controls.Add(view);
            view.BringToFront();
            try
            {
                _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataPath, "WebView2"), new CoreWebView2EnvironmentOptions { Language = "ar-SA" });
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
                _status.Text = "نوع العنوان هذا غير مدعوم.";
                RefreshChrome();
                return;
            }
            if (_programmaticNavigation || e.IsRedirected) tab.ReplaceCurrent(e.Uri);
            else tab.Visit(e.Uri);
            _programmaticNavigation = false;
            _navigationId = e.NavigationId;
            _loading = true;
            _status.Text = "جارٍ تحميل الصفحة…";
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
            _status.Text = e.IsSuccess ? "اكتمل التحميل · التبويبات الخلفية مُفرَغة" : $"تعذّر تحميل الصفحة: {e.WebErrorStatus}. اضغط ↻ للمحاولة مجددًا.";
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
            if (Current()) _status.Text = "جارٍ تنزيل ملف · ابقِ التبويب مفتوحًا حتى الانتهاء (Ctrl+J).";
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
                _status.Text = "انتهى محرك الصفحة. اضغط ↻ لفتحها من جديد.";
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
        _memory.Text = $"≈ {memory.WorkingSetBytes / 1048576d:N0} م.ب  ·  {LiveViewCount} نشط  /  {sleeping} خامل";
        _tips.SetToolTip(_memory, $"التطبيق + عمليات WebView2 الخاصة به: {memory.ProcessCount}.\nمجموع مجموعات العمل؛ قد تُحسب صفحات الذاكرة المشتركة أكثر من مرة.\nالذاكرة الخاصة المحجوزة: {memory.PrivateBytes / 1048576d:N0} م.ب.\nهذا قياس وليس حدًا مفروضًا.");
    }

    private void RefreshChrome()
    {
        if (_closing) return;
        _tabs.Tabs = _session.Tabs;
        _tabs.SelectedIndex = _session.SelectedIndex;
        _tabs.RevealSelected();
        if (!_address.Focused) SetAddress(ActiveTab.CurrentUrl is null or "about:blank" ? "" : ActiveTab.CurrentUrl);
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
            _status.Text = "تعذّر حفظ الجلسة. تحقّق من صلاحية الوصول إلى مجلد الملف الشخصي.";
        }
    }

    private void FocusAddress() { _address.Focus(); _address.SelectAll(); }

    /// <summary>Writes the address box without letting a programmatic value trip the language gate.</summary>
    private void SetAddress(string value)
    {
        _syncAddress = true;
        try { if (_address.Text != value) _address.Text = value; }
        finally { _syncAddress = false; }
    }

    /// <summary>Runs for every edit in the address bar and on the start page; a foreign letter opens the gate.</summary>
    private void HandleTypedInput(Control source, string text)
    {
        if (_syncAddress) return;
        if (ArabicText.ContainsForeignLetter(text)) WarnLanguage(source);
        else if (_gateWarnedFor == source) _gateWarnedFor = null;
    }

    /// <summary>Shows the picture window once per invalid entry, until the text becomes Arabic again.</summary>
    private void WarnLanguage(Control source)
    {
        if (_gateWarnedFor == source || _smoke || _closing) return;
        _gateWarnedFor = source;
        ShowLanguageGate(source);
    }

    private void ShowLanguageGate(Control source)
    {
        if (_gateOpen || _closing) return;
        _gateOpen = true;
        try
        {
            using var gate = new LanguageGateForm();
            gate.ShowDialog(this);
        }
        finally { _gateOpen = false; }
        if (!IsDisposed && source is TextBox box && !box.ContainsFocus) box.Focus();
    }

    /// <summary>Arabic text navigates; a foreign script raises the gate and blocks the request.</summary>
    private void SubmitInput(Control source, string text)
    {
        if (ArabicText.ContainsForeignLetter(text))
        {
            WarnLanguage(source);
            return;
        }
        RunUi(() => NavigateInputAsync(text));
    }

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
        _status.Text = "تعذّر فتح الصفحة. حاول مرة أخرى.";
        RefreshChrome();
        if (!_smoke) MessageBox.Show(this,
            ex is WebView2RuntimeNotFoundException
                ? "لم يُعثر على Microsoft Edge WebView2 Runtime. ثبّته من https://developer.microsoft.com/microsoft-edge/webview2/ ثم أعد تشغيل Feather."
                : "تعذّر فتح الصفحة.\n" + ex.Message,
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
