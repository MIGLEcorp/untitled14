using System.Diagnostics;
using System.Text.Json;
using CefSharp;
using CefSharp.WinForms;

namespace MBrowser;

/// <summary>
/// Embedded CEF 154 rendering within the MBrowser shell. The custom native
/// libcef.dll must always ship with the matching CEF resources, locales and
/// V8 snapshot from the same build; mixing stock and custom runtimes is unsafe.
/// </summary>
internal sealed partial class MainForm
{
    private sealed class CefTabRuntime(BrowserTab tab, ChromiumWebBrowser view, CefNavigationHandler navigationHandler)
    {
        internal BrowserTab Tab { get; } = tab;
        internal ChromiumWebBrowser View { get; } = view;
        internal CefNavigationHandler NavigationHandler { get; } = navigationHandler;
        internal bool MainFrameReady { get; set; }
        internal long MainFrameStartedTimestamp { get; set; }
        internal bool Loading { get; set; }
        internal string MemoryAgentScript { get; set; } = "";
        internal DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
        internal DateTimeOffset? LoadStartedUtc { get; set; } = DateTimeOffset.UtcNow;
        internal DateTimeOffset? RestoreStartedUtc { get; set; }
    }

    private readonly Dictionary<Guid, CefTabRuntime> _cefRuntimes = [];
    private bool _cefEngine;
    private bool _cefCustomLibCef;

    private bool CefIsAlive(CefTabRuntime r) =>
        !_closing && !r.View.IsDisposed && _cefRuntimes.GetValueOrDefault(r.Tab.Id) == r;

    private CefTabRuntime CreateCefView(BrowserTab tab)
    {
        // Initialization is performed on the main UI thread in Program.Main.
        var browser = new ChromiumWebBrowser(tab.CurrentUrl ?? "about:blank")
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Chromium Embedded Framework page"
        };
        browser.DownloadHandler = new CefDownloadsHandler(this, tab.Id);
        browser.PermissionHandler = new CefPermissionsHandler(this, tab.Id);
        var navigationHandler = new CefNavigationHandler(this, tab);
        browser.RequestHandler = navigationHandler;
        browser.LifeSpanHandler = new CefPopupHandler(this);
        browser.DisplayHandler = new CefDisplayHandler(this);
        browser.MenuHandler = new CefImageContextMenuHandler(this);
        var runtime = new CefTabRuntime(tab, browser, navigationHandler)
        {
            Loading = true,
            MemoryAgentScript = _smoke || ActivePageMemoryController.HasEnabledPolicies(_preferences)
                ? ActivePageMemoryController.Script(_preferences) : ""
        };
        if (_hibernatedIds.Remove(tab.Id)) runtime.RestoreStartedUtc = DateTimeOffset.UtcNow;
        _cefRuntimes.Add(tab.Id, runtime);
        ConfigureCefPasswordAutofill(runtime);
        _viewport.Controls.Add(browser);
        browser.BringToFront();

