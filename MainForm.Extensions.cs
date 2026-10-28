using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MBrowser;

internal sealed partial class MainForm
{
    private CoreWebView2EnvironmentOptions EnvironmentOptions() => new()
    {
        Language = "ru-RU", AreBrowserExtensionsEnabled = true,
        // Native smoke windows are hidden by the test runner. Keep their render/idle queues running.
        AdditionalBrowserArguments = GraphicsOptimizationManager.BrowserArguments(_preferences.HardwareAcceleration, _smoke)
    };

    private async Task WithProfileCoreAsync(Func<CoreWebView2, Task> action)
    {
        WebView2? temporary = null;
        try
        {
            var core = ActiveCore ?? _runtimes.Values.FirstOrDefault()?.View.CoreWebView2;
            if (core is null)
            {
                _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataPath, "WebView2"), EnvironmentOptions());
                temporary = new WebView2 { Visible = false, Size = new Size(1, 1) };
                _viewport.Controls.Add(temporary);
                await temporary.EnsureCoreWebView2Async(_environment);
                core = temporary.CoreWebView2;
            }
            await action(core);
        }
        finally { if (temporary is not null) { _viewport.Controls.Remove(temporary); temporary.Dispose(); } }
    }

    private async Task ShowExtensionsAsync()
    {
        await WithProfileCoreAsync(async core =>
        {
            var extensions = await core.Profile.GetBrowserExtensionsAsync();
            var rows = extensions.Select(extension => (extension.Id, extension.Name, extension.IsEnabled)).ToArray();
            ShowMenuPage("Расширения · Manifest V3", add =>
            {
                add("Установить из папки…", () => RunUi(InstallExtensionAsync));
                foreach (var extension in rows)
                {
                    add(extension.Name + (extension.IsEnabled ? " · вкл." : " · выкл."), () => ShowMenuPage(extension.Name, entry =>
                    {
                        entry(extension.IsEnabled ? "Отключить" : "Включить", () => RunUi(() => WithProfileCoreAsync(async current =>
                        { var installed = (await current.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(item => item.Id == extension.Id); if (installed is not null) await installed.EnableAsync(!installed.IsEnabled); })));
                        entry("Удалить", () => RunUi(() => WithProfileCoreAsync(async current =>
                        { var installed = (await current.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(item => item.Id == extension.Id); if (installed is not null) await installed.RemoveAsync(); })));
                        entry("ID: " + extension.Id, () => { });
                    }));
                }
            });
        });
    }

    private async Task InstallExtensionAsync()
    {
        using var picker = new FolderBrowserDialog { Description = "Папка расширения с manifest.json", UseDescriptionForTitle = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string source = Path.GetFullPath(picker.SelectedPath);
        var manifest = ExtensionPackage.Read(source);
        byte[] reviewedManifest = SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "manifest.json")));
        bool allowed = await AskInsideWindowAsync("Установить «" + manifest.Name + "»?", "Разрешения:\n" + string.Join(", ", manifest.Permissions));
        CloseOverlay();
        _permissionChoice = null;
        _permissionRemember = null;
        _permissionPanel = null;
        if (!allowed) return;
        string destination = Path.Combine(DataPath, "Extensions", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + manifest.Version)))) + "-" + Guid.NewGuid().ToString("N")[..8];
        ExtensionPackage.Copy(source, destination);
        try
        {
            if (!reviewedManifest.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination, "manifest.json")))))
                throw new InvalidDataException("Manifest изменился во время установки. Проверьте разрешения заново.");
            await WithProfileCoreAsync(async core => { await core.Profile.AddBrowserExtensionAsync(destination); });
            await ShowExtensionsAsync();
        }
        catch
        {
            string extensionRoot = Path.GetFullPath(Path.Combine(DataPath, "Extensions")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(destination).StartsWith(extensionRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(destination))
                Directory.Delete(destination, true);
            throw;
        }
    }
}

internal sealed record ExtensionPackage(string Name, string Version, string[] Permissions)
{
    internal static ExtensionPackage Read(string directory)
    {
        string path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Не найден корректный manifest.json.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("manifest_version").GetInt32() != 3) throw new InvalidDataException("Поддерживается Manifest V3.");
        var permissions = new List<string>();
        foreach (string key in new[] { "permissions", "host_permissions", "optional_permissions", "optional_host_permissions" })
            if (root.TryGetProperty(key, out var list)) permissions.AddRange(list.EnumerateArray().Select(item => item.GetString()!));
        if (root.TryGetProperty("content_scripts", out var scripts))
            foreach (var script in scripts.EnumerateArray())
                if (script.TryGetProperty("matches", out var matches)) permissions.AddRange(matches.EnumerateArray().Select(item => item.GetString()!));
        return new(root.GetProperty("name").GetString()!, root.GetProperty("version").GetString()!, permissions.Distinct().ToArray());
    }
    internal static void Copy(string source, string destination)
    {
        var files = new List<(string Source, string Relative)>();
        long total = 0;
        void Collect(string directory, int depth = 0)
        {
            if (depth > 32) throw new InvalidDataException("Слишком много вложенных папок в расширении.");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в пакете расширения не поддерживаются.");
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в пакете расширения не поддерживаются.");
                total += new FileInfo(file).Length;
                if (total > 100 * 1024 * 1024 || files.Count >= 10000) throw new InvalidDataException("Пакет слишком большой.");
                files.Add((file, Path.GetRelativePath(source, file)));
            }
            foreach (string child in Directory.EnumerateDirectories(directory)) Collect(child, depth + 1);
        }
        Collect(source);
        foreach (var file in files)
        {
            string target = Path.Combine(destination, file.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.Source, target);
        }
    }
}
