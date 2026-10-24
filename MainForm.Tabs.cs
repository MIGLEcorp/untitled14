using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace Feather;

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
        DismissPasswordPopup();
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
            runtime.View.Visible = true;
            runtime.View.BringToFront();
            runtime.View.Focus();
            _status.Text = runtime.Loading ? "Загрузка страницы…" : "Готово";
        }
        SetMemoryTargets();
        await EvictOverBudgetAsync();
        RefreshChrome();
        UpdateMemory();
    }

    internal Task UnloadIdleTabsAsync(DateTimeOffset now) => TransitionAsync(async () =>
    {
        foreach (var runtime in _runtimes.Values.ToArray())
        {
            if (TabUnloadPolicy.ShouldUnload(runtime.Tab == ActiveTab, HasProtectedActivity(runtime),
                runtime.LastUsedUtc, now, TimeSpan.FromMinutes(_preferences.UnloadAfterMinutes)))
                await TryUnloadAsync(runtime);
        }
        await EvictOverBudgetAsync();
        RefreshChrome();
        UpdateMemory();
    });

    internal Task UnloadBackgroundTabsAsync() => TransitionAsync(async () =>
    {
        foreach (var runtime in _runtimes.Values.Where(r => r.Tab != ActiveTab).ToArray())
            await TryUnloadAsync(runtime);
        RefreshChrome();
        UpdateMemory();
    });

    private async Task EvictOverBudgetAsync()
    {
        foreach (var runtime in _runtimes.Values.Where(r => r.Tab != ActiveTab).OrderBy(r => r.LastUsedUtc).ToArray())
        {
            if (LiveViewCount <= _preferences.MaximumResidentTabs) break;
            await TryUnloadAsync(runtime);
        }
    }

    private bool _budgetEvictionQueued;

    private void QueueBudgetEviction()
    {
        if (_budgetEvictionQueued || _closing || LiveViewCount <= _preferences.MaximumResidentTabs) return;
        _budgetEvictionQueued = true;
        RunUi(async () =>
        {
            try
            {
                await TransitionAsync(async () =>
                {
                    await EvictOverBudgetAsync();
                    _tabs.Invalidate();
                    UpdateMemory();
                });
            }
            finally { _budgetEvictionQueued = false; }
        });
    }

    private static bool HasProtectedActivity(TabRuntime runtime)
    {
        try { return runtime.Loading || runtime.DownloadCount > 0 || runtime.View.CoreWebView2.IsDocumentPlayingAudio; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return true; }
    }

    private async Task TryUnloadAsync(TabRuntime runtime)
    {
        if (runtime.Tab == ActiveTab || HasProtectedActivity(runtime)) return;
        try
        {
            // Return only a boolean and scroll coordinates. Form contents never leave the page.
            const string snapshot = """
                (() => {
                  const dirty = doc => {
                    if(doc.querySelector('[contenteditable]:not([contenteditable="false"])'))return true;
                    if([...doc.querySelectorAll('input,textarea,select')].some(e=>{
                      if(e.tagName==='SELECT')return [...e.options].some(o=>o.selected!==o.defaultSelected);
                      if(['hidden','button','submit','reset','image'].includes(e.type))return false;
                      return ['checkbox','radio'].includes(e.type)?e.checked!==e.defaultChecked:e.value!==e.defaultValue;
                    }))return true;
                    return [...doc.querySelectorAll('iframe,frame')].some(f=>{
                      try { return !f.contentDocument || dirty(f.contentDocument); } catch { return true; }
                    });
                  };
                  return {x:scrollX,y:scrollY,dirty:dirty(document)};
                })()
                """;
            string json = await runtime.View.CoreWebView2.ExecuteScriptAsync(snapshot);
            using var state = JsonDocument.Parse(json);
            if (state.RootElement.GetProperty("dirty").GetBoolean()) return;
            runtime.Tab.ScrollX = state.RootElement.GetProperty("x").GetDouble();
            runtime.Tab.ScrollY = state.RootElement.GetProperty("y").GetDouble();
            if (runtime.Tab != ActiveTab && !HasProtectedActivity(runtime)) DestroyView(runtime.Tab.Id);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
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
