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
