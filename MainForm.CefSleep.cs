using CefSharp;
using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private DateTimeOffset _inactiveSinceUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastNetworkActivityUtc = DateTimeOffset.UtcNow;
    private bool IsBrowserForeground => Form.ActiveForm == this || ContainsFocus;

    private void ResumeActivePage()
    {
        if (_closing || ActiveTab.CurrentUrl is null or "about:blank") return;
        if (_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) && CefIsAlive(runtime))
            runtime.View.Focus();
    }

    private void SetMemoryTargets()
    {
        // CEF GPU and native raster switches are set before Cef.Initialize in Program.Main.
        // Per-page switches are applied by ActivePageMemoryController.
    }

    private static bool CefHasProtectedActivity(CefTabRuntime runtime) =>
        runtime.Loading;

    private async Task<bool> HibernateCefTabAsync(CefTabRuntime runtime, bool manual)
    {
        if (!CefIsAlive(runtime) || runtime.Tab == ActiveTab || CefHasProtectedActivity(runtime))
            return false;
        if (!runtime.View.CanExecuteJavascriptInMainFrame) return false;
        try
        {
            // Never discard a page containing unsaved form fields, running audio/video,
            // downloads or active network interactions without an explicit site confirmation.
            var response = await runtime.View.EvaluateScriptAsync(TabHibernationManager.SafeToHibernateScript);
            if (!response.Success || response.Result is not true) return false;
            var scroll = await runtime.View.EvaluateScriptAsync("JSON.stringify([scrollX,scrollY])");
            if (scroll.Success && scroll.Result is string json)
            {
                var coords = JsonSerializer.Deserialize<double[]>(json);
                if (coords is { Length: 2 } && coords.All(double.IsFinite))
                {
                    runtime.Tab.ScrollX = Math.Clamp(coords[0], 0, 100_000_000);
                    runtime.Tab.ScrollY = Math.Clamp(coords[1], 0, 100_000_000);
                }
            }
            if (!CefIsAlive(runtime) || runtime.Tab == ActiveTab || runtime.Loading) return false;
            DestroyCefView(runtime.Tab.Id);
            _hibernatedIds.Add(runtime.Tab.Id);
            _hibernateCount++;
            await SaveSessionAsync();
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ObjectDisposedException)
        { Program.LogError(DataPath, error); return false; }
    }

    private async Task HibernateIdleBackgroundTabsAsync(DateTimeOffset now)
    {
        if (!_preferences.AutomaticHibernate || _preferences.MemoryMode == MemoryMode.Performance) return;
        long free = AdaptiveResourceManager.AvailableRamBytes();
        foreach (var runtime in _cefRuntimes.Values.Where(r => r.Tab != ActiveTab)
            .OrderBy(r => r.LastUsedUtc).ToArray())
        {
            if (Uri.TryCreate(runtime.Tab.CurrentUrl, UriKind.Absolute, out var site) &&
                _preferences.NeverHibernateHosts.Contains(site.Host, StringComparer.OrdinalIgnoreCase))
                continue;
            if (!CefIsAlive(runtime) || CefHasProtectedActivity(runtime)) continue;
            if (!AdaptiveResourceManager.ShouldHibernate(_preferences.MemoryMode,
                runtime.LastUsedUtc, now, _cefRuntimes.Count, free, _preferences.UnloadAfterMinutes))
                continue;
            await HibernateCefTabAsync(runtime, manual: false);
        }
        _tabs.Invalidate();
    }

    internal Task FreezeBackgroundForTestAsync() => UnloadBackgroundTabsAsync();
    internal Task HibernateBackgroundForTestAsync() => TransitionAsync(() =>
        HibernateIdleBackgroundTabsAsync(DateTimeOffset.UtcNow.AddHours(2)));
}
