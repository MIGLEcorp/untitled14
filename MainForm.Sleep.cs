using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private DateTimeOffset _inactiveSinceUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastNetworkActivityUtc = DateTimeOffset.UtcNow;
    private bool IsBrowserForeground => IsHandleCreated && GetAncestor(GetForegroundWindow(), 3) == Handle;

    private Task FreezeIdleTabsAsync(DateTimeOffset now) => TransitionAsync(async () =>
    {
        if (!TabFreezePolicy.ShouldFreeze(IsBrowserForeground, _runtimes.Values.Any(HasProtectedActivity),
            _inactiveSinceUtc, _lastNetworkActivityUtc, now, TimeSpan.FromMinutes(_preferences.UnloadAfterMinutes))) return;
        foreach (var runtime in _runtimes.Values.ToArray()) await FreezeRuntimeAsync(runtime);
        _tabs.Invalidate();
    });

    private async Task FreezeRuntimeAsync(TabRuntime runtime)
    {
        if (!IsAlive(runtime) || HasProtectedActivity(runtime) || runtime.View.CoreWebView2.IsSuspended) return;
        runtime.View.Visible = false;
        try { await runtime.View.CoreWebView2.TrySuspendAsync(); }
        catch (COMException) { }
        if (IsAlive(runtime) && runtime.Tab == ActiveTab && IsBrowserForeground)
        { runtime.View.CoreWebView2.Resume(); runtime.View.Visible = true; }
    }

    private void ResumeActivePage()
    {
        if (_closing || ActiveRuntime is not { } runtime || runtime.View.CoreWebView2 is not { } core) return;
        core.Resume();
        runtime.View.Visible = true;
        _tabs.Invalidate();
    }

    // No artificial working-set trimming: only disposing an idle WebView2 can release its owned resources.
    private async Task HibernateIdleBackgroundTabsAsync(DateTimeOffset now)
    {
        if (!_preferences.AutomaticHibernate || _preferences.MemoryMode == MemoryMode.Performance) return;
        long freeRam = AdaptiveResourceManager.AvailableRamBytes();
        foreach (var runtime in _runtimes.Values.Where(r => r.Tab != ActiveTab)
                     .OrderBy(r => r.LastUsedUtc).ToArray())
        {
            if (Uri.TryCreate(runtime.Tab.CurrentUrl, UriKind.Absolute, out var pageUri) &&
                _preferences.NeverHibernateHosts.Contains(pageUri.Host, StringComparer.OrdinalIgnoreCase)) continue;
            if (!IsAlive(runtime) || HasProtectedActivity(runtime) ||
                !AdaptiveResourceManager.ShouldHibernate(_preferences.MemoryMode, runtime.LastUsedUtc,
                    now, _runtimes.Count, freeRam, _preferences.UnloadAfterMinutes)) continue;
            try
            {
                // Conservative safety policy: never automatically discard forms or live media.
                // Other unobservable JavaScript state may still be lost on restoration.
                if (runtime.View.CoreWebView2.IsSuspended) runtime.View.CoreWebView2.Resume();
                string safe = await runtime.View.CoreWebView2.ExecuteScriptAsync(TabHibernationManager.SafeToHibernateScript);
                if (!IsAlive(runtime) || safe != "true")
                {
                    if (IsAlive(runtime)) await FreezeRuntimeAsync(runtime);
                    continue;
                }
                string position = await runtime.View.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify([window.scrollX,window.scrollY])");
                if (!IsAlive(runtime)) continue;
                string? json = JsonSerializer.Deserialize<string>(position);
                if (json is not null)
                {
                    var values = JsonSerializer.Deserialize<double[]>(json);
                    if (values is { Length: 2 } && values.All(double.IsFinite))
                    {
                        runtime.Tab.ScrollX = Math.Clamp(values[0], 0, 100_000_000);
                        runtime.Tab.ScrollY = Math.Clamp(values[1], 0, 100_000_000);
                    }
                }
                if (IsAlive(runtime) && runtime.Tab != ActiveTab && !HasProtectedActivity(runtime))
                {
                    DestroyView(runtime.Tab.Id);
                    _hibernatedIds.Add(runtime.Tab.Id);
                    _hibernateCount++;
                    // The existing session store writes the tab's URL, history and scroll to SSD.
                    await SaveSessionAsync();
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException or ArgumentException)
            {
                Program.LogError(DataPath, ex);
                if (IsAlive(runtime)) await FreezeRuntimeAsync(runtime);
            }
        }
        SetMemoryTargets();
        _tabs.Invalidate();
    }

    private async Task ConfigureActivityTrackingAsync(TabRuntime runtime)
    {
        var core = runtime.View.CoreWebView2;
        core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (_, e) =>
        {
            using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
            if (data.RootElement.TryGetProperty("requestId", out var id)) runtime.PendingRequests.Add(id.GetString()!);
            _lastNetworkActivityUtc = DateTimeOffset.UtcNow;
        };
        foreach (string name in new[] { "Network.loadingFinished", "Network.loadingFailed" })
            core.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, e) =>
            {
                using var data = JsonDocument.Parse(e.ParameterObjectAsJson);
                if (data.RootElement.TryGetProperty("requestId", out var id)) runtime.PendingRequests.Remove(id.GetString()!);
                _lastNetworkActivityUtc = DateTimeOffset.UtcNow;
            };
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
    }

    internal async Task FreezeBackgroundForTestAsync() { await UnloadBackgroundTabsAsync(); }
    internal Task HibernateBackgroundForTestAsync() => TransitionAsync(() => HibernateIdleBackgroundTabsAsync(DateTimeOffset.UtcNow.AddHours(2)));
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
}
