using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MBrowser;

internal sealed record CefExtensionEntry(string Id, string Name, string Version, string Folder, bool Enabled);

/// <summary>
/// Unpacked MV3 packages for Chromium startup. CEF support varies with its runtime;
/// presence in this registry is not proof that chrome.extension APIs are available.
/// </summary>
internal static class CefExtensionRegistry
{
    internal static string Root(string profile) => Path.Combine(profile, "CefExtensions");
    private static string IndexPath(string profile) => Path.Combine(Root(profile), "extensions-index.json");

    internal static IReadOnlyList<CefExtensionEntry> Read(string profile)
    {
        var root = Path.GetFullPath(Root(profile));
        var path = IndexPath(profile);
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<CefExtensionEntry>>(File.ReadAllText(path)) ?? [];
            return list.Where(item => IsAllowedFolder(root, item.Folder) &&
                File.Exists(Path.Combine(item.Folder, "manifest.json")))
                .Select(item => item with { Id = CefExtensionIdentity.FromFolder(item.Folder) })
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return []; }
    }

    private static bool IsAllowedFolder(string root, string path)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    internal static string EnabledPaths(string profile)
    {
        return string.Join(",", Read(profile).Where(x => x.Enabled && !x.Folder.Contains(','))
            .Select(x => Path.GetFullPath(x.Folder)));
    }

    internal static void Save(string profile, IReadOnlyList<CefExtensionEntry> entries)
    {
        Directory.CreateDirectory(Root(profile));
        string path = IndexPath(profile);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
        File.Move(temporary, path, true);
    }

    internal static CefExtensionEntry Add(string profile, string source)
    {
        var package = ExtensionPackage.Read(source);
        string root = Root(profile);
        Directory.CreateDirectory(root);
        string id = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(source) + "|" + package.Version)))[..24].ToLowerInvariant();
        string destination = Path.Combine(root, id);
        if (Directory.Exists(destination))
            throw new InvalidOperationException("Это расширение уже установлено.");
        ExtensionPackage.Copy(source, destination);
        try
        {
            _ = ExtensionPackage.Read(destination);
            var extensionId = CefExtensionIdentity.FromFolder(destination);
            var entry = new CefExtensionEntry(extensionId, package.Name, package.Version, destination, true);
            var list = Read(profile).Where(e => e.Id != id).Append(entry).ToArray();
            Save(profile, list);
            return entry;
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            throw;
        }
    }
}

internal sealed partial class MainForm
{
    private async Task ShowExtensionsAsync()
    {
        var list = CefExtensionRegistry.Read(DataPath);
        ShowMenuPage("Расширения · Chromium CEF", rows =>
        {
            rows("Установить Manifest V3 из папки…", () => RunUi(InstallExtensionAsync));
            foreach (var item in list)
            {
                rows(item.Name + (item.Enabled ? " · включено" : " · выключено"), () =>
                    ShowMenuPage(item.Name, entry =>
                    {
                        var pages = CefExtensionPages(item);
                        if (pages.Popup is { } popup)
                            entry("Открыть панель расширения", () => ShowCefExtensionPage(item, popup));
                        if (pages.Options is { } options)
                            entry("Открыть настройки расширения", () => ShowCefExtensionPage(item, options));
                        entry(item.Enabled ? "Отключить (после перезапуска)" : "Включить (после перезапуска)", () =>
                        {
                            CefExtensionRegistry.Save(DataPath,
                                CefExtensionRegistry.Read(DataPath)
                                    .Select(e => e.Id == item.Id ? e with { Enabled = !e.Enabled } : e).ToArray());
                            ShowExtensionsAsync().GetAwaiter().GetResult();
                        });
                        entry("Удалить расширение", () =>
                        {
                            var remaining = CefExtensionRegistry.Read(DataPath)
                                .Where(e => e.Id != item.Id).ToArray();
                            CefExtensionRegistry.Save(DataPath, remaining);
                            if (Directory.Exists(item.Folder)) Directory.Delete(item.Folder, true);
                            _status.Text = "Расширение удалено. Перезапустите MBrowser.";
                        });
                        entry("ID: " + item.Id, () => { });
                    }));
            }
            rows("Изменения расширений применяются после перезапуска", () => { });
        });
        await Task.CompletedTask;
    }

    private async Task InstallExtensionAsync()
    {
        using var dialog = new FolderBrowserDialog
        { Description = "Папка расширения Manifest V3 с manifest.json", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string path = Path.GetFullPath(dialog.SelectedPath);
        var package = ExtensionPackage.Read(path);
        bool allowed = await AskInsideWindowAsync("Установить «" + package.Name + "»?",
            "Запрашиваемые разрешения:\n" + string.Join(", ", package.Permissions));
        _permissionChoice = null;
        _permissionRemember = null;
        _permissionPanel = null;
        if (ReferenceEquals(_overlay, _permissionPanel)) CloseOverlay();
        else CloseOverlay();
        if (!allowed) return;
        var entry = CefExtensionRegistry.Add(DataPath, path);
        _status.Text = "Установлено: " + entry.Name + ". Перезапустите MBrowser.";
        await ShowExtensionsAsync();
    }
}
