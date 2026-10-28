using System.Runtime.InteropServices;

namespace MBrowser;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length==1 && args[0]=="--register-browser") { BrowserRegistration.Register(Application.ExecutablePath); return 0; }
        if(args.Length==1 && args[0]=="--unregister-browser") { BrowserRegistration.Unregister(Application.ExecutablePath); return 0; }
        if(args.Length==2 && args[0]=="--prepare-translator")
        {
            string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FeatherBrowser");
            TranslatorPackage.PrepareAsync(profile,args[1]).GetAwaiter().GetResult(); return 0;
        }
        ApplicationConfiguration.Initialize();
        SetCurrentProcessExplicitAppUserModelID("MIGLEcorp.MBrowser");
        if (args.Length==1 && args[0]=="--self-test")
        {
            AttachConsole(-1);
            return CoreTests.Run();
        }

        // v4.3: embedded Chromium CEF is the primary renderer.
        // --webview2 restores the previous engine without touching existing user data.
        // --smoke-test keeps the original WebView2 regression route.
        bool cefSmoke = args.Length == 1 && args[0] == "--cef-smoke";
        bool cefMain = cefSmoke || (args.Length == 0 ||
            (args[0] != "--webview2" && args[0] != "--smoke-test"));
        if (cefMain)
        {
            bool customCef = CefMemoryFeatures.HasVerifiedCustomBuildMarker(AppContext.BaseDirectory);
            string dataPathCef = cefSmoke
                ? Path.Combine(Path.GetTempPath(), "MBrowser-cef-smoke-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FeatherBrowser");
            string[] targets = cefSmoke ? ["https://example.com"] :
                BrowserDocuments.ParseArguments(args.Length > 0 && args[0] == "--cef"
                    ? args.Skip(1).ToArray() : args);
            // Use the SAME installation mutex as v4.2.1. Never allow two versions
            // to write the shared session and preferences concurrently.
            using var mutexCef = new Mutex(true,
                cefSmoke ? "Local\\MBrowserCefSmoke-" + Guid.NewGuid() : "Local\\FeatherBrowser",
                out bool cefFirst);
            if (!cefFirst)
            {
                if (!BrowserActivation.Send(targets))
                    MessageBox.Show("MBrowser уже открыт. Закройте старую версию перед запуском обновления.", "MBrowser");
                return 0;
            }
            Directory.CreateDirectory(dataPathCef);
            // WebView2 userdata/profile and legacy passwords remain untouched.
            string cefRoot = Path.Combine(dataPathCef, "CEFRuntime");
            Directory.CreateDirectory(cefRoot);
            string cefCache = Path.Combine(cefRoot, "Default");
            Directory.CreateDirectory(cefCache);
            var cefSettings = new CefSharp.WinForms.CefSettings
            {
                RootCachePath = cefRoot,
                CachePath = cefCache
            };
            var cefPrefs = BrowserPreferences.Load(dataPathCef);
            string cefFeatures = CefMemoryFeatures.CommandLine(cefPrefs, customCef);
            if (cefFeatures.Length > 0)
                cefSettings.CefCommandLineArgs["enable-features"] = cefFeatures;
            if (!CefSharp.Cef.Initialize(cefSettings))
                throw new InvalidOperationException("Chromium CEF failed to initialize.");
            Application.ThreadException += (_, e) => LogError(dataPathCef, e.Exception);
            using var cefForm = new MainForm(dataPathCef, cefSmoke, targets,
                cefEngine: true, customCef: customCef);
            _ = cefForm.Handle;
            using var cefActivation = cefSmoke ? null : new BrowserActivation(incoming =>
            {
                try
                {
                    if (!cefForm.IsDisposed && cefForm.IsHandleCreated)
                        cefForm.BeginInvoke(() => cefForm.AcceptShellTargets(incoming));
                }
                catch (InvalidOperationException) when (cefForm.IsDisposed || !cefForm.IsHandleCreated) { }
            });
            if (cefSmoke)
            {
                cefForm.Shown += (_, _) =>
                {
                    var timer = new System.Windows.Forms.Timer { Interval = 9000 };
                    timer.Tick += async (_, _) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        bool agent = await cefForm.CefMemoryAgentReadyForTestAsync();
                        var info = cefForm.CefDiagnosticsForTest();
                        File.WriteAllText(Path.Combine(dataPathCef, "cef-smoke.txt"),
                            "CEF_WINDOW_SHOWN=" + cefForm.IsHandleCreated +
                            Environment.NewLine + "ACTIVE_URL=" + cefForm.ActiveTab.CurrentUrl +
                            Environment.NewLine + "CEF_RENDERER_INITIALIZED=" + info.BrowserInitialized +
                            Environment.NewLine + "CEF_RENDERER_ADDRESS=" + info.Address +
                            Environment.NewLine + "CEF_MEMORY_AGENT_LOADED=" + agent +
                            Environment.NewLine + "CEF_LIVE_VIEWS=" + cefForm.LiveViewCount +
                            Environment.NewLine + "CEF_GROUP_PROCESSES=" + cefForm.ReadMemory().ProcessCount);
                        cefForm.Close();
                    };
                    timer.Start();
                };
            }
            Application.Run(cefForm);
            return Environment.ExitCode;
        }

        bool smoke = args.Length==1 && args[0]=="--smoke-test";
        string dataPath = smoke
            ? Path.Combine(Path.GetTempPath(), "MBrowser-smoke-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FeatherBrowser");
        using var instance = new Mutex(true, smoke ? "Local\\MBrowserSmoke-" + Guid.NewGuid() : "Local\\FeatherBrowser", out bool first);
        if (!first)
        {
            if(!BrowserActivation.Send(BrowserDocuments.ParseArguments(args))) MessageBox.Show("Не удалось передать адрес открытому окну MBrowser.","MBrowser");
            return 0;
        }

        Directory.CreateDirectory(dataPath);
        Application.ThreadException += (_, e) => LogError(dataPath, e.Exception);
        using var form = new MainForm(dataPath, smoke,smoke ? [] : BrowserDocuments.ParseArguments(args));
        _ = form.Handle;
        using var activation = smoke ? null : new BrowserActivation(targets =>
        {
            try { if(!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(() => form.AcceptShellTargets(targets)); }
            catch(InvalidOperationException) when(form.IsDisposed || !form.IsHandleCreated) { }
        });
        if (smoke)
            form.Shown += async (_, _) => await SmokeTests.RunAsync(form, dataPath);
        Application.Run(form);
        return Environment.ExitCode;
    }

    internal static void LogError(string dataPath, Exception error)
    {
        try { File.AppendAllText(Path.Combine(dataPath, "errors.log"), $"{DateTimeOffset.Now:u} {error}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
