using System.Text.Json;

namespace MBrowser;

public static class CoreTests
{
    public static int Run()
    {
        var passed = 0;
        var failed = 0;
        var folder = Path.Combine(Path.GetTempPath(), "MBrowserTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        void Check(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }

        try
        {
            ThemeTests.RunCore(Check, folder);
            Check("Address parsing: domains, ports, loopback and Unicode", () =>
            {
                Resolves(" example.com ", "https://example.com/");
                Resolves("example.com:8080/path", "https://example.com:8080/path");
                Resolves("localhost:5000/test", "http://localhost:5000/test");
                Resolves("127.0.0.1:8080", "http://127.0.0.1:8080/");
                Resolves("[::1]:8080/test", "http://[::1]:8080/test");
                Resolves("::1", "http://[::1]/");
                Resolves("1.1.1.1", "https://1.1.1.1/");
                Resolves("HTTPS://Example.com/a?q=1#x", "https://example.com/a?q=1#x");
                Resolves("about:blank", "about:blank");
                Assert(AddressParser.TryResolve("пример.рф/путь", out var unicode), "Unicode domain rejected");
                Assert(new Uri(unicode).IdnHost == "xn--e1afmkfd.xn--p1ai", "Wrong Unicode host");
            });

            Check("Address parsing: search and blocked schemes", () =>
            {
                Resolves("как уменьшить память", "https://www.google.com/search?q=" + Uri.EscapeDataString("как уменьшить память"));
                Resolves("weather", "https://www.google.com/search?q=weather");
                foreach (var value in new[] { "", "  ", "javascript:alert(1)", "data:text/html,test", "file:///C:/Windows", "mailto:test@example.com", "ftp://example.com", "custom:thing", "custom.domain://thing", "https:example.com", "https://", "https://a\\b", "https://user:secret@example.com", "https://example.com\n" + "bad" })
                    Assert(!AddressParser.TryResolve(value, out _), "Unexpectedly accepted: " + value);
            });

            Check("History branches, redirect replacement and bounds", () =>
            {
                var tab = new BrowserTab();
                Assert(tab.CurrentUrl is null && !tab.CanGoBack && !tab.CanGoForward, "New tab state");
                tab.Visit("https://one.example/");
                tab.Visit("https://two.example/");
                tab.Visit("https://two.example/");
                Assert(tab.History.Count == 2, "Duplicate visit added");
                Assert(tab.Back() == "https://one.example/" && tab.CanGoForward, "Back failed");
                Assert(tab.Forward() == "https://two.example/", "Forward failed");
                tab.Back();
                tab.Visit("https://three.example/");
                Assert(tab.History.Count == 2 && !tab.CanGoForward, "Forward branch retained");
                tab.ReplaceCurrent("https://redirect.example/");
                Assert(tab.History.Count == 2 && tab.CurrentUrl == "https://redirect.example/", "Redirect added history");
                for (var i = 0; i < 100; i++)
                    tab.Visit("https://example.com/" + i);
                Assert(tab.History.Count == 80 && tab.HistoryIndex == 79 && tab.History[0].EndsWith("/20"), "History limit");
                for (var i = 0; i < 100; i++) tab.Back();
                Assert(tab.HistoryIndex == 0, "Back underflow");
                for (var i = 0; i < 100; i++) tab.Forward();
                Assert(tab.HistoryIndex == 79, "Forward overflow");
            });

            Check("Session roundtrip and replacement", () =>
            {
                var path = Path.Combine(folder, "nested", "session.json");
                var session = new AppSession { Tabs = [new BrowserTab { Title = "Пример" }, new BrowserTab()], SelectedIndex = 1 };
                session.Tabs[0].Visit("https://example.com/one");
                session.Tabs[0].Visit("https://example.com/two");
                session.Tabs[0].Back();
                SessionStore.Save(path, session);
                var restored = SessionStore.Load(path);
                Assert(restored.Tabs.Count == 2 && restored.SelectedIndex == 1, "Tabs not restored");
                Assert(restored.Tabs[0].Id == session.Tabs[0].Id && restored.Tabs[0].Title == "Пример", "Tab metadata lost");
                Assert(restored.Tabs[0].CurrentUrl == "https://example.com/one" && restored.Tabs[0].CanGoForward, "History position lost");
                SessionStore.Save(path, new AppSession());
                Assert(SessionStore.Load(path).Tabs.Count == 1, "Existing file not replaced");
                Assert(!File.Exists(path + ".tmp"), "Temporary file retained");
            });

            Check("Corrupt, missing and unsafe session recovery", () =>
            {
                var path = Path.Combine(folder, "corrupt.json");
                Assert(SessionStore.Load(path).Tabs.Count == 1, "Missing-file recovery");
                foreach (var json in new[] { "{broken", "null", "{\"Tabs\":null}", "{\"Tabs\":[]}", "{\"Tabs\":[null]}" })
                {
                    File.WriteAllText(path, json);
                    Assert(SessionStore.Load(path).Tabs.Count == 1, "Malformed session recovery: " + json);
                }
                File.WriteAllText(path, "{\"Tabs\":[{\"Title\":null,\"History\":[\"javascript:alert(1)\",\"https://example.com/\",null,\"file:///secret\"],\"HistoryIndex\":3}],\"SelectedIndex\":900}");
                var restored = SessionStore.Load(path);
                Assert(restored.SelectedIndex == 0 && restored.Tabs[0].History.Count == 1, "Unsafe history retained");
                Assert(restored.Tabs[0].CurrentUrl == "https://example.com/" && restored.Tabs[0].Title == "Новая вкладка", "Recovery position/title");
            });

            Check("Session tab/history limits and unique identities", () =>
            {
                var path = Path.Combine(folder, "limits.json");
                var sameId = Guid.NewGuid();
                var tabs = Enumerable.Range(0, 110).Select(_ => new BrowserTab
                {
                    Id = sameId,
                    History = Enumerable.Range(0, 100).Select(i => "https://example.com/" + i).ToList(),
                    HistoryIndex = 50
                }).ToList();
                File.WriteAllText(path, JsonSerializer.Serialize(new AppSession { Tabs = tabs, SelectedIndex = 109 }));
                var restored = SessionStore.Load(path);
                Assert(restored.Tabs.Count == 100 && restored.SelectedIndex == 99, "Tab limit");
                Assert(restored.Tabs.Select(tab => tab.Id).Distinct().Count() == 100, "Duplicate tab IDs");
                Assert(restored.Tabs.All(tab => tab.History.Count == 80 && tab.CurrentUrl == "https://example.com/50"), "History limit or selection");
            });

            Check("Session write failure is reported", () =>
            {
                var path = Path.Combine(folder, "occupied");
                Directory.CreateDirectory(path);
                var threw = false;
                try { SessionStore.Save(path, new AppSession()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { threw = true; }
                Assert(threw, "Write failure swallowed");
            });

            Check("Idle policy protects active tabs, media and downloads", () =>
            {
                var now = DateTimeOffset.UtcNow;
                var timeout = TimeSpan.FromMinutes(5);
                Assert(!TabFreezePolicy.ShouldFreeze(false, false, now.AddMinutes(-4), now.AddMinutes(-8), now, timeout), "Early freeze");
                Assert(TabFreezePolicy.ShouldFreeze(false, false, now.AddMinutes(-5), now.AddMinutes(-5), now, timeout), "Timeout boundary");
                Assert(!TabFreezePolicy.ShouldFreeze(true, false, now.AddHours(-1), now.AddHours(-1), now, timeout), "Foreground freeze");
                Assert(!TabFreezePolicy.ShouldFreeze(false, true, now.AddHours(-1), now.AddHours(-1), now, timeout), "Protected activity freeze");
                Assert(!TabFreezePolicy.ShouldFreeze(false, false, now.AddHours(-1), now.AddMinutes(-1), now, timeout), "Recent network activity ignored");
            });

            Check("Preferences and themes persist and recover invalid settings", () =>
            {
                var settingsPath = Path.Combine(folder, "preferences");
                Directory.CreateDirectory(settingsPath);
                var settings = BrowserPreferences.Load(settingsPath);
                Assert(settings.UnloadAfterMinutes == 5, "Wrong default timeout");
                settings.UnloadAfterMinutes = 15;
                settings.Save(settingsPath);
                Assert(BrowserPreferences.Load(settingsPath).UnloadAfterMinutes == 15, "Settings lost");
                File.WriteAllText(Path.Combine(settingsPath, "preferences.json"), "{\"UnloadAfterMinutes\":-1,\"MaximumResidentTabs\":900}");
                settings = BrowserPreferences.Load(settingsPath);
                Assert(settings.UnloadAfterMinutes == 1, "Settings bounds");
                ThemeSettings.Load(settingsPath);
                Assert(ThemeSettings.Current == BrowserTheme.Anime, "Anime must be default");
                ThemeSettings.Select(BrowserTheme.Light, settingsPath);
                ThemeSettings.Load(settingsPath);
                Assert(ThemeSettings.Current == BrowserTheme.Light, "Theme not persisted");
                File.WriteAllText(Path.Combine(settingsPath, "theme.json"), "\"invalid\"");
                ThemeSettings.Load(settingsPath);
                Assert(ThemeSettings.Current == BrowserTheme.Anime, "Invalid theme recovery");
            });

            Check("Scroll state survives session storage and is bounded", () =>
            {
                var path = Path.Combine(folder, "scroll.json");
                var session = new AppSession();
                session.Tabs[0].ScrollY = 1700;
                session.Tabs[0].ScrollX = -100;
                SessionStore.Save(path, session);
                var tab = SessionStore.Load(path).Tabs[0];
                Assert(tab.ScrollY == 1700 && tab.ScrollX == 0, "Scroll storage or bounds");
            });

            Check("Extension manifests expose hosts and reject unsupported formats", () =>
            {
                string package = Path.Combine(folder, "package"); Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, "manifest.json"), """
                    {"manifest_version":3,"name":"fixture","version":"1.0","permissions":["storage"],"content_scripts":[{"matches":["https://example.test/*"],"js":["content.js"]}]}
                    """);
                File.WriteAllText(Path.Combine(package, "content.js"), "// fixture");
                var manifest = ExtensionPackage.Read(package);
                Assert(manifest.Permissions.Contains("storage") && manifest.Permissions.Contains("https://example.test/*"), "Permissions omitted from review");
                string copy = Path.Combine(folder, "installed"); ExtensionPackage.Copy(package, copy);
                Assert(File.Exists(Path.Combine(copy, "content.js")), "Package was not copied");
                File.WriteAllText(Path.Combine(package, "manifest.json"), "{\"manifest_version\":2}");
                bool rejected = false;
                try { ExtensionPackage.Read(package); } catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "Unsupported manifest accepted");
            });

            FeatureModule.RunTests(Check, folder);
            Check("Optional feature assembly unloads after use", () =>
            {
                for (int attempt = 0; attempt < 10 && FeatureModule.LastContext!.IsAlive; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                Assert(FeatureModule.LastContext is { IsAlive: false }, "Feature load context retained");
            });
        }
        finally
        {
            // Only remove this suite's freshly-created, uniquely-named temporary directory.
            Directory.Delete(folder, recursive: true);
        }
        Console.WriteLine($"{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void Resolves(string input, string expected)
    {
        Assert(AddressParser.TryResolve(input, out var actual), "Rejected: " + input);
        Assert(actual == expected, $"{input}: expected {expected}, got {actual}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
