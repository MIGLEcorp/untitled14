using Microsoft.Win32;

namespace MBrowser;

internal static class BrowserIntegrationTests
{
    internal static void RunCore(Action<string, Action> check, string folder)
    {
        check("Default-browser candidates register without changing protected UserChoice", () =>
        {
            string testKey = @"Software\MBrowser-Test-" + Guid.NewGuid().ToString("N");
            try
            {
                using var root = Registry.CurrentUser.CreateSubKey(testKey);
                string executable = Path.Combine(folder, "Browser With Spaces,Comma", "MBrowser.exe");
                BrowserRegistration.Register(executable, root);
                using var http = root.OpenSubKey(BrowserRegistration.Capabilities + @"\URLAssociations");
                Assert((string?)http?.GetValue("https") == BrowserRegistration.UrlId, "HTTPS registration missing");
                using var files = root.OpenSubKey(BrowserRegistration.Capabilities + @"\FileAssociations");
                Assert(files?.GetValueNames().Length == BrowserDocuments.Extensions.Length, "Document associations missing");
                using var command = root.OpenSubKey(@"Software\Classes\MBrowser.Url\shell\open\command");
                Assert((string?)command?.GetValue("") == $"\"{executable}\" --open \"%1\"", "Shell command is not quoted");
                using var choice = root.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations");
                Assert(choice is null, "Registration wrote protected defaults");
                BrowserRegistration.Unregister(Application.ExecutablePath, root);
                using (var kept = root.OpenSubKey(BrowserRegistration.Client)) Assert(kept is not null, "Uninstalling another copy erased the active registration");
                BrowserRegistration.Unregister(executable, root);
                using var removed = root.OpenSubKey(BrowserRegistration.Client);
                Assert(removed is null, "Uninstall left its own registration behind");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }
        });
        check("Explicit local documents round-trip while arbitrary file navigation remains blocked", () =>
        {
            string file = Path.Combine(folder, "local # document.html"); File.WriteAllText(file, "<h1>local</h1>");
            Assert(BrowserDocuments.TryResolve(file, out var uri), "Local HTML rejected");
            Assert(!AddressParser.TryResolve(uri, out _), "Typing a file URL bypassed explicit-open policy");
            var session = new AppSession { Tabs = [new BrowserTab { History = [uri], HistoryIndex = 0, LocalDocuments = [uri] }] };
            string path = Path.Combine(folder, "local-session.json"); SessionStore.Save(path, session);
            Assert(SessionStore.Load(path).Tabs[0].CurrentUrl == uri, "Approved document lost from session");
            session.Tabs[0].LocalDocuments = []; SessionStore.Save(path, session);
            Assert(SessionStore.Load(path).Tabs[0].CurrentUrl is null, "Unapproved file restored");
            Assert(!BrowserDocuments.TryResolve(@"\\server\private.html", out _), "UNC path fetched");
        });
        check("Single-instance activation delivers Unicode URLs without another browser process", () =>
        {
            var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            string pipe = "MBrowser-Test-" + Guid.NewGuid().ToString("N");
            using var server = new BrowserActivation(values => received.TrySetResult(values), pipe);
            Assert(BrowserActivation.Send(["https://example.test/путь?q=1"], pipe), "IPC send failed");
            Assert(received.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Single().Contains("путь"), "IPC payload corrupted");
        });
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
