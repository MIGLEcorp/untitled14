using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MBrowser;

/// <summary>Disposable vault and actual WebView2 DOM tests. Called only by the smoke profile.</summary>
internal static class PasswordAutofillTests
{
    internal static async Task RunAsync(MainForm form, Action<bool, string> check)
    {
        string originalUrl = form.ActiveCore?.Source ?? throw new InvalidOperationException("Autofill tests need a live WebView.");
        int originalIndex = form.SelectedTabIndexForTest;
        int originalTabs = form.TabCount;
        string folder = Path.Combine(form.DataPath, "autofill-fixtures");
        Directory.CreateDirectory(folder);
        const string single = "single.feather-autofill.test";
        const string multiple = "multiple.feather-autofill.test";
        const string unknown = "unknown.feather-autofill.test";
        const string fixtureSecret = "fixture-single-autofill-secret";
        string csv = Path.Combine(folder, "passwords.csv");
        File.WriteAllText(csv,
            "name,url,username,password\nSingle,https://" + single + "/,single-user," + fixtureSecret + "\n" +
            "First,https://" + multiple + "/,first-user,fixture-first-secret\nSecond,https://" + multiple + "/,second-user,fixture-second-secret\n");
        FeatureModule.Invoke(form.DataPath, "ImportPasswordsCsv", csv);
        File.Delete(csv);

        const string fields = "<form><label>Логин<input id='user' autocomplete='username'></label><label>Пароль<input id='password' type='password' autocomplete='current-password'></label></form>";
        static string Page(string content, string script = "") => "<!doctype html><html><head><meta charset='utf-8'><title>Autofill fixture</title>" +
            "<style>body{margin:40px;font:16px sans-serif}label{display:block;margin:18px 0}input{display:block;width:280px;height:30px}</style></head><body>" +
            content + "<script>" + script + "</script></body></html>";
        File.WriteAllText(Path.Combine(folder, "login.html"), Page(fields,
            "if(window!==top)setTimeout(()=>parent.postMessage({fixtureFramePasswordLength:document.getElementById('password').value.length},'*'),400);"));
        File.WriteAllText(Path.Combine(folder, "typed.html"), Page(fields, "document.getElementById('user').value='typed-user'"));
        File.WriteAllText(Path.Combine(folder, "registration.html"), Page(fields.Replace("current-password", "new-password", StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(folder, "hidden.html"), Page(fields.Replace("id='password'", "id='password' style='display:none'", StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(folder, "cross-action.html"), Page(fields.Replace("<form>", "<form action='https://other.test/'>", StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(folder, "dynamic.html"), Page("<button id='add'>Open sign in</button>",
            "document.getElementById('add').onclick=()=>document.body.insertAdjacentHTML('beforeend'," + JsonSerializer.Serialize(fields) + ")"));
        File.WriteAllText(Path.Combine(folder, "frame.html"), Page("<iframe src='https://" + single + "/login.html'></iframe>",
            "window.framePasswordLength=-1;addEventListener('message',event=>{if(event.origin==='https://" + single + "' && typeof event.data.fixtureFramePasswordLength==='number')window.framePasswordLength=event.data.fixtureFramePasswordLength;});"));

        CoreWebView2? testCore = null;
        var messages = new List<string>();
        EventHandler<CoreWebView2WebMessageReceivedEventArgs> observer = (_, message) => messages.Add(message.WebMessageAsJson);
        try
        {
            await form.OpenTabAsync(originalUrl);
            await WaitAsync(() => form.ActiveCore is not null && !form.IsPageLoading, "Fixture tab did not start");
            testCore = form.ActiveCore!;
            foreach (string host in new[] { single, multiple, unknown })
                testCore.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            testCore.WebMessageReceived += observer;

            async Task Navigate(string host, string page)
            {
                string url = "https://" + host + "/" + page;
                testCore.Navigate(url);
                await WaitAsync(() => form.ActiveCore?.Source == url && !form.IsPageLoading && form.ActiveCore.DocumentTitle == "Autofill fixture", "Autofill fixture page did not finish loading: " + page);
            }
            async Task<string> Value(string id) => JsonSerializer.Deserialize<string>(await testCore.ExecuteScriptAsync("document.getElementById(" + JsonSerializer.Serialize(id) + ").value")) ?? "";
            async Task WaitValue(string id, string expected)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromSeconds(8))
                {
                    if (await Value(id) == expected) return;
                    await Task.Delay(40);
                }
                throw new TimeoutException("Autofill did not fill the expected " + id + " field.");
            }

            await Navigate(single, "login.html");
            await WaitValue("password", fixtureSecret);
            check(await Value("user") == "single-user", "A single imported login fills automatically without a keyboard shortcut");
            await testCore.ExecuteScriptAsync("document.getElementById('user').focus()");
            await WaitAsync(() => form.IsCredentialPopupVisible && form.CredentialPopupCount == 1, "Single-login native popup did not appear on focus");
            using (var field = JsonDocument.Parse(await testCore.ExecuteScriptAsync("(()=>{const r=document.getElementById('user').getBoundingClientRect();return {x:r.x,y:r.bottom,w:innerWidth,h:innerHeight}})()")))
            {
                Rectangle view = form.ActiveWebViewBoundsForTest, popup = form.CredentialPopupBoundsForTest;
                double x = view.X + field.RootElement.GetProperty("x").GetDouble() * view.Width / field.RootElement.GetProperty("w").GetDouble();
                double y = view.Y + field.RootElement.GetProperty("y").GetDouble() * view.Height / field.RootElement.GetProperty("h").GetDouble();
                check(Math.Abs(popup.X - x) <= 12 && Math.Abs(popup.Y - y) <= 12, "Native credential popup is anchored directly below the focused field");
            }

            await Navigate(single, "typed.html");
            await Task.Delay(450);
            check(await Value("user") == "typed-user" && await Value("password") == "", "Automatic autofill preserves text already entered by the user");

            await Navigate(multiple, "login.html");
            await Task.Delay(450);
            check(await Value("password") == "", "Multiple saved accounts wait for a choice instead of selecting an arbitrary login");
            await testCore.ExecuteScriptAsync("document.getElementById('user').focus()");
            await WaitAsync(() => form.IsCredentialPopupVisible && form.CredentialPopupCount == 2, "Multiple-login native popup did not appear");
            form.SelectCredentialSuggestionForTest(1);
            await WaitValue("password", "fixture-second-secret");
            check(await Value("user") == "second-user" && !form.IsCredentialPopupVisible, "Selecting a native popup suggestion fills the selected account without opening a dialog");

            await Navigate(single, "registration.html");
            await testCore.ExecuteScriptAsync("document.getElementById('password').focus()");
            await Task.Delay(450);
            check(await Value("password") == "" && !form.IsCredentialPopupVisible, "Registration/new-password fields are never filled from saved credentials");

            await Navigate(single, "hidden.html");
            await testCore.ExecuteScriptAsync("chrome.webview.postMessage({type:'feather.credentials',kind:'focus',origin:location.origin,bounds:{x:1,y:1,width:100,height:20}})");
            await Task.Delay(450);
            check(await Value("password") == "" && !form.IsCredentialPopupVisible, "Invisible fields and page-supplied popup coordinates cannot trigger credential filling");

            await Navigate(single, "cross-action.html");
            await Task.Delay(450);
            check(await Value("password") == "", "Forms submitting credentials to a different origin are not autofilled");

            await Navigate(unknown, "login.html");
            await testCore.ExecuteScriptAsync("document.getElementById('password').focus()");
            await Task.Delay(450);
            check(await Value("password") == "" && !form.IsCredentialPopupVisible, "An unrelated origin receives neither saved passwords nor a credential popup");

            await Navigate(multiple, "frame.html");
            await WaitAsync(async () => await testCore.ExecuteScriptAsync("window.framePasswordLength") == "0", "Cross-origin iframe fixture did not report an empty password field");
            check(!form.IsCredentialPopupVisible, "Cross-origin iframes cannot access top-document autofill credentials");

            await Navigate(single, "dynamic.html");
            await testCore.ExecuteScriptAsync("document.getElementById('add').click()");
            await WaitValue("password", fixtureSecret);
            check(await Value("user") == "single-user", "Login forms inserted after page load are detected and autofilled automatically");

            await Task.Delay(550);
            await testCore.ExecuteScriptAsync("document.getElementById('user').focus()");
            await WaitAsync(() => form.IsCredentialPopupVisible, "Popup did not appear before tab-switch test");
            await form.SelectTabAsync(originalIndex);
            check(!form.IsCredentialPopupVisible, "Switching tabs dismisses the credential popup immediately");
            await form.SelectTabAsync(form.TabCount - 1);
            check(messages.Count > 0 && messages.All(message => !message.Contains("fixture-single-autofill-secret", StringComparison.Ordinal) &&
                !message.Contains("fixture-first-secret", StringComparison.Ordinal) && !message.Contains("fixture-second-secret", StringComparison.Ordinal)),
                "Page-to-host autofill messages never contain passwords");
        }
        finally
        {
            if (testCore is not null)
            {
                testCore.WebMessageReceived -= observer;
                foreach (string host in new[] { single, multiple, unknown }) testCore.ClearVirtualHostNameToFolderMapping(host);
            }
            while (form.TabCount > originalTabs) await form.CloseTabAsync(form.TabCount - 1);
            if (form.SelectedTabIndexForTest != originalIndex) await form.SelectTabAsync(originalIndex);
        }
    }

    private static Task WaitAsync(Func<bool> condition, string message) => WaitAsync(() => Task.FromResult(condition()), message);

    private static async Task WaitAsync(Func<Task<bool>> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await condition()) return;
            await Task.Delay(40);
        }
        throw new TimeoutException(message);
    }
}
