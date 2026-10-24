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
        ApplicationConfiguration.Initialize();
        SetCurrentProcessExplicitAppUserModelID("MIGLEcorp.MBrowser");
        if (args.Contains("--self-test"))
        {
            AttachConsole(-1);
            return CoreTests.Run();
        }

        bool smoke = args.Contains("--smoke-test");
        string dataPath = smoke
            ? Path.Combine(Path.GetTempPath(), "MBrowser-smoke-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FeatherBrowser");
        using var instance = new Mutex(true, smoke ? "Local\\MBrowserSmoke-" + Guid.NewGuid() : "Local\\FeatherBrowser", out bool first);
        if (!first)
        {
            MessageBox.Show("MBrowser уже открыт. Используйте его окно на панели задач.", "MBrowser");
            return 0;
        }

        Directory.CreateDirectory(dataPath);
        Application.ThreadException += (_, e) => LogError(dataPath, e.Exception);
        using var form = new MainForm(dataPath, smoke);
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
