using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace MBrowser;

internal sealed partial class MainForm
{
    internal Task NavigateInputAsync(string input)
    {
        if (!AddressParser.TryResolve(input, out var url))
        {
            _status.Text = "Введите адрес http/https или поисковый запрос.";
            return Task.CompletedTask;
        }
        return TransitionAsync(async () =>
        {
            await LeaveSiteFullscreenAsync();
            ActiveTab.Visit(url);
            ActiveTab.ScrollX = ActiveTab.ScrollY = 0;
            DestroyView(ActiveTab.Id);
            await ShowActiveTabAsync();
        });
    }

    internal Task OpenTabAsync(string? url = null) => TransitionAsync(async () =>
    {
        if (TabCount >= 100) { _status.Text = "Достигнут предел: 100 вкладок."; return; }
        await LeaveSiteFullscreenAsync();
        var tab = new BrowserTab();
        if (url is not null && AddressParser.TryResolve(url, out var resolved)) tab.Visit(resolved);
        _session.Tabs.Add(tab);
        _session.SelectedIndex = TabCount - 1;
        await ShowActiveTabAsync();
        if (tab.CurrentUrl is null) FocusAddress();
    });

    internal Task SelectTabAsync(int index) => TransitionAsync(async () =>
    {
        if (index < 0 || index >= TabCount || index == _session.SelectedIndex) return;
        await LeaveSiteFullscreenAsync();
        _session.SelectedIndex = index;
        await ShowActiveTabAsync();
    });

    internal Task CloseTabAsync(int index) => TransitionAsync(async () =>
    {
        if (index < 0 || index >= TabCount) return;
        bool wasActive = index == _session.SelectedIndex;
        if (wasActive) await LeaveSiteFullscreenAsync();
        DestroyView(_session.Tabs[index].Id);
        _session.Tabs.RemoveAt(index);
        if (TabCount == 0) _session.Tabs.Add(new BrowserTab());
        if (index < _session.SelectedIndex) _session.SelectedIndex--;
        _session.SelectedIndex = Math.Min(_session.SelectedIndex, TabCount - 1);
        if (wasActive) await ShowActiveTabAsync();
        else RefreshChrome();
        UpdateMemory();
    });

    internal Task HomeAsync() => TransitionAsync(async () =>
    {
        await LeaveSiteFullscreenAsync();
        DestroyView(ActiveTab.Id);
        ActiveTab.Visit("about:blank");
        ActiveTab.ScrollX = ActiveTab.ScrollY = 0;
        await ShowActiveTabAsync();
    });

    internal Task GoBackAsync() => TransitionAsync(async () =>
    {
        if (!ActiveTab.CanGoBack) return;
        await LeaveSiteFullscreenAsync();
        ActiveTab.Back();
        DestroyView(ActiveTab.Id);
        ActiveTab.ScrollX = ActiveTab.ScrollY = 0;
        await ShowActiveTabAsync();
    });

    internal Task GoForwardAsync() => TransitionAsync(async () =>
    {
        if (!ActiveTab.CanGoForward) return;
        await LeaveSiteFullscreenAsync();
        ActiveTab.Forward();
        DestroyView(ActiveTab.Id);
        ActiveTab.ScrollX = ActiveTab.ScrollY = 0;
        await ShowActiveTabAsync();
    });

    private Task ReloadAsync() => TransitionAsync(async () =>
    {
        if (ActiveCore is { } core)
        {
            if (IsPageLoading) { core.Stop(); ActiveRuntime!.Loading = false; }
            else core.Reload();
            RefreshChrome();
        }
        else await ShowActiveTabAsync();
    });

    /// <summary>Switching a tab keeps its live page intact. Only idle eviction disposes its control.</summary>
    private async Task ShowActiveTabAsync()
    {
        CloseOverlay();
        DismissPasswordPopup();
        _address.Text = ActiveTab.CurrentUrl is null or "about:blank" ? "" : ActiveTab.CurrentUrl;
        foreach (var runtime in _runtimes.Values)
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
            _status.Text = "Новая вкладка · фоновые страницы отдыхают";
        }
        else
        {
            var runtime = ActiveRuntime;
            if (runtime is null) runtime = await CreateViewAsync(ActiveTab);
            if (_closing || runtime.View.IsDisposed) return;
            runtime.LastUsedUtc = DateTimeOffset.UtcNow;
            runtime.View.CoreWebView2.Resume();
            runtime.View.Visible = true;
            runtime.View.BringToFront();
            runtime.View.Focus();
            _status.Text = runtime.Loading ? "Загрузка страницы…" : "Готово";
        }
        SetMemoryTargets();
        RefreshChrome();
        UpdateMemory();
    }

    // Retained names keep command bindings stable; these operations now suspend, never discard.
    internal Task UnloadIdleTabsAsync(DateTimeOffset now) => FreezeIdleTabsAsync(now);
    internal Task UnloadBackgroundTabsAsync() => TransitionAsync(async () =>
    {
        foreach (var runtime in _runtimes.Values.Where(r => r.Tab != ActiveTab).ToArray())
            await FreezeRuntimeAsync(runtime);
        _tabs.Invalidate();
    });
    private static bool HasProtectedActivity(TabRuntime runtime)
    {
        try { return runtime.Loading || runtime.DownloadCount > 0 || runtime.PendingRequests.Count > 0 || runtime.View.CoreWebView2.IsDocumentPlayingAudio; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return true; }
    }
    private void DestroyView(Guid id)
    {
        if (!_runtimes.Remove(id, out var runtime)) return;
        if (runtime.Tab == ActiveTab) DismissPasswordPopup();
        if (runtime.Tab == ActiveTab && _siteEnteredFullscreen)
        {
            _siteEnteredFullscreen = false;
            if (IsFullScreen) ToggleFullScreen();
        }
        _viewport.Controls.Remove(runtime.View);
        runtime.View.Dispose();
    }

    private void DestroyAllViews()
    {
        foreach (var id in _runtimes.Keys.ToArray()) DestroyView(id);
    }
}
