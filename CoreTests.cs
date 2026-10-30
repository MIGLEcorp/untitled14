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
            BrowserIntegrationTests.RunCore(Check,folder);
            Check("Tab strip restores hidden tabs when overflow arrows disappear", () =>
            {
                using var strip = new TabStrip { Width = 820 };
                strip.Tabs = Enumerable.Range(0, 20)
                    .Select(i => new BrowserTab { Title = "Tab " + i }).ToArray();
                strip.SelectedIndex = 19;
                strip.RevealSelected();
                Assert(strip.HasOverflowForTest && strip.VisibleStartForTest > 0,
                    "Narrow strip should scroll to the selected tab");
                using (var narrow = new Bitmap(strip.Width, strip.Height))
                    strip.DrawToBitmap(narrow, new Rectangle(Point.Empty, strip.Size));
                Assert(strip.PaintedOverflowArrowsForTest && strip.PaintedTabCountForTest > 0,
                    "Narrow layout did not paint scrolling arrows and visible tabs");

                // Expanding must paint all 20 tabs and reset the scroll offset.
                strip.Width = 3600;
                using (var wide = new Bitmap(strip.Width, strip.Height))
                    strip.DrawToBitmap(wide, new Rectangle(Point.Empty, strip.Size));
                Assert(!strip.HasOverflowForTest && !strip.PaintedOverflowArrowsForTest,
                    "Overflow arrows survived a wide resize");
                Assert(strip.VisibleStartForTest == 0 && strip.VisibleCountForTest == 20 &&
                    strip.PaintedTabCountForTest == 20,
                    "Tabs remained hidden or unpainted after the arrows disappeared");

                // Repeating the resize must keep the selected tab reachable.
                strip.Width = 820;
                Assert(strip.HasOverflowForTest && strip.VisibleStartForTest > 0,
                    "Overflow did not return after shrinking");
                strip.Width = 3600;
                Assert(strip.VisibleStartForTest == 0 && strip.VisibleCountForTest == 20,
                    "Second expansion failed to reveal every tab");
            });
            Check("Tab strip clamps scroll after closing tabs or partial expansion", () =>
            {
                Assert(TabStrip.FirstVisibleForLayout(20, 19, 5, 18) == 15,
                    "Scroll offset was not clamped to the last full page");
                Assert(TabStrip.FirstVisibleForLayout(20, 8, 8, 15) == 8,
                    "Selected tab became hidden when capacity increased");
                Assert(TabStrip.FirstVisibleForLayout(20, 19, 20, 15) == 0,
                    "Overflow-free layout did not reset the offset");
                Assert(TabStrip.FirstVisibleForLayout(0, 0, 4, 20) == 0,
                    "Empty tab list retained old scroll position");
                using var strip = new TabStrip { Width = 820 };
                strip.Tabs = Enumerable.Range(0, 20).Select(i => new BrowserTab()).ToArray();
                strip.SelectedIndex = 19;
                strip.RevealSelected();
                strip.Tabs = strip.Tabs.Take(2).ToArray();
                strip.SelectedIndex = 1;
                strip.RevealSelected();
                Assert(!strip.HasOverflowForTest && strip.VisibleStartForTest == 0 &&
                    strip.VisibleCountForTest == 2, "Closing tabs left hidden entries");
            });
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

            Check("Adaptive eviction policy and GPU flags", () =>
            {
                var now = DateTimeOffset.UtcNow;
                const long plentyOfRam = 16L * 1024 * 1024 * 1024;
                Assert(!AdaptiveResourceManager.ShouldHibernate(MemoryMode.Performance, now.AddHours(-24), now, 99, 1, 5),
                    "Performance mode evicted an inactive tab");
                Assert(!AdaptiveResourceManager.ShouldHibernate(MemoryMode.Balanced, now.AddSeconds(-15), now, 99, 1, 5),
                    "Hysteresis ignored for a recently switched tab");
                Assert(AdaptiveResourceManager.ShouldHibernate(MemoryMode.Extreme, now.AddMinutes(-3), now, 3, plentyOfRam, 5),
                    "Extreme mode failed to evict old tab");
                Assert(AdaptiveResourceManager.ShouldHibernate(MemoryMode.LowMemory, now.AddMinutes(-2), now, 3, 100_000_000, 5),
                    "Low-memory emergency evictions ignored");
                Assert(GraphicsOptimizationManager.BrowserArguments(true, false) == "", "Default GPU acceleration was disabled");
                Assert(GraphicsOptimizationManager.BrowserArguments(false, false).Contains("--disable-gpu"),
                    "GPU toggle did not disable hardware rendering");
                Assert(!GraphicsOptimizationManager.BrowserArguments(false, false).Contains("--no-sandbox"),
                    "Browser sandbox unexpectedly disabled");
            });

            Check("Asynchronous atomic session storage and native DXGI fallback", () =>
            {
                var path = Path.Combine(folder, "async-session.json");
                var data = new AppSession();
                data.Tabs[0].Visit("https://example.com/ssd");
                data.Tabs[0].ScrollY = 700;
                SessionStore.SaveAsync(path, data).GetAwaiter().GetResult();
                var restored = SessionStore.Load(path);
                Assert(restored.Tabs[0].CurrentUrl == "https://example.com/ssd" && restored.Tabs[0].ScrollY == 700,
                    "Async session storage lost metadata");
                Assert(!File.Exists(path + ".tmp"), "Incomplete atomic temp file was kept");
                var gpu = NativeGpuDiagnostics.Read(); // Optional hardware capability: never crash if DXGI unavailable.
                if (gpu is { } info) Assert(info.Budget > 0, "Native DXGI reported an invalid VRAM budget");
                Console.WriteLine(gpu is { } v ? $"DXGI calling process: {v.Used / 1048576} MiB used, {v.Budget / 1048576} MiB budget" : "DXGI VRAM: unavailable");
                var systemGpu = NativeGpuDiagnostics.SystemDedicatedUsage();
                Console.WriteLine(systemGpu is { } dedicated ? $"PDH dedicated VRAM across adapters: {dedicated / 1048576} MiB" : "PDH GPU Adapter Memory: unavailable");
            });

            Check("Preferences and themes persist and recover invalid settings", () =>
            {
                var settingsPath = Path.Combine(folder, "preferences");
                Directory.CreateDirectory(settingsPath);
                var settings = BrowserPreferences.Load(settingsPath);
                Assert(settings.UnloadAfterMinutes == 5, "Wrong default timeout");
                settings.UnloadAfterMinutes = 15;
                settings.MemoryMode = MemoryMode.Extreme;
                settings.HardwareAcceleration = false;
                settings.NeverHibernateHosts.Add("example.org");
                settings.Save(settingsPath);
                var changed = BrowserPreferences.Load(settingsPath);
                Assert(changed.UnloadAfterMinutes == 15 && changed.MemoryMode == MemoryMode.Extreme &&
                    !changed.HardwareAcceleration && changed.NeverHibernateHosts.Contains("example.org"), "Settings lost");
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
