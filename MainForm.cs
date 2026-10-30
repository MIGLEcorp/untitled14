using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MBrowser;

internal readonly record struct MemorySnapshot(long WorkingSetBytes, long PrivateBytes, int ProcessCount);

/// <summary>Application coordinator. UI, tab lifecycle and engine events live in separate files.</summary>
internal sealed partial class MainForm : BrowserWindow
{
    private readonly AppSession _session;
    private readonly BrowserPreferences _preferences;
    private readonly TabStrip _tabs = new();
    private readonly TextBox _address = new();
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, BackColor = Palette.Background };
    private readonly HomeSurface _home = new();
    private readonly Label _status = new();
    private readonly Button _back = Palette.Button("←", "Назад (Alt+←)");
    private readonly Button _forward = Palette.Button("→", "Вперёд (Alt+→)");
    private readonly Button _reload = Palette.Button("↻", "Обновить (F5)");
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 15000 };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 800 };
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly SemaphoreSlim _sessionIoGate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closing;
    private readonly bool _smoke;
    private GlassPanel? _menu;

    internal string DataPath { get; }
    internal Task ReadyTask => _ready.Task;
    internal BrowserTab ActiveTab => _session.Tabs[_session.SelectedIndex];
    internal int TabCount => _session.Tabs.Count;
    internal int LiveViewCount => _cefRuntimes.Count;
    internal bool IsPageLoading => _cefRuntimes.GetValueOrDefault(ActiveTab.Id)?.Loading ?? false;
    internal bool HasLoadingTabs => _cefRuntimes.Values.Any(r => r.Loading);

    public MainForm(string dataPath, bool smoke, string[]? initialTargets = null,
        bool cefEngine = false, bool customCef = false)
    {
        _cefEngine = true;
        _cefCustomLibCef = customCef;
        DataPath = dataPath;
        _smoke = smoke;
        _preferences = BrowserPreferences.Load(dataPath);
        _cefStartupFeatures = cefEngine ? CefMemoryFeatures.CommandLine(_preferences, customCef) : "";
        ThemeSettings.Load(dataPath);
        _session = SessionStore.Load(Path.Combine(dataPath, "session.json"));
        foreach(string target in (initialTargets ?? []).Take(16))
        {
            if(_session.Tabs.Count>=100) break;
            var tab=new BrowserTab();
            if(AddressParser.TryWebUrl(target,out var url)) tab.Visit(url);
            else if(BrowserDocuments.TryResolve(target,out var file)) { tab.Visit(file); tab.LocalDocuments.Add(file); }
            else continue;
            _session.Tabs.Add(tab); _session.SelectedIndex=_session.Tabs.Count-1;
        }
        Text = "MBrowser — Chromium CEF";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Size = new Size(1180, 800);
        MinimumSize = new Size(820, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        KeyPreview = true;
        BuildChrome();
        InitializeWindowFrame(_tabs);
        FullScreenChanged += UpdateChromeLayout;
        Palette.Apply(this);

        _tabs.IsTabLoaded = id => _cefRuntimes.TryGetValue(id, out var cef) && CefIsAlive(cef);
        _tabs.TabSelected += index => RunUi(() => SelectTabAsync(index));
        _tabs.TabClosed += index => RunUi(() => CloseTabAsync(index));
        _tabs.NewTab += () => RunUi(() => OpenTabAsync());
        _home.Navigate += input => RunUi(() => NavigateInputAsync(input));
        _idleTimer.Tick += (_, _) => RunUi(() => UnloadIdleTabsAsync(DateTimeOffset.UtcNow));
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); RunUi(SaveSessionAsync); };
        Resize += (_, _) => { SetMemoryTargets(); DismissPasswordPopup(); };
        Move += (_, _) => DismissPasswordPopup();
        Deactivate += (_, _) => { _inactiveSinceUtc = DateTimeOffset.UtcNow; DismissPasswordPopup(); ThemeMedia.SetActive(false); };
        Activated += (_, _) => { ResumeActivePage(); ThemeMedia.SetActive(true); };
        Shown += async (_, _) =>
        {
            try
            {
                await TransitionAsync(() => ShowActiveTabAsync());
                _ready.TrySetResult();
            }
            catch (Exception ex) { _ready.TrySetException(ex); ShowError(ex); }
            _idleTimer.Start();
            UpdateMemory();
        };
        RefreshChrome();
    }

    private async Task TransitionAsync(Func<Task> action)
    {
        await _transition.WaitAsync();
        try { if (!_closing) await action(); }
        finally { _transition.Release(); }
    }

    private void RefreshChrome()
    {
        if (_closing) return;
        _tabs.Tabs = _session.Tabs;
        _tabs.SelectedIndex = _session.SelectedIndex;
        _tabs.RevealSelected();
        UpdateChromeLayout();
        if (!_address.Focused) _address.Text = ActiveTab.CurrentUrl is null or "about:blank" ? "" : ActiveTab.CurrentUrl;
        _back.Enabled = ActiveTab.CanGoBack;
        _forward.Enabled = ActiveTab.CanGoForward;
        _reload.Text = IsPageLoading ? "×" : "↻";
        Text = (_cefEngine ? "MBrowser [CEF 154] — " : "MBrowser — ") + ActiveTab.Title;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private Task SaveSessionAsync()
    {
        // Snapshot on UI thread, then do all I/O and lock continuations off the UI thread.
        byte[] snapshot = SessionStore.Capture(_session);
        return WriteSessionSnapshotAsync(snapshot);
    }

    private async Task WriteSessionSnapshotAsync(byte[] snapshot)
    {
        await _sessionIoGate.WaitAsync().ConfigureAwait(false);
        try { await SessionStore.SaveBytesAsync(Path.Combine(DataPath, "session.json"), snapshot).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.LogError(DataPath, ex); }
        finally { _sessionIoGate.Release(); }
    }

    private void SaveSession()
    {
        // Shutdown path must persist the final state before the application exits.
        _sessionIoGate.Wait();
        try { SessionStore.Save(Path.Combine(DataPath, "session.json"), _session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.LogError(DataPath, ex);
            if (!_closing) _status.Text = "Не удалось сохранить сессию. Проверьте доступ к папке профиля.";
        }
        finally { _sessionIoGate.Release(); }
    }

    internal MemorySnapshot ReadMemory()
    {
        var ids = new HashSet<int> { Environment.ProcessId };
        if (_cefEngine)
            foreach (int childId in CefProcessMetrics.GetDescendantIds(Environment.ProcessId))
                ids.Add(childId);
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
        return new(working, committed, count);
    }

    private void UpdateMemory() => _tabs.Invalidate();

    private async void RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { if (!_closing) ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        Program.LogError(DataPath, ex);
        _status.Text = "Операция не выполнена: " + ex.Message;
        RefreshChrome();
        if (!_smoke) MessageBox.Show(this, ex.Message, "MBrowser",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        _closing = true;
        ImageText.StopAll();
        _idleTimer.Stop();
        _saveTimer.Stop();
        SaveSession();
        DestroyAllViews();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            DismissPasswordPopup();
            _idleTimer.Dispose();
            _saveTimer.Dispose();
            _tips.Dispose();
            CloseOverlay();
            _suggestionCancellation?.Cancel();
            _faviconCache?.Dispose();
            DestroyAllViews();
            ThemeMedia.ReleaseAll();
        }
        base.Dispose(disposing);
    }
}
