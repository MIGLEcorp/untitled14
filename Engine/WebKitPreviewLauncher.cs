using System.Diagnostics;

namespace MBrowser.Engine;

/// <summary>No Playwright assembly is referenced or loaded by the main browser.</summary>
internal static class WebKitPreviewLauncher
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    internal static async Task LaunchAsync(IWin32Window owner, string? url, string? dataPath = null)
    {
        string address = string.IsNullOrWhiteSpace(url) ? "https://example.com/" : url;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            MessageBox.Show(owner, "Откройте HTTP/HTTPS-страницу, затем выберите WebKit.", "MBrowser · WebKit");
            return;
        }

        try
        {
            string executable = await FindOrBuildHelperAsync(owner);
            if (dataPath is null)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string modern = Path.Combine(local, "MBrowser");
                string legacy = Path.Combine(local, "FeatherBrowser");
                dataPath = Directory.Exists(modern) || !Directory.Exists(legacy) ? modern : legacy;
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--url");
            start.ArgumentList.Add(uri.AbsoluteUri);
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(Path.Combine(dataPath, "WebKitPreview"));
            start.ArgumentList.Add("--browsers-path");
            start.ArgumentList.Add(Path.Combine(dataPath, "Engines", "WebKit"));
            using var process = Process.Start(start)
                ?? throw new IOException("Windows не запустила процесс WebKit.");
        }
        catch (Exception error)
        {
            MessageBox.Show(owner, "Не удалось запустить экспериментальное окно WebKit.\n\n" + error.Message,
                "MBrowser · WebKit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task<string> FindOrBuildHelperAsync(IWin32Window owner)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "Features", "WebKit", "MBrowser.WebKit.exe");
        if (File.Exists(bundled))
            return bundled;

        // Development builds can create the optional helper on first invocation.
        string? project = FindProject();
        if (project is null)
            throw new FileNotFoundException("Модуль Features\\WebKit отсутствует. Выполните build.ps1 или восстановите полную папку дистрибутива.");
        string output = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", "net10.0-windows", "win-x64", "publish");
        string executable = Path.Combine(output, "MBrowser.WebKit.exe");
        await BuildLock.WaitAsync();
        try
        {
            if (File.Exists(executable))
                return executable;
            if (owner is Control control)
                control.UseWaitCursor = true;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "publish", project, "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-o", output })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить .NET SDK.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string log = await standardOutput + await standardError;
            if (process.ExitCode != 0 || !File.Exists(executable))
                throw new IOException("Не удалось собрать WebKit-модуль. Нужен .NET SDK 10 и доступ к NuGet.\n" + log[^Math.Min(log.Length, 1800)..]);
            return executable;
        }
        finally
        {
            if (owner is Control control && !control.IsDisposed)
                control.UseWaitCursor = false;
            BuildLock.Release();
        }
    }

    private static string? FindProject()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string path = Path.Combine(folder.FullName, "Engine", "WebKitPreview", "MBrowser.WebKit.csproj");
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
