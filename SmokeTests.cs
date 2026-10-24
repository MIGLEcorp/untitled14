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
        var started=DateTimeOffset.UtcNow;
        var watch=Stopwatch.StartNew();
        var checks=new List<object>();
        string? failure=null;
        void Check(bool condition,string name) { if(!condition)throw new InvalidOperationException(name); checks.Add(new{name,passed=true,elapsedMs=watch.ElapsedMilliseconds}); }
        try
        {
            await form.ReadyTask.WaitAsync(TimeSpan.FromSeconds(40));
            Check(form.LiveViewCount==0,"Native home starts without a browser engine");
            using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save("preview.png",ImageFormat.Png);}
            await WindowFrameTests.RunAsync(form,Check);
            await using var server=new LocalPageServer();
            string one=server.Origin+"one",two=server.Origin+"two";
            await form.NavigateInputAsync(one);
            await WaitForPageAsync(form,one,"Feather smoke one");
            await WaitUntilAsync(()=>Directory.Exists(Path.Combine(dataPath,"Favicons")) && Directory.EnumerateFiles(Path.Combine(dataPath,"Favicons")).Any(),"Site favicon did not reach the local cache");
            Check(true,"The engine favicon is decoded and cached locally");
            await File.WriteAllTextAsync(Path.Combine(dataPath,"bookmarks.json"),JsonSerializer.Serialize(new[]{new{Title="Local bookmark fixture",Url=one}}));
            form.RefreshBookmarks();
            form.AddressInputForTest.Focus();form.AddressInputForTest.Text="Local bookmark";
            await WaitUntilAsync(()=>form.HasSuggestionsForTest,"Local bookmark prediction did not appear");
            Check(true,"Typing offers a local bookmark prediction inside the window");
            form.CloseOverlay();
            Check(!form.Controls.Cast<Control>().Any(control=>control.Dock==DockStyle.Bottom),"The memory footer is absent");
            var retained=form.ActiveCore!;
            Check(await retained.ExecuteScriptAsync("window.smokeProbe")=="42","Real JavaScript executes in the installed engine");
            await retained.ExecuteScriptAsync("window.retainedState={value:73};let draft=document.createElement('input');draft.id='draft';document.body.append(draft);draft.value='unsaved draft'");
            await form.OpenTabAsync();
            await Task.Delay(300);
            await form.FreezeBackgroundForTestAsync();
            await WaitUntilAsync(()=>retained.IsSuspended,"Inactive page did not suspend");
            Check(form.LiveViewCount==1,"Freezing preserves the WebView rather than disposing it");
            await form.SelectTabAsync(0);
            Check(form.ActiveCore==retained && !retained.IsSuspended,"Selecting a frozen tab resumes the same engine instance");
            Check(await retained.ExecuteScriptAsync("window.retainedState.value")=="73" && await retained.ExecuteScriptAsync("document.getElementById('draft').value")=="\"unsaved draft\"","DOM, JavaScript objects and unsaved input survive freezing");
            await form.CloseTabAsync(1);
            await retained.ExecuteScriptAsync("document.getElementById('next').click()");
            await WaitForPageAsync(form,two,"Feather smoke two");
            await form.GoBackAsync(); await WaitForPageAsync(form,one,"Feather smoke one");
            Check(form.ActiveTab.CanGoForward,"History remains usable after suspension and navigation");
            retained=form.ActiveCore!;
            await retained.ExecuteScriptAsync("navigator.geolocation.getCurrentPosition(()=>window.permissionFixture='allow',()=>window.permissionFixture='deny')");
            await WaitUntilAsync(()=>form.IsPermissionPromptVisible,"Permission prompt did not appear");
            Check(form.IsPermissionPromptVisible,"Permissions UI is created only when a site requests access");
            form.DecidePermissionForTest(false);
            await WaitForScriptValueAsync(form,"window.permissionFixture","\"deny\"");
            Check(!form.IsPermissionPromptVisible,"Permission request resolves and its UI is released");
            string extensionPath=Path.Combine(dataPath,"extension-fixture");Directory.CreateDirectory(extensionPath);
            await File.WriteAllTextAsync(Path.Combine(extensionPath,"manifest.json"),"""
                {"manifest_version":3,"name":"Feather API fixture","version":"1.0","permissions":["storage","declarativeNetRequest"],"host_permissions":["http://127.0.0.1/*"],"content_scripts":[{"matches":["http://127.0.0.1/*"],"js":["content.js"]}],"declarative_net_request":{"rule_resources":[{"id":"rules","enabled":true,"path":"rules.json"}]}}
                """);
            await File.WriteAllTextAsync(Path.Combine(extensionPath,"content.js"),"chrome.storage.local.set({probe:91},()=>chrome.storage.local.get('probe',r=>document.documentElement.dataset.extensionProbe=String(r.probe)))");
            await File.WriteAllTextAsync(Path.Combine(extensionPath,"rules.json"),"""
                [{"id":1,"priority":1,"action":{"type":"block"},"condition":{"urlFilter":"/blocked","resourceTypes":["xmlhttprequest"]}}]
                """);
            var extension=await retained.Profile.AddBrowserExtensionAsync(extensionPath);
            retained.Navigate(one+"?extension");await WaitForPageAsync(form,one+"?extension","Feather smoke one");
            await WaitForScriptValueAsync(form,"document.documentElement.dataset.extensionProbe","\"91\"");
            Check(true,"Manifest V3 content scripts and chrome.storage work in a real tab");
            await retained.ExecuteScriptAsync("fetch('/blocked').then(()=>window.blockFixture=false).catch(()=>window.blockFixture=true)");
            await WaitForScriptValueAsync(form,"window.blockFixture","true");
            Check(true,"Extension declarativeNetRequest can block a matching request");
            await extension.RemoveAsync();
            await form.ApplyCookiesAsync(JsonSerializer.Serialize(new[]{new{Domain="127.0.0.1",Name="fixture",Value="session",Path="/",IsSecure=false,IsHttpOnly=true,ExpiresUtc=(DateTimeOffset?)null,SameSite="Lax"}}));
            Check((await retained.CookieManager.GetCookiesAsync(one)).Any(c=>c.Name=="fixture"&&c.IsHttpOnly),"Cookie import preserves session and HttpOnly flags");
            await PasswordAutofillTests.RunAsync(form,Check);
            Check(form.TabCount==1,"Autofill fixture closes its temporary tab");
            await form.HomeAsync();
            Check(form.LiveViewCount==0,"Explicitly leaving a site releases its view");
            Check(!File.Exists(Path.Combine(dataPath,"errors.log")),"No unhandled UI errors were logged");
            Environment.ExitCode=0;
        }
        catch(Exception error){failure=error.ToString();Environment.ExitCode=1;Program.LogError(dataPath,error);}
        finally
        {
            await File.WriteAllTextAsync("smoke-results.json",JsonSerializer.Serialize(new{passed=failure is null,startedUtc=started,elapsedMs=watch.ElapsedMilliseconds,dataPath,checks,failure},new JsonSerializerOptions{WriteIndented=true}));
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
                string html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Feather smoke {name}</title><link rel=\"icon\" href=\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' fill='%2300b5bd'/%3E%3C/svg%3E\"><style>body{{min-height:3200px}}</style></head><body><h1>Local test {name}</h1><p id=\"probe\">Waiting for JavaScript</p><a id=\"next\" href=\"{destination}\">Next page</a><script>window.smokeProbe=42;document.getElementById('probe').textContent='JavaScript is running';</script></body></html>";
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
