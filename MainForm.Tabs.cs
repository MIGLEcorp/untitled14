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
            if (!TryCefNavigate(url)) await ShowCefActiveTabAsync();
            RefreshChrome();
        });
    }

    internal Task OpenTabAsync(string? url = null) => TransitionAsync(async () =>
    {
        if (TabCount >= 100) { _status.Text = "Достигнут предел: 100 вкладок."; return; }
        await LeaveSiteFullscreenAsync();
        var tab = new BrowserTab();
        if (url != null && AddressParser.TryResolve(url, out var resolved)) tab.Visit(resolved);
        _session.Tabs.Add(tab);
        _session.SelectedIndex = TabCount - 1;
        await ShowCefActiveTabAsync();
        if (tab.CurrentUrl is null) FocusAddress();
    });

    internal Task SelectTabAsync(int index) => TransitionAsync(async () =>
    {
        if (index < 0 || index >= TabCount || index == _session.SelectedIndex) return;
        await LeaveSiteFullscreenAsync();
        _session.SelectedIndex = index;
        await ShowCefActiveTabAsync();
    });

    internal Task CloseTabAsync(int index) => TransitionAsync(async () =>
    {
        if (index < 0 || index >= TabCount) return;
        bool wasActive = index == _session.SelectedIndex;
        if (wasActive) await LeaveSiteFullscreenAsync();
        DestroyCefView(_session.Tabs[index].Id);
        _session.Tabs.RemoveAt(index);
        if (TabCount == 0) _session.Tabs.Add(new BrowserTab());
        if (index < _session.SelectedIndex) _session.SelectedIndex--;
        _session.SelectedIndex = Math.Min(_session.SelectedIndex, TabCount - 1);
        if (wasActive) await ShowCefActiveTabAsync();
        else RefreshChrome();
        UpdateMemory();
    });

    internal Task HomeAsync() => TransitionAsync(async () =>
    {
        await LeaveSiteFullscreenAsync();
        DestroyCefView(ActiveTab.Id);
        ActiveTab.Visit("about:blank");
        ActiveTab.ScrollX = ActiveTab.ScrollY = 0;
        await ShowCefActiveTabAsync();
    });

    internal Task GoBackAsync() => TransitionAsync(async () =>
    {
        await LeaveSiteFullscreenAsync();
        if (TryCefBack()) { ActiveTab.Back(); RefreshChrome(); return; }
        if (!ActiveTab.CanGoBack) return;
        ActiveTab.Back();
        DestroyCefView(ActiveTab.Id);
        await ShowCefActiveTabAsync();
    });

    internal Task GoForwardAsync() => TransitionAsync(async () =>
    {
        await LeaveSiteFullscreenAsync();
        if (TryCefForward()) { ActiveTab.Forward(); RefreshChrome(); return; }
        if (!ActiveTab.CanGoForward) return;
        ActiveTab.Forward();
        DestroyCefView(ActiveTab.Id);
        await ShowCefActiveTabAsync();
    });

    private Task ReloadAsync() => TransitionAsync(async () =>
    {
        if (!TryCefReload()) await ShowCefActiveTabAsync();
    });

    private Task ShowActiveTabAsync() => ShowCefActiveTabAsync();

    internal Task UnloadIdleTabsAsync(DateTimeOffset now) => TransitionAsync(() => HibernateIdleBackgroundTabsAsync(now));

    internal Task UnloadBackgroundTabsAsync() => TransitionAsync(async () =>
    {
        foreach (var runtime in _cefRuntimes.Values.Where(r => r.Tab != ActiveTab).ToArray())
            await HibernateCefTabAsync(runtime, manual: true);
        _tabs.Invalidate();
    });

    private void DestroyView(Guid id) => DestroyCefView(id);
    private void DestroyAllViews() => DestroyAllCefViews();
}
