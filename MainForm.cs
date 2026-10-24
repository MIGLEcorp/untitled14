using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

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
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<Guid, TabRuntime> _runtimes = [];
    private CoreWebView2Environment? _environment;
    private bool _closing;
    private readonly bool _smoke;
    private GlassPanel? _menu;

    internal string DataPath { get; }
    internal Task ReadyTask => _ready.Task;
    internal BrowserTab ActiveTab => _session.Tabs[_session.SelectedIndex];
    internal int TabCount => _session.Tabs.Count;
    internal int LiveViewCount => _runtimes.Count;
    internal bool IsPageLoading => ActiveRuntime?.Loading ?? false;
    internal bool HasLoadingTabs => _runtimes.Values.Any(runtime => runtime.Loading);
    private TabRuntime? ActiveRuntime => _runtimes.GetValueOrDefault(ActiveTab.Id);
    internal CoreWebView2? ActiveCore => ActiveRuntime?.View.CoreWebView2;

    public MainForm(string dataPath, bool smoke)
    {
        DataPath = dataPath;
        _smoke = smoke;
        _preferences = BrowserPreferences.Load(dataPath);
        ThemeSettings.Load(dataPath);
        _session = SessionStore.Load(Path.Combine(dataPath, "session.json"));
        Text = "MBrowser";
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
        Palette.Apply(this);

        _tabs.IsTabLoaded = id => _runtimes.TryGetValue(id, out var runtime) && runtime.View.CoreWebView2?.IsSuspended != true;
        _tabs.TabSelected += index => RunUi(() => SelectTabAsync(index));
        _tabs.TabClosed += index => RunUi(() => CloseTabAsync(index));
        _tabs.NewTab += () => RunUi(() => OpenTabAsync());
        _home.Navigate += input => RunUi(() => NavigateInputAsync(input));
        _idleTimer.Tick += (_, _) => RunUi(() => UnloadIdleTabsAsync(DateTimeOffset.UtcNow));
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSession(); };
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
        if (!_address.Focused) _address.Text = ActiveTab.CurrentUrl is null or "about:blank" ? "" : ActiveTab.CurrentUrl;
        _back.Enabled = ActiveTab.CanGoBack;
        _forward.Enabled = ActiveTab.CanGoForward;
        _reload.Text = IsPageLoading ? "×" : "↻";
        Text = ActiveTab.Title + " — MBrowser";
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
        if (!_smoke) MessageBox.Show(this,
            ex is WebView2RuntimeNotFoundException
                ? "Не найден Microsoft Edge WebView2 Runtime. Установите Runtime и запустите MBrowser снова."
                : ex.Message, "MBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        _closing = true;
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
