using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MBrowser;

internal sealed partial class MainForm
{
    private sealed class TabRuntime(BrowserTab tab, WebView2 view)
    {
        internal BrowserTab Tab { get; } = tab;
        internal WebView2 View { get; } = view;
        internal DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
        internal bool Loading { get; set; } = true;
        internal bool ProgrammaticNavigation { get; set; } = true;
        internal bool TraversingHistory { get; set; }
        internal ulong NavigationId { get; set; }
        internal int DownloadCount { get; set; }
        internal HashSet<string> PendingRequests { get; } = [];
        internal int NativeIndex { get; set; } = -1;
        internal Dictionary<int, int> NativeEntries { get; } = [];
        internal long HistoryRevision { get; set; }
    }

    private bool IsAlive(TabRuntime runtime) => !_closing &&
        _runtimes.GetValueOrDefault(runtime.Tab.Id) == runtime && !runtime.View.IsDisposed;

    private async Task<TabRuntime> CreateViewAsync(BrowserTab tab)
    {
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Palette.Background,
            AccessibleName = "Содержимое страницы" };
        var runtime = new TabRuntime(tab, view);
        _runtimes.Add(tab.Id, runtime);
        _viewport.Controls.Add(view);
        view.BringToFront();
        try
        {
            // One environment/profile shared across tabs. Chromium is started by the first website only.
            _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataPath, "WebView2"),
                EnvironmentOptions());
            if (!IsAlive(runtime)) return runtime;
            await view.EnsureCoreWebView2Async(_environment);
            if (!IsAlive(runtime)) return runtime;
            ConfigureView(runtime);
            await ConfigureActivityTrackingAsync(runtime);
            await ConfigurePasswordAutofillAsync(runtime);
            if (!IsAlive(runtime)) return runtime;
            view.CoreWebView2.Navigate(tab.CurrentUrl!);
            return runtime;
        }
        catch { DestroyView(tab.Id); _home.Visible = true; throw; }
    }

    private void ConfigureView(TabRuntime runtime)
    {
        var view = runtime.View;
        var tab = runtime.Tab;
        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = true;
        SetPageColorScheme(core);
        ConfigurePermissions(runtime);
        view.GotFocus += (_, _) => CloseOverlay();
        core.FaviconChanged += (_, _) => RunUi(async () =>
        {
            if (!IsAlive(runtime) || !AddressParser.TryWebUrl(core.Source, out _)) return;
            try { using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png); Favicons.Save(core.Source, stream); _tabs.Invalidate(); }
            catch (Exception ex) when (ex is COMException or ArgumentException or IOException or OutOfMemoryException) { }
        });
        view.KeyDown += (_, e) => { if (HandleShortcut(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; } };
        core.NavigationStarting += (_, e) =>
        {
            if (!IsAlive(runtime)) return;
            if (tab == ActiveTab) { DismissPasswordPopup(); CloseOverlay(); }
            if (!IsWebAddress(e.Uri))
            {
                e.Cancel = true;
                runtime.ProgrammaticNavigation = runtime.Loading = false;
                if (tab == ActiveTab) _status.Text = "Этот тип адреса не поддерживается.";
                OnRuntimeChanged(runtime);
                return;
            }
            runtime.TraversingHistory = e.NavigationKind == CoreWebView2NavigationKind.BackOrForward;
            if (!runtime.TraversingHistory)
            {
                if (runtime.ProgrammaticNavigation || e.IsRedirected) tab.ReplaceCurrent(e.Uri);
                else VisitRuntime(runtime, e.Uri);
            }
            runtime.ProgrammaticNavigation = false;
            runtime.NavigationId = e.NavigationId;
            runtime.Loading = true;
            runtime.PendingRequests.Clear();
            OnRuntimeChanged(runtime);
        };
        core.SourceChanged += (_, e) =>
        {
            if (!IsAlive(runtime) || !IsWebAddress(core.Source)) return;
            if (runtime.Loading && e.IsNewDocument && !runtime.TraversingHistory) tab.ReplaceCurrent(core.Source);
            else RunUi(() => SynchronizeNativeHistoryAsync(runtime));
            OnRuntimeChanged(runtime);
        };
        core.HistoryChanged += (_, _) => { if (IsAlive(runtime)) RunUi(() => SynchronizeNativeHistoryAsync(runtime)); };
        core.DocumentTitleChanged += (_, _) =>
        {
            if (!IsAlive(runtime)) return;
            string title = core.DocumentTitle;
            tab.Title = string.IsNullOrWhiteSpace(title) ? new Uri(core.Source).Host : title[..Math.Min(300, title.Length)];
            OnRuntimeChanged(runtime);
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (!IsAlive(runtime) || e.NavigationId != runtime.NavigationId) return;
            runtime.Loading = false;
            if (tab == ActiveTab)
                _status.Text = e.IsSuccess ? "Готово" : $"Страница не загружена: {e.WebErrorStatus}. Нажмите ↻ для повтора.";
            try
            {
                await SynchronizeNativeHistoryAsync(runtime);
                runtime.TraversingHistory = false;
                if (e.IsSuccess && IsAlive(runtime) && (tab.ScrollX > 0 || tab.ScrollY > 0))
                {
                    double x = tab.ScrollX, y = tab.ScrollY;
                    tab.ScrollX = tab.ScrollY = 0;
                    await core.ExecuteScriptAsync($"scrollTo({x.ToString(CultureInfo.InvariantCulture)},{y.ToString(CultureInfo.InvariantCulture)})");
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
            if (IsAlive(runtime)) { OnRuntimeChanged(runtime); UpdateMemory(); }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsAlive(runtime) && tab == ActiveTab && IsWebAddress(e.Uri))
            {
                string uri = e.Uri;
                BeginInvoke(() => RunUi(() => OpenTabAsync(uri)));
            }
        };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.DownloadStarting += (_, e) =>
        {
            runtime.DownloadCount++;
            var download = e.DownloadOperation;
            EventHandler<object>? changed = null;
            changed = (_, _) =>
            {
                if (download.State == CoreWebView2DownloadState.InProgress) return;
                download.StateChanged -= changed;
                runtime.DownloadCount = Math.Max(0, runtime.DownloadCount - 1);
            };
            download.StateChanged += changed;
            if (tab == ActiveTab) _status.Text = "Скачивание файла · вкладка защищена от автоматической выгрузки.";
        };
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (IsAlive(runtime) && tab == ActiveTab) SetSiteFullscreen(core.ContainsFullScreenElement);
        };
        core.ProcessFailed += (_, e) =>
        {
            if (!IsAlive(runtime)) return;
            Program.LogError(DataPath, new InvalidOperationException("WebView2 process failed: " + e.ProcessFailedKind));
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
            BeginInvoke(() =>
            {
                if (!IsAlive(runtime)) return;
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) { DestroyAllViews(); _environment = null; }
                else DestroyView(tab.Id);
                if (tab == ActiveTab) { _home.Visible = true; _status.Text = "Движок страницы завершился. Нажмите ↻ для повтора."; }
                RefreshChrome();
            });
        };
    }

    /// <summary>Native entry IDs distinguish pushState, replaceState and browser history traversal.</summary>
    private async Task SynchronizeNativeHistoryAsync(TabRuntime runtime)
    {
        long revision = ++runtime.HistoryRevision;
        try
        {
            string json = await runtime.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}");
            if (!IsAlive(runtime) || revision != runtime.HistoryRevision) return;
            using var document = JsonDocument.Parse(json);
            int index = document.RootElement.GetProperty("currentIndex").GetInt32();
            var entry = document.RootElement.GetProperty("entries")[index];
            string? url = entry.GetProperty("url").GetString();
            if (url is null || !IsWebAddress(url) || url == "about:blank") return;
            int id = entry.GetProperty("id").GetInt32();
            var tab = runtime.Tab;
            if ((runtime.Loading && !runtime.TraversingHistory) || runtime.NativeIndex < 0)
                runtime.NativeEntries[id] = tab.HistoryIndex;
            else if (runtime.NativeEntries.TryGetValue(id, out int modelIndex) && modelIndex >= 0 && modelIndex < tab.History.Count)
            {
                tab.HistoryIndex = modelIndex;
                tab.ReplaceCurrent(url);
            }
            else
            {
                VisitRuntime(runtime, url);
                runtime.NativeEntries[id] = tab.HistoryIndex;
            }
            runtime.NativeIndex = index;
            OnRuntimeChanged(runtime);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException or ArgumentOutOfRangeException) { }
    }

    private static void VisitRuntime(TabRuntime runtime, string url)
    {
        var tab = runtime.Tab;
        if (tab.CurrentUrl == url) return;
        int oldIndex = tab.HistoryIndex;
        bool willTrim = oldIndex == 79 && tab.History.Count == 80;
        // A forward branch is discarded by Visit; entry IDs pointing into it become invalid too.
        foreach (var (id, index) in runtime.NativeEntries.ToArray())
        {
            if (index > oldIndex || (willTrim && index == 0)) runtime.NativeEntries.Remove(id);
            else if (willTrim) runtime.NativeEntries[id] = index - 1;
        }
        tab.Visit(url);
    }

    private void OnRuntimeChanged(TabRuntime runtime)
    {
        if (!IsAlive(runtime)) return;
        if (runtime.Tab == ActiveTab) RefreshChrome();
        else { _tabs.Invalidate(); _saveTimer.Stop(); _saveTimer.Start(); }
    }

    private static bool IsWebAddress(string value) => value == "about:blank" || AddressParser.TryWebUrl(value, out _);

    private static void SetPageColorScheme(CoreWebView2 core) => core.Profile.PreferredColorScheme =
        ThemeSettings.Current == BrowserTheme.Light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;

    private void SetMemoryTargets()
    {
        foreach (var runtime in _runtimes.Values)
        {
            try
            {
                if (runtime.View.CoreWebView2 is { } core)
                    core.MemoryUsageTargetLevel = runtime.Tab != ActiveTab || WindowState == FormWindowState.Minimized
                        ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch (Exception ex) when (ex is NotImplementedException or COMException or InvalidOperationException) { }
        }
    }
}
