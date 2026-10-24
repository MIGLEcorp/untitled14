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
        TrimFrozenRenderers();
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

    private void TrimFrozenRenderers()
    {
        if (_environment is null || _runtimes.Values.Any(runtime => !runtime.View.CoreWebView2.IsSuspended)) return;
        // Renderer memory stays committed and pageable. Windows chooses compression/pagefile placement.
        foreach (var info in _environment.GetProcessInfos().Where(info => info.Kind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessKind.Renderer))
        {
            try { using var process = Process.GetProcessById(info.ProcessId); EmptyWorkingSet(process.Handle); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
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
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("psapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyWorkingSet(nint process);
}
