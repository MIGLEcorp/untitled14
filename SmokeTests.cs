using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Feather;

/// <summary>Exercises the real WinForms host and installed WebView2 Runtime without using external sites.</summary>
internal static class SmokeTests
{
    internal static async Task RunAsync(MainForm form, string dataPath)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var checks = new List<object>();
        var memory = new List<object>();
        string? failure = null;
        string reportPath = Path.Combine(Environment.CurrentDirectory, "smoke-results.json");
        string previewPath = Path.Combine(Environment.CurrentDirectory, "preview.png");

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(new { name = description, passed = true, elapsedMs = watch.ElapsedMilliseconds });
        }

        void Sample(string stage)
        {
            var sample = form.ReadMemory();
            memory.Add(new
            {
                stage,
                sample.WorkingSetBytes,
                sample.PrivateBytes,
                sample.ProcessCount,
                liveViews = form.LiveViewCount,
                tabs = form.TabCount,
                elapsedMs = watch.ElapsedMilliseconds
            });
        }

        try
        {
            await form.ReadyTask.WaitAsync(TimeSpan.FromSeconds(40));
            await Task.Delay(200);
            Check(form.TabCount > 0 && form.LiveViewCount == 0, "Native home starts without a WebView2 control");
            Sample("native-home");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(previewPath, ImageFormat.Png);
            }
            Check(File.Exists(previewPath), "Native home preview saved");
            await WindowFrameTests.RunAsync(form, Check);

            await using var server = new LocalPageServer();
            string one = server.Origin + "one";
            string two = server.Origin + "two";

            await form.NavigateInputAsync(one);
            await WaitForPageAsync(form, one, "Feather smoke one");
            await WaitForHistoryAsync(form, one, 0, 1);
            Check(form.LiveViewCount == 1, "First webpage creates exactly one live control");
            string probe = await form.ActiveCore!.ExecuteScriptAsync("JSON.stringify({value:window.smokeProbe, text:document.getElementById('probe').textContent})");
            string? json = JsonSerializer.Deserialize<string>(probe);
            using (var parsed = JsonDocument.Parse(json ?? "null"))
            {
                Check(parsed.RootElement.GetProperty("value").GetInt32() == 42 &&
                      parsed.RootElement.GetProperty("text").GetString() == "JavaScript is running",
                    "Real webpage JavaScript executes and updates the DOM");
            }
            await Task.Delay(600);
            Sample("one-local-page");
            Check(form.ActiveCore!.Settings.IsPasswordAutosaveEnabled && form.ActiveCore.Settings.IsGeneralAutofillEnabled,
                "Native password-save prompts and form autofill are enabled");
            var beforeFullscreen = form.Bounds;
            form.ToggleFullScreen();
            Check(form.IsFullScreen && form.Bounds == Screen.FromControl(form).Bounds,
                "Fullscreen fills the monitor and hides browser chrome");
            form.ToggleFullScreen();
            Check(!form.IsFullScreen && form.Bounds == beforeFullscreen,
                "Leaving fullscreen restores the original window bounds");
            var themedCore = form.ActiveCore;
            form.SelectTheme(BrowserTheme.Light);
            Check(form.ActiveCore == themedCore && themedCore.Profile.PreferredColorScheme == CoreWebView2PreferredColorScheme.Light,
                "Changing theme updates native chrome and page preference without recreating the tab");
            form.SelectTheme(BrowserTheme.Anime);
            await form.ApplyCookiesAsync(JsonSerializer.Serialize(new[] { new
            {
                Domain = "127.0.0.1", Name = "feather_smoke", Value = "fixture-only", Path = "/",
                IsSecure = false, IsHttpOnly = true, ExpiresUtc = (DateTimeOffset?)null, SameSite = "Lax"
            } }));
            var importedCookies = await form.ActiveCore.CookieManager.GetCookiesAsync(one);
            Check(importedCookies.Any(cookie => cookie.Name == "feather_smoke" && cookie.Value == "fixture-only" && cookie.IsHttpOnly),
                "Cookie import writes real session cookies with HttpOnly preserved");

            string partitionReport = await form.ApplyCookiesAsync("""
                [
                  {"Domain":"third.feather.test","Name":"feather_partition","Value":"fixture-one","Path":"/","IsSecure":true,"IsHttpOnly":true,"ExpiresUtc":null,"SameSite":"None","PartitionKey":{"TopLevelSite":"https://one.test","HasCrossSiteAncestor":false}},
                  {"Domain":"third.feather.test","Name":"feather_partition","Value":"fixture-two","Path":"/","IsSecure":true,"IsHttpOnly":true,"ExpiresUtc":null,"SameSite":"None","PartitionKey":{"TopLevelSite":"https://two.test","HasCrossSiteAncestor":true}},
                  {"Domain":"third.feather.test","Name":"feather_partition_unsafe","Value":"fixture-rejected","Path":"/","IsSecure":false,"IsHttpOnly":true,"ExpiresUtc":null,"SameSite":"Lax","PartitionKey":{"TopLevelSite":"https://one.test","HasCrossSiteAncestor":false}}
                ]
                """);
            using (var cookieStorage = JsonDocument.Parse(await form.ActiveCore.CallDevToolsProtocolMethodAsync("Storage.getCookies", "{}")))
            {
                var partitions = cookieStorage.RootElement.GetProperty("cookies").EnumerateArray()
                    .Where(cookie => cookie.GetProperty("name").GetString() == "feather_partition").ToList();
                Check(partitions.Count == 2 && partitions.All(cookie => cookie.TryGetProperty("partitionKey", out _) &&
                    cookie.GetProperty("domain").GetString() == "third.feather.test" && cookie.GetProperty("httpOnly").GetBoolean() && cookie.GetProperty("session").GetBoolean()) &&
                    partitions.Any(cookie => cookie.GetProperty("partitionKey").GetProperty("topLevelSite").GetString() == "https://one.test" &&
                        !cookie.GetProperty("partitionKey").GetProperty("hasCrossSiteAncestor").GetBoolean()) &&
                    partitions.Any(cookie => cookie.GetProperty("partitionKey").GetProperty("topLevelSite").GetString() == "https://two.test" &&
                        cookie.GetProperty("partitionKey").GetProperty("hasCrossSiteAncestor").GetBoolean()),
                    "Cookie import retains CHIPS sites, ancestor bits, host-only scope and session flags in real WebView2");
                Check(!cookieStorage.RootElement.GetProperty("cookies").EnumerateArray().Any(cookie => cookie.GetProperty("name").GetString() == "feather_partition_unsafe") &&
                    partitionReport.Contains("импортированы: 2", StringComparison.Ordinal) && partitionReport.Contains("отклонено движком: 1", StringComparison.Ordinal),
                    "Unsupported partitions are reported accurately and never become ordinary cookies");
            }

            await form.ActiveCore!.ExecuteScriptAsync("document.getElementById('next').click()");
            await WaitForPageAsync(form, two, "Feather smoke two");
            await WaitForHistoryAsync(form, two, 1, 2);
            Check(true, "A webpage link navigates to the second page");
            await form.GoBackAsync();
            await WaitForPageAsync(form, one, "Feather smoke one");
            await WaitForHistoryAsync(form, one, 0, 2);
            Check(true, "Back returns to the previous webpage");
            await form.GoForwardAsync();
            await WaitForPageAsync(form, two, "Feather smoke two");
            await WaitForHistoryAsync(form, two, 1, 2);
            Check(true, "Forward returns to the second webpage");

            string third = one + "?third";
            await form.NavigateInputAsync(third);
            await WaitForPageAsync(form, third, "Feather smoke one");
            await WaitForHistoryAsync(form, third, 2, 3);
            await Task.WhenAll(form.GoBackAsync(), form.GoBackAsync());
            await WaitForPageAsync(form, one, "Feather smoke one");
            await WaitForHistoryAsync(form, one, 0, 3);
            Check(true, "Two rapid Back commands reach the first history entry");
            await form.GoForwardAsync();
            await WaitForPageAsync(form, two, "Feather smoke two");
            await WaitForHistoryAsync(form, two, 1, 3);
            Check(true, "Forward after rapid Back reaches the middle entry");
            await form.GoForwardAsync();
            await WaitForPageAsync(form, third, "Feather smoke one");
            await WaitForHistoryAsync(form, third, 2, 3);
            Check(true, "Forward preserves the third entry after rapid Back");

            int beforeReplace = form.ActiveTab.History.Count;
            int replaceIndex = form.ActiveTab.HistoryIndex;
            string replacement = one + "?replacement";
            await form.ActiveCore!.ExecuteScriptAsync("history.replaceState({smoke:'replacement'}, '', '/one?replacement')");
            await WaitForPageAsync(form, replacement, "Feather smoke one");
            await WaitForHistoryAsync(form, replacement, replaceIndex, beforeReplace);
            Check(form.ActiveTab.History.Count == beforeReplace,
                "SPA replaceState updates the current URL without growing history");

            string pushed = two + "?push";
            await form.ActiveCore!.ExecuteScriptAsync("history.pushState({smoke:'push'}, '', '/two?push')");
            await WaitForPageAsync(form, pushed, "Feather smoke one");
            await WaitForHistoryAsync(form, pushed, replaceIndex + 1, beforeReplace + 1);
            Check(form.ActiveTab.HistoryIndex == form.ActiveTab.History.Count - 1,
                "SPA pushState appends one entry and selects the last history position");

            await form.ActiveCore!.ExecuteScriptAsync("history.back()");
            await WaitForPageAsync(form, replacement, "Feather smoke one");
            await WaitForHistoryAsync(form, replacement, replaceIndex, beforeReplace + 1);
            Check(true, "Native history.back updates the model index without growing history");
            await form.ActiveCore!.ExecuteScriptAsync("history.forward()");
            await WaitForPageAsync(form, pushed, "Feather smoke one");
            await WaitForHistoryAsync(form, pushed, replaceIndex + 1, beforeReplace + 1);
            Check(true, "Native history.forward restores the pushed SPA entry");

            await form.NavigateInputAsync(two);
            await WaitForPageAsync(form, two, "Feather smoke two");
            await WaitForHistoryAsync(form, two, beforeReplace + 1, beforeReplace + 2);
            form.WindowState = FormWindowState.Minimized;
            await WaitUntilAsync(() => form.ActiveCore?.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low,
                "Minimized WebView2 memory target did not become Low");
            Check(true, "Minimizing the window sets the WebView2 memory target to Low");
            form.WindowState = FormWindowState.Normal;
            await WaitUntilAsync(() => form.ActiveCore?.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal,
                "Restored WebView2 memory target did not become Normal");
            Check(true, "Restoring the window sets the WebView2 memory target to Normal");

            int initialTabs = form.TabCount;
            var retainedCore = form.ActiveCore;
            await form.ActiveCore!.ExecuteScriptAsync("window.retainedProbe='tab-state';scrollTo(0,600)");
            await form.OpenTabAsync();
            Check(form.TabCount == initialTabs + 1 && form.LiveViewCount == 1 && form.ActiveCore is null,
                "Opening a native blank tab retains the previous page in the background");
            await form.SelectTabAsync(0);
            await WaitForPageAsync(form, two, "Feather smoke two");
            Check(form.ActiveCore == retainedCore && await form.ActiveCore!.ExecuteScriptAsync("window.retainedProbe") == "\"tab-state\"",
                "Switching back preserves the same live DOM and JavaScript state");
            await form.SelectTabAsync(1);
            await form.UnloadIdleTabsAsync(DateTimeOffset.UtcNow.AddMinutes(4));
            Check(form.LiveViewCount == 1, "Background page remains resident before the five-minute timeout");
            await form.UnloadIdleTabsAsync(DateTimeOffset.UtcNow.AddMinutes(6));
            Check(form.LiveViewCount == 0, "Idle background page is disposed after the configured timeout");
            await form.SelectTabAsync(0);
            await WaitForPageAsync(form, two, "Feather smoke two");
            Check(form.ActiveCore != retainedCore && await form.ActiveCore!.ExecuteScriptAsync("window.retainedProbe") == "null",
                "An evicted tab is recreated only when selected");
            await WaitForScriptValueAsync(form, "scrollY", "600");
            Check(true, "An evicted page restores its scroll position after reloading");
            await form.ActiveCore!.ExecuteScriptAsync("let draft=document.createElement('input');draft.id='draft';document.body.append(draft);draft.value='unsaved';");
            await form.SelectTabAsync(1);
            await form.UnloadIdleTabsAsync(DateTimeOffset.UtcNow.AddMinutes(6));
            Check(form.LiveViewCount == 1, "Unsaved form input protects a background tab from automatic eviction");
            await form.SelectTabAsync(0);
            Check(await form.ActiveCore!.ExecuteScriptAsync("document.getElementById('draft').value") == "\"unsaved\"",
                "Protected form data survives a background interval");
            await form.ActiveCore.ExecuteScriptAsync("document.getElementById('draft').remove()");
            await form.CloseTabAsync(form.TabCount - 1);
            Check(form.TabCount == initialTabs && form.LiveViewCount == 1,
                "Closing an inactive tab preserves the active webpage");

            for (int i = 0; i < 20; i++)
            {
                string address = i % 2 == 0 ? one : two;
                await form.OpenTabAsync(address);
                await WaitForPageAsync(form, address, i % 2 == 0 ? "Feather smoke one" : "Feather smoke two");
                if (form.LiveViewCount > 8)
                    throw new InvalidOperationException($"Resident tab budget exceeded after adding tab {i + 1}: {form.LiveViewCount}.");
            }
            Check(form.TabCount == initialTabs + 20 && form.LiveViewCount == 8,
                "Twenty-one open tabs keep at most eight ordinary pages resident");
            await form.UnloadIdleTabsAsync(DateTimeOffset.UtcNow.AddMinutes(6));
            Check(form.LiveViewCount == 1, "Timed eviction leaves only the active page after many background tabs");
            await Task.Delay(1200);
            Sample("twenty-sleeping-tabs-and-one-active");

            int lastTab = form.TabCount - 1;
            await Task.WhenAll(form.SelectTabAsync(0), form.SelectTabAsync(1), form.SelectTabAsync(lastTab));
            await WaitForPageAsync(form, two, "Feather smoke two");
            Check(form.LiveViewCount <= 3 && form.TabCount == initialTabs + 20,
                "Rapid tab switches serialize without leaking controls or losing tabs");
            Check(await form.ActiveCore!.ExecuteScriptAsync("window.smokeProbe") == "42",
                "JavaScript remains operational after rapid tab switches");

            await form.HomeAsync();
            await WaitUntilAsync(() => !form.HasLoadingTabs, "Background pages did not finish loading");
            await form.UnloadBackgroundTabsAsync();
            Check(form.LiveViewCount == 0 && form.ActiveCore is null,
                "Returning home releases all live WebView2 controls");
            await Task.Delay(1200);
            Sample("home-after-twenty-tabs");

            while (form.TabCount > 1)
                await form.CloseTabAsync(0);
            await form.CloseTabAsync(0);
            Check(form.TabCount == 1 && form.LiveViewCount == 0 && form.ActiveCore is null &&
                  form.ActiveTab.CurrentUrl is null or "about:blank",
                "Closing the last tab leaves one native blank tab without a WebView2 control");

            await form.NavigateInputAsync(one);
            await WaitForPageAsync(form, one, "Feather smoke one");
            await form.ActiveCore!.ExecuteScriptAsync("document.getElementById('next').click()");
            await WaitForPageAsync(form, two, "Feather smoke two");
            await form.ActiveCore!.ExecuteScriptAsync("history.back()");
            await WaitForPageAsync(form, one, "Feather smoke one");
            await WaitForHistoryAsync(form, one, 0, 2);
            await form.ActiveCore!.ExecuteScriptAsync("history.forward()");
            await WaitForPageAsync(form, two, "Feather smoke two");
            await WaitForHistoryAsync(form, two, 1, 2);
            Check(true, "Native document back and forward preserve history length and model position");

            await form.NavigateInputAsync(one + "?spa");
            await WaitForPageAsync(form, one + "?spa", "Feather smoke one");
            for (int i = 1; i <= 85; i++)
            {
                await form.ActiveCore!.ExecuteScriptAsync($"history.pushState({{}}, '', '/one?state={i}')");
                int count = Math.Min(80, 3 + i);
                await WaitForHistoryAsync(form, one + $"?state={i}", count - 1, count);
            }
            await form.ActiveCore!.ExecuteScriptAsync("history.go(-10)");
            await WaitForHistoryAsync(form, one + "?state=75", 69, 80);
            Check(true, "SPA history remains correct beyond Chromium's native history window and the 80-entry model limit");
            await form.ActiveCore.ExecuteScriptAsync("history.replaceState({}, '', '/one?replaced-after-cap')");
            await WaitForHistoryAsync(form, one + "?replaced-after-cap", 69, 80);
            await form.ActiveCore.ExecuteScriptAsync("history.pushState({}, '', '/one?branched-after-cap')");
            await WaitForHistoryAsync(form, one + "?branched-after-cap", 70, 71);
            Check(true, "SPA replacement and branching after history trimming preserve existing entries");

            await PasswordAutofillTests.RunAsync(form, Check);
            await form.HomeAsync();
            await form.ApplyCookiesAsync(JsonSerializer.Serialize(new[] { new
            {
                Domain = "127.0.0.1", Name = "feather_native_home_import", Value = "fixture", Path = "/",
                IsSecure = false, IsHttpOnly = false, ExpiresUtc = (DateTimeOffset?)null, SameSite = "Lax"
            } }));
            Check(form.ActiveCore is null && form.LiveViewCount == 0 && form.TabCount == 1,
                "Cookie import from the native home uses and releases a temporary view without creating a tab");

            string errorLog = Path.Combine(dataPath, "errors.log");
            Check(!File.Exists(errorLog) || new FileInfo(errorLog).Length == 0,
                "No unhandled UI errors were logged");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            checks.Add(new { name = "Smoke test failure", passed = false, elapsedMs = watch.ElapsedMilliseconds, error = failure });
            Environment.ExitCode = 1;
            Program.LogError(dataPath, ex);
        }
        finally
        {
            try
            {
                var report = new
                {
                    passed = failure is null,
                    startedUtc = started,
                    elapsedMs = watch.ElapsedMilliseconds,
                    dataPath,
                    previewPath,
                    checks,
                    memory,
                    memoryNote = "WorkingSetBytes sums resident working sets for the host and its WebView2 profile processes; shared pages may be counted more than once. PrivateBytes is private committed memory, not physical RAM. GetProcessInfos excludes crashpad. Measurements use simple local pages, not heavy external websites.",
                    failure
                };
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Program.LogError(dataPath, ex);
            }
            form.Close();
        }
    }

    private static async Task WaitForPageAsync(MainForm form, string address, string title)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(35))
        {
            try
            {
                var core = form.ActiveCore;
                if (core is not null && !form.IsPageLoading &&
                    string.Equals(core.Source, address, StringComparison.Ordinal) && core.DocumentTitle == title)
                    return;
            }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Page did not become ready: {address}; expected title: {title}; live controls: {form.LiveViewCount}; loading: {form.IsPageLoading}.");
    }

    private static async Task WaitForHistoryAsync(MainForm form, string address, int index, int count)
    {
        try
        {
            await WaitUntilAsync(() => form.ActiveTab.CurrentUrl == address &&
                form.ActiveTab.HistoryIndex == index && form.ActiveTab.History.Count == count,
                $"History model did not synchronize with {address}");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}; expected index/count: {index}/{count}; actual URL: {form.ActiveTab.CurrentUrl}; actual index/count: {form.ActiveTab.HistoryIndex}/{form.ActiveTab.History.Count}.", ex);
        }
    }

    private static async Task WaitForScriptValueAsync(MainForm form, string script, string expected)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await form.ActiveCore!.ExecuteScriptAsync(script) == expected) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Script value did not become {expected}: {script}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                if (condition()) return;
            }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            await Task.Delay(50);
        }
        throw new TimeoutException(failureMessage);
    }

    /// <summary>Tiny loopback-only HTTP server; an ephemeral TCP port avoids HttpListener URL reservations.</summary>
    private sealed class LocalPageServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task serverTask;
        internal string Origin { get; }

        internal LocalPageServer()
        {
            listener.Start();
            Origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            serverTask = ServeAsync();
        }

        private async Task ServeAsync()
        {
            var clients = new List<Task>();
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    clients.RemoveAll(task => task.IsCompleted);
                    clients.Add(HandleClientAsync(client, stopping.Token));
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (SocketException) when (stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
            finally
            {
                await Task.WhenAll(clients);
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
                await RespondAsync(client, cancellationToken);
        }

        private static async Task RespondAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? request = await reader.ReadLineAsync(timeout.Token);
                if (request is null) return;
                string? header;
                do { header = await reader.ReadLineAsync(timeout.Token); }
                while (!string.IsNullOrEmpty(header));

                string path = request.Split(' ').ElementAtOrDefault(1) ?? "/one";
                bool second = path.StartsWith("/two", StringComparison.Ordinal);
                string name = second ? "two" : "one";
                string destination = second ? "/one" : "/two";
                string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Feather smoke {name}</title><style>body{{min-height:3200px}}</style></head><body><h1>Local test {name}</h1><p id=\"probe\">Waiting for JavaScript</p><a id=\"next\" href=\"{destination}\">Next page</a><script>window.smokeProbe=42;document.getElementById('probe').textContent='JavaScript is running';</script></body></html>";
                byte[] body = Encoding.UTF8.GetBytes(html);
                byte[] responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            listener.Stop();
            await serverTask;
            stopping.Dispose();
        }
    }
}