        void OnUi(Action update)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() => { if (CefIsAlive(runtime)) update(); });
            }
            catch (InvalidOperationException) { }
        }

        browser.LoadingStateChanged += (_, e) => OnUi(() =>
        {
            runtime.Loading = e.IsLoading;
            if (e.IsLoading)
                runtime.LoadStartedUtc ??= DateTimeOffset.UtcNow;
            else if (runtime.LoadStartedUtc is { } loadStarted)
            {
                runtime.LoadStartedUtc = null;
                var duration = DateTimeOffset.UtcNow - loadStarted;
                if (duration >= TimeSpan.FromSeconds(2))
                {
                    RecordSlowCefNavigation(runtime.View.Address, duration);
                    _ = RecordCefNavigationTimingAsync(runtime, duration, "all-resources-idle");
                }
            }
            if (!e.IsLoading && runtime.RestoreStartedUtc is { } restored)
            {
                _restoreCount++;
                _restoreTotalMilliseconds += (DateTimeOffset.UtcNow - restored).TotalMilliseconds;
                runtime.RestoreStartedUtc = null;
            }
            // Reapply page policies once the main-frame V8 context is ready.
            // Native CEF builds can deliver FrameLoadEnd before the final
            // renderer context is able to execute injected JavaScript.
            if (!e.IsLoading && runtime.MemoryAgentScript.Length > 0 &&
                runtime.View.IsBrowserInitialized && runtime.View.CanExecuteJavascriptInMainFrame)
            {
                try { runtime.View.ExecuteScriptAsync(runtime.MemoryAgentScript); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            if (tab == ActiveTab)
            {
                _status.Text = e.IsLoading
                    ? runtime.MainFrameReady ? "CEF: страница открыта · фоновые запросы" : "CEF: загружается…"
                    : "CEF: готово";
                RefreshChrome();
            }
        });
        browser.AddressChanged += (_, e) => OnUi(() =>
        {
            if (!string.Equals(e.Address, "about:blank", StringComparison.OrdinalIgnoreCase) &&
                AddressParser.TryWebUrl(e.Address, out string resolved))
            {
                if (tab.CurrentUrl != resolved) tab.Visit(resolved);
            }
            if (tab == ActiveTab) RefreshChrome();
            else _tabs.Invalidate();
        });
        browser.TitleChanged += (_, e) => OnUi(() =>
        {
            if (!string.IsNullOrWhiteSpace(e.Title))
                tab.Title = e.Title[..Math.Min(300, e.Title.Length)];
            if (tab == ActiveTab) RefreshChrome();
            else _tabs.Invalidate();
        });
        browser.FrameLoadStart += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            runtime.MainFrameReady = false;
            runtime.MainFrameStartedTimestamp = Stopwatch.GetTimestamp();
            if (runtime.NavigationHandler.ResourceTracingEnabled)
                _ = LogPendingResourcesAfterDelayAsync(runtime);
            if (runtime.MemoryAgentScript.Length > 0)
            {
                try { e.Frame.ExecuteJavaScriptAsync(runtime.MemoryAgentScript); }
                catch (ObjectDisposedException) { }
            }
        };
        // FrameLoadStart can precede V8 context creation; retry after load.
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            try
            {
                if (runtime.MemoryAgentScript.Length > 0)
                    e.Frame.ExecuteJavaScriptAsync(runtime.MemoryAgentScript);
                e.Frame.ExecuteJavaScriptAsync(AutofillPageScripts.CefObserver);
                e.Frame.ExecuteJavaScriptAsync(TabHibernationManager.ActivityTrackerScript);
                if (tab.ScrollX > 0 || tab.ScrollY > 0)
                    e.Frame.ExecuteJavaScriptAsync("scrollTo(" + tab.ScrollX.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + tab.ScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            }
            catch (ObjectDisposedException) { }
            var frameElapsed = runtime.MainFrameStartedTimestamp == 0
                ? TimeSpan.Zero : Stopwatch.GetElapsedTime(runtime.MainFrameStartedTimestamp);
            OnUi(() =>
            {
                runtime.MainFrameReady = true;
                _ = RecordCefNavigationTimingAsync(runtime, frameElapsed, "main-frame-ready");
                if (tab == ActiveTab)
                {
                    if (runtime.Loading) _status.Text = "CEF: страница открыта · фоновые запросы";
                    RunUi(() => OfferCefPasswordsAsync(runtime, false));
                }
            });
        };
        browser.LoadError += (_, e) =>
        {
            if (e.ErrorCode == CefErrorCode.Aborted) return;
            OnUi(() => { if (tab == ActiveTab) _status.Text = "CEF: " + e.ErrorText; });
        };
        browser.KeyDown += (_, e) =>
        {
            if (HandleShortcut(e.KeyData)) e.Handled = e.SuppressKeyPress = true;
        };
        return runtime;
    }

    private void RecordSlowCefNavigation(string? url, TimeSpan elapsed)
    {
        // Host only: avoid storing paths, search terms, private URLs or credentials.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return;
        try
        {
            File.AppendAllText(Path.Combine(DataPath, "cef-slow-navigation.log"),
                $"{DateTimeOffset.UtcNow:O} host={uri.Host} elapsed_ms={elapsed.TotalMilliseconds:F0}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Program.LogError(DataPath, error); }
    }

    private async Task LogPendingResourcesAfterDelayAsync(CefTabRuntime runtime)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        // CefIsAlive accesses the UI-owned tab dictionary; marshal to the UI thread.
        if (!IsDisposed && IsHandleCreated)
            DispatchCefUi(() =>
            {
                if (CefIsAlive(runtime)) runtime.NavigationHandler.LogPendingResources();
                return Task.CompletedTask;
            });
    }

    private async Task RecordCefNavigationTimingAsync(
        CefTabRuntime runtime, TimeSpan elapsed, string phase)
    {
        // Main-frame milestones are captured separately from CEF's all-resources
        // idle state. Resource paths and query strings are never written to disk.
        if (!CefIsAlive(runtime) || !runtime.View.IsBrowserInitialized ||
            !runtime.View.CanExecuteJavascriptInMainFrame ||
            !Uri.TryCreate(runtime.View.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return;
        const string script = "(() => { const n=performance.getEntriesByType('navigation')[0];" +
            "const r=performance.getEntriesByType('resource');" +
            "const t=v=>Number.isFinite(v)?Math.round(v):-1;" +
            "const slow=r.filter(x=>x.duration>=1000).sort((a,b)=>b.duration-a.duration).slice(0,8).map(x=>{" +
            "let h='unknown';try{h=new URL(x.name).host}catch{}" +
            "return {host:h,type:x.initiatorType,ms:t(x.duration)}});" +
            "return JSON.stringify({ttfb:t(n?.responseStart),dom:t(n?.domContentLoadedEventEnd)," +
            "onload:t(n?.loadEventEnd),now:t(performance.now()),resources:r.length," +
            "bytes:r.reduce((s,x)=>s+(x.transferSize||0),0),slow}); })()";
        try
        {
            var response = await runtime.View.EvaluateScriptAsync(script);
            if (!response.Success || response.Result is not string json || json.Length > 4096) return;
            using var parsed = JsonDocument.Parse(json);
            long Metric(string key) => parsed.RootElement.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : -1;
            string slow = "";
            if (parsed.RootElement.TryGetProperty("slow", out var slowItems) &&
                slowItems.ValueKind == JsonValueKind.Array)
            {
                slow = string.Join(";", slowItems.EnumerateArray().Select(item =>
                {
                    string host = item.TryGetProperty("host", out var h) ? h.GetString() ?? "unknown" : "unknown";
                    string type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
                    long ms = item.TryGetProperty("ms", out var m) && m.TryGetInt64(out var value) ? value : -1;
                    return host + ":" + type + ":" + ms;
                }));
            }
            string line = $"{DateTimeOffset.UtcNow:O} phase={phase} host={uri.Host} " +
                $"phase_ms={elapsed.TotalMilliseconds:F0} ttfb_ms={Metric("ttfb")} " +
                $"dom_ms={Metric("dom")} onload_ms={Metric("onload")} " +
                $"browser_elapsed_ms={Metric("now")} resources={Metric("resources")} " +
                $"transfer_bytes={Metric("bytes")} slow=[{slow}]";
            File.AppendAllText(Path.Combine(DataPath, "cef-navigation-metrics.log"),
                line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ObjectDisposedException or InvalidOperationException or JsonException)
        { Program.LogError(DataPath, error); }
    }

    private Task ShowCefActiveTabAsync()
    {
        CloseOverlay();
        DismissPasswordPopup();
        foreach (var runtime in _cefRuntimes.Values)
        {
            if (runtime.Tab != ActiveTab && runtime.View.Visible)
            {
                runtime.LastUsedUtc = DateTimeOffset.UtcNow;
                runtime.View.Visible = false;
            }
        }
        bool home = ActiveTab.CurrentUrl is null or "about:blank";
        _home.Visible = home;
        if (!home) ThemeMedia.Release(ThemeArea.Home);
        UpdateChromeLayout();
        if (home)
        {
            ActiveTab.Title = "Новая вкладка";
            _home.BringToFront();
            _status.Text = "CEF: новая вкладка";
        }
        else
        {
            if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime))
                runtime = CreateCefView(ActiveTab);
            if (!CefIsAlive(runtime)) return Task.CompletedTask;
            runtime.LastUsedUtc = DateTimeOffset.UtcNow;
            runtime.View.Visible = true;
            runtime.View.BringToFront();
            runtime.View.Focus();
            _status.Text = runtime.Loading ? "CEF: загрузка..." : "CEF: готово";
        }
        RefreshChrome();
        UpdateMemory();
        return Task.CompletedTask;
    }

    private bool DestroyCefView(Guid id)
    {
        if (!_cefRuntimes.Remove(id, out var runtime)) return false;
        _viewport.Controls.Remove(runtime.View);
        runtime.View.Dispose();
        return true;
    }

    private void DestroyAllCefViews()
    {
        foreach (var id in _cefRuntimes.Keys.ToArray()) DestroyCefView(id);
    }

    internal async Task<string> CefFaceDetectionSmokeResultForTestAsync()
    {
        if (!_cefEngine || !_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ||
            !CefIsAlive(runtime) || !runtime.View.IsBrowserInitialized ||
            !runtime.View.CanExecuteJavascriptInMainFrame)
            return "no-active-javascript-browser";
        try
        {
            var response = await runtime.View.EvaluateScriptAsync(
                "document.documentElement.dataset.faceDetectionSmoke || 'pending'");
            string result = response.Success
                ? response.Result?.ToString() ?? "null" : "failed:" + response.Message;
            try { File.WriteAllText(Path.Combine(DataPath, "cef-face-detection-smoke.txt"), result); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Program.LogError(DataPath, error); }
            return result;
        }
        catch (Exception error) { return "error:" + error.GetType().Name; }
    }

    internal (bool BrowserInitialized, string Address, int LiveTabs) CefDiagnosticsForTest()
    {
        if (!_cefEngine || !_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime))
            return (false, "", 0);
        return (runtime.View.IsBrowserInitialized, runtime.View.Address ?? "",
            _cefRuntimes.Count);
    }

    internal async Task<bool> CefMemoryAgentReadyForTestAsync()
    {
        if (!_cefEngine || !_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ||
            !CefIsAlive(runtime) || !runtime.View.IsBrowserInitialized ||
            !runtime.View.CanExecuteJavascriptInMainFrame)
            return false;
        try
        {
            var response = await runtime.View.EvaluateScriptAsync(
                "typeof globalThis.__mbrowser_memory_lab_agent_v1?.configure === 'function'");
            if (!response.Success || response.Result is not true)
            {
                try
                {
                    File.AppendAllText(Path.Combine(DataPath, "cef-agent-diagnostics.log"),
                        $"{DateTimeOffset.Now:u} ready={response.Success} result={response.Result} error={response.Message} url={runtime.View.Address} canJS={runtime.View.CanExecuteJavascriptInMainFrame}{Environment.NewLine}");
                }
                catch (IOException) { }
            }
            return response.Success && response.Result is true;
        }
        catch (Exception ex)
        {
            Program.LogError(DataPath, ex);
            return false;
        }
    }

    internal async Task<bool> CefExtensionSmokeReadyForTestAsync()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var current) ||
            !CefIsAlive(current) || !current.View.CanExecuteJavascriptInMainFrame)
            return false;
        try
        {
            var response = await current.View.EvaluateScriptAsync(
                "document.documentElement.dataset.mbrowserMv3Smoke === 'loaded'");
            return response.Success && response.Result is true;
        }
        catch (Exception error) { Program.LogError(DataPath, error); return false; }
    }

    internal async Task<bool> CefExtensionWorkerReadyForTestAsync()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var current) ||
            !CefIsAlive(current) || !current.View.CanExecuteJavascriptInMainFrame)
            return false;
        try
        {
            var response = await current.View.EvaluateScriptAsync(
                "document.documentElement.dataset.mbrowserMv3Worker === 'ok'");
            return response.Success && response.Result is true;
        }
        catch (Exception error) { Program.LogError(DataPath, error); return false; }
    }

    internal async Task<string> CefExtensionIdForTestAsync()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var current) ||
            !CefIsAlive(current) || !current.View.CanExecuteJavascriptInMainFrame)
            return "";
        try
        {
            var response = await current.View.EvaluateScriptAsync(
                "document.documentElement.dataset.mbrowserMv3Id || ''");
            return response.Success ? response.Result as string ?? "" : "";
        }
        catch (Exception error) { Program.LogError(DataPath, error); return ""; }
    }

    private bool TryCefNavigate(string url)
    {
        if (!_cefEngine || !_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ||
            !CefIsAlive(runtime)) return false;
        runtime.View.LoadUrl(url);
        return true;
    }

    private bool TryCefReload()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) || !CefIsAlive(runtime))
            return false;
        if (runtime.Loading) runtime.View.Stop();
        else runtime.View.Reload();
        RefreshChrome();
        return true;
    }

    private bool TryCefBack()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ||
            !CefIsAlive(runtime) || !runtime.View.CanGoBack) return false;
        runtime.View.Back();
        return true;
    }

    private bool TryCefForward()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ||
            !CefIsAlive(runtime) || !runtime.View.CanGoForward) return false;
        runtime.View.Forward();
        return true;
    }

    private void ApplyCefActivePageSettings()
    {
        bool enable = _smoke || ActivePageMemoryController.HasEnabledPolicies(_preferences);
        string script = enable ? ActivePageMemoryController.Script(_preferences) : "";
        string update = enable ? script : ActivePageMemoryController.LiveUpdate(_preferences);
        foreach (var runtime in _cefRuntimes.Values)
        {
            if (!CefIsAlive(runtime)) continue;
            runtime.MemoryAgentScript = script;
            if (runtime.View.IsBrowserInitialized)
            {
                try { runtime.View.ExecuteScriptAsync(update); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private void ShowCefMemorySettings()
    {
        ShowMenuPage("CEF 154 — экономия RAM", rows =>
        {
            rows("Изменения требуют закрыть и заново открыть режим CEF", () => { });
            rows("Меньше предварительных тайлов: " +
                (_preferences.NativeSmallerRasterArea ? "ВКЛ" : "ВЫКЛ"), () =>
            {
                _preferences.NativeSmallerRasterArea = !_preferences.NativeSmallerRasterArea;
                _preferences.Save(DataPath);
                ShowCefMemorySettings();
            });
            rows("Очищать тайлы в простое: " +
                (_preferences.NativeReclaimTilesWhenIdle ? "ВКЛ" : "ВЫКЛ"), () =>
            {
                _preferences.NativeReclaimTilesWhenIdle = !_preferences.NativeReclaimTilesWhenIdle;
                _preferences.Save(DataPath);
                ShowCefMemorySettings();
            });
            rows("Освобождать старые тайлы: " +
                (_preferences.NativeReclaimOldPrepaintTiles ? "ВКЛ" : "ВЫКЛ"), () =>
            {
                _preferences.NativeReclaimOldPrepaintTiles = !_preferences.NativeReclaimOldPrepaintTiles;
                _preferences.Save(DataPath);
                ShowCefMemorySettings();
            });
            rows("— Вкладка: изменения применяются сразу —", () => { });
            rows("Отложенная загрузка изображений: " +
                (_preferences.LazyOffscreenImages ? "ВКЛ" : "ВЫКЛ"), () =>
            {
                _preferences.LazyOffscreenImages = !_preferences.LazyOffscreenImages;
                _preferences.Save(DataPath);
                ApplyCefActivePageSettings();
                ShowCefMemorySettings();
            });
            rows("Отложенная загрузка невидимого видео: " +
                (_preferences.DeferOffscreenVideo ? "ВКЛ" : "ВЫКЛ"), () =>
            {
                _preferences.DeferOffscreenVideo = !_preferences.DeferOffscreenVideo;
                _preferences.Save(DataPath);
                ApplyCefActivePageSettings();
                ShowCefMemorySettings();
            });
            rows("ЭКСПЕРИМЕНТ: content-visibility: " +
                (_preferences.ExperimentalContentVisibility ? "ВКЛ" : "ВЫКЛ"), () =>
            {
                _preferences.ExperimentalContentVisibility = !_preferences.ExperimentalContentVisibility;
                _preferences.Save(DataPath);
                ApplyCefActivePageSettings();
                ShowCefMemorySettings();
            });
            if (_cefCustomLibCef)
                rows("Компактный кэш изображений: " +
                    (_preferences.NativeCompactDecodedImageCache ? "ВКЛ" : "ВЫКЛ"), () =>
                {
                    _preferences.NativeCompactDecodedImageCache = !_preferences.NativeCompactDecodedImageCache;
                    _preferences.Save(DataPath);
                    ShowCefMemorySettings();
                });
            else
                rows("Компактный кэш изображений: нужен свой libcef (ещё нет)", () => { });
            rows("Запрошенные флаги: " + CefMemoryFeatures.CommandLine(_preferences, _cefCustomLibCef), () => { });
            rows("Применить: перезапусти MBrowser", () => { });
        });
    }

    private void UpdateCefTabVisibility()
    {
        if (!_cefEngine) return;
        _tabs.IsTabLoaded = id => _cefRuntimes.TryGetValue(id, out var r) && CefIsAlive(r);
    }
}
