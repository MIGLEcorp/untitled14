using System.Text.Json;

namespace MBrowser;

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
