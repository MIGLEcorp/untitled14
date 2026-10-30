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
            string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile=Path.Combine(local,"MBrowser");
            // Never create the new root prematurely before the offline profile migration.
            if (!Directory.Exists(profile) && Directory.Exists(Path.Combine(local,"FeatherBrowser")))
                profile=Path.Combine(local,"FeatherBrowser");
            TranslatorPackage.PrepareAsync(profile,args[1]).GetAwaiter().GetResult(); return 0;
        }
        if (args.Length > 0 && args[0] == "--webview2")
        {
            MessageBox.Show("WebView2 удалён из MBrowser. Запустите без --webview2.",
                "MBrowser CEF-only", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }
        ApplicationConfiguration.Initialize();
        SetCurrentProcessExplicitAppUserModelID("MIGLEcorp.MBrowser");
        if (args.Length==1 && args[0]=="--self-test")
        {
            AttachConsole(-1);
            return CoreTests.Run();
        }

        // CEF-only: WebView2 has been removed from the production executable.
        bool chromeRuntimeSmoke = args.Length == 1 && args[0] == "--cef-chrome-runtime-smoke";
        bool credentialSmoke = args.Length == 1 && args[0] == "--cef-credentials-smoke";
        bool extensionPopupSmoke = args.Length == 1 && args[0] == "--cef-extension-ui-smoke";
        bool extensionSmoke = extensionPopupSmoke || (args.Length == 1 && args[0] == "--cef-extension-smoke");
        bool cefSmoke = chromeRuntimeSmoke || credentialSmoke || extensionSmoke ||
            (args.Length == 1 && (args[0] == "--cef-smoke" || args[0] == "--smoke-test"));
        {
            bool customCef = CefMemoryFeatures.HasVerifiedCustomBuildMarker(AppContext.BaseDirectory);
            string dataPathCef = cefSmoke
                ? Path.Combine(Path.GetTempPath(), "MBrowser-cef-smoke-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MBrowser");
            using var cefSmokeServer = cefSmoke ? new CefSmokeServer(credentialSmoke) : null;
            string[] targets = cefSmoke ? [cefSmokeServer!.Url] :
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
            if (!cefSmoke)
                dataPathCef = UserProfileBootstrap.Prepare(dataPathCef);
            Directory.CreateDirectory(dataPathCef);
            // The previous profile is migrated and verified before CEF is initialized.
            string cefRoot = Path.Combine(dataPathCef, "CEFRuntime");
            Directory.CreateDirectory(cefRoot);
            string cefCache = Path.Combine(cefRoot, "Default");
            Directory.CreateDirectory(cefCache);
            if (extensionSmoke)
            {
                string source = Path.Combine(dataPathCef, "mv3-smoke-source");
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "manifest.json"),
                    """{"manifest_version":3,"name":"MBrowser CEF MV3 Smoke","version":"1.0.0","action":{"default_popup":"popup.html"},"permissions":["storage"],"background":{"service_worker":"worker.js"},"content_scripts":[{"matches":["http://127.0.0.1/*"],"js":["inject.js"],"run_at":"document_start"}]}""");
                File.WriteAllText(Path.Combine(source, "inject.js"),
                    """
                    document.documentElement.dataset.mbrowserMv3Smoke = 'loaded';
                    document.documentElement.dataset.mbrowserMv3Id = chrome.runtime.id;
                    try {
                      chrome.runtime.sendMessage({type:'mv3-ping'}, reply => {
                        if (chrome.runtime.lastError) {
                          document.documentElement.dataset.mbrowserMv3Worker = 'error';
                          return;
                        }
                        document.documentElement.dataset.mbrowserMv3Worker =
                          reply?.pong === true ? 'ok' : 'bad';
                      });
                    } catch (e) { document.documentElement.dataset.mbrowserMv3Worker = 'exception'; }
                    """);
                File.WriteAllText(Path.Combine(source, "worker.js"),
                    """
                    chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
                        if (msg?.type === 'mv3-ping') {
                          chrome.storage.local.set({mbrowserSmoke: true}).then(() => {
                            sendResponse({pong:true});
                          }).catch(() => sendResponse({pong:false}));
                          return true;
                        }
                      });
                    """);
                File.WriteAllText(Path.Combine(source, "popup.html"),
                    """<!doctype html><html><head><title>MBrowser MV3 popup</title></head><body><div id="mbrowser-popup-smoke">popup-ok</div></body></html>""");
                CefExtensionRegistry.Add(dataPathCef, source);
            }
            var cefPrefs = BrowserPreferences.Load(dataPathCef);
            var cefSettings = new CefSharp.WinForms.CefSettings
            {
                RootCachePath = cefRoot,
                CachePath = cefCache
            };
            if (chromeRuntimeSmoke || cefPrefs.ExperimentalChromePasswordManager)
                CefSharp.CefSharpSettings.RuntimeStyle = CefSharp.CefRuntimeStyle.Chrome;
            if (!cefPrefs.HardwareAcceleration)
                cefSettings.CefCommandLineArgs["disable-gpu"] = "";
            // Unpacked Chromium extensions: CEF 154 may not expose the full
            // Manifest V3 service-worker API. Do not claim parity without tests.
            string installedExtensions = CefExtensionRegistry.EnabledPaths(dataPathCef);
            if (!string.IsNullOrWhiteSpace(installedExtensions))
                cefSettings.CefCommandLineArgs["load-extension"] = installedExtensions;
            string cefFeatures = CefMemoryFeatures.CommandLine(cefPrefs, customCef);
            if (cefFeatures.Length > 0)
                cefSettings.CefCommandLineArgs["enable-features"] = cefFeatures;
            // A cold CEF process can be slow while Windows verifies native DLLs.
            // Show real startup status before the synchronous native load.
            using var startup = cefSmoke ? null : new Form
            {
                Text = "MBrowser · запуск Chromium",
                Width = 420, Height = 145, StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false,
                ShowInTaskbar = false, TopMost = true
            };
            if (startup is not null)
            {
                startup.Controls.Add(new ProgressBar
                {
                    Dock = DockStyle.Bottom, Height = 12,
                    Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28
                });
                startup.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    Text = "Загружается Chromium CEF 154…"
                });
                startup.Show();
                startup.Refresh();
                Application.DoEvents();
            }
            var startupWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!CefSharp.Cef.Initialize(cefSettings))
                    throw new InvalidOperationException("Chromium CEF failed to initialize.");
                if (!cefSmoke)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(dataPathCef, "cef-last-startup.txt"),
                            "CEF native initialization: " + startupWatch.ElapsedMilliseconds +
                            " ms" + Environment.NewLine + "Version: 154");
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { LogError(dataPathCef, error); }
                }
            }
            finally { startup?.Close(); }
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
            int smokeExitCode = 1;
            if (cefSmoke)
            {
                cefForm.Shown += (_, _) =>
                {
                    var timer = new System.Windows.Forms.Timer { Interval = 9000 };
                    timer.Tick += async (_, _) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        try
                        {
                            bool agent = await cefForm.CefMemoryAgentReadyForTestAsync();
                            bool mv3 = extensionSmoke && await cefForm.CefExtensionSmokeReadyForTestAsync();
                            bool mv3Worker = extensionSmoke && await cefForm.CefExtensionWorkerReadyForTestAsync();
                            string mv3Id = extensionSmoke ? await cefForm.CefExtensionIdForTestAsync() : "";
                            bool popupReady = extensionPopupSmoke &&
                                await cefForm.CefExtensionPopupReadyForTestAsync(mv3Id);
                            string nativePasswordUi = chromeRuntimeSmoke ?
                                await cefForm.CefNativePasswordUiForTestAsync() : "not tested";
                            var info = cefForm.CefDiagnosticsForTest();
                            File.WriteAllText(Path.Combine(dataPathCef, "cef-smoke.txt"),
                                "CEF_WINDOW_SHOWN=" + cefForm.IsHandleCreated +
                                Environment.NewLine + "ACTIVE_URL=" + cefForm.ActiveTab.CurrentUrl +
                                Environment.NewLine + "CEF_RENDERER_INITIALIZED=" + info.BrowserInitialized +
                                Environment.NewLine + "CEF_RENDERER_ADDRESS=" + info.Address +
                                Environment.NewLine + "CEF_MEMORY_AGENT_LOADED=" + agent +
                                Environment.NewLine + "CEF_MV3_CONTENT_SCRIPT_LOADED=" + mv3 +
                                Environment.NewLine + "CEF_MV3_WORKER_MESSAGE_OK=" + mv3Worker +
                                Environment.NewLine + "CEF_MV3_RUNTIME_ID=" + mv3Id +
                                Environment.NewLine + "CEF_MV3_POPUP_PAGE_OK=" + popupReady +
                                Environment.NewLine + "CEF_NATIVE_PASSWORD_UI=" + nativePasswordUi +
                                Environment.NewLine + "CEF_LOGIN_BRIDGE_MESSAGES=" + cefForm.CefLoginBridgeMessagesForTest +
                                Environment.NewLine + "CEF_RAW_JS_MESSAGES=" + cefForm.CefRawJsMessagesForTest +
                                Environment.NewLine + "CEF_RAW_JS_MESSAGE_INFO=" + cefForm.CefRawJsMessageInfoForTest +
                                Environment.NewLine + "CEF_LOGIN_BRIDGE_DIAGNOSTICS=" + (credentialSmoke ? await cefForm.CefCredentialDiagnosticsForTestAsync() : "not tested") +
                                Environment.NewLine + "CEF_LIVE_VIEWS=" + cefForm.LiveViewCount +
                                Environment.NewLine + "CEF_GROUP_PROCESSES=" + cefForm.ReadMemory().ProcessCount);
                            smokeExitCode = (!info.BrowserInitialized || !agent ||
                                (extensionSmoke && (!mv3 || !mv3Worker)) ||
                                (extensionPopupSmoke && !popupReady) ||
                                (credentialSmoke && cefForm.CefLoginBridgeMessagesForTest == 0)) ? 1 : 0;
                        }
                        catch (Exception ex)
                        {
                            LogError(dataPathCef, ex);
                            smokeExitCode = 1;
                        }
                        finally { cefForm.Close(); }
                    };
                    timer.Start();
                };
            }
            Application.Run(cefForm);
            return cefSmoke ? smokeExitCode : Environment.ExitCode;
        }

    }

    internal static void LogError(string dataPath, Exception error)
    {
        try { File.AppendAllText(Path.Combine(dataPath, "errors.log"), $"{DateTimeOffset.Now:u} {error}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
