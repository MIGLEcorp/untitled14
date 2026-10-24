using System.IO.Compression;
using System.Text.Json;

namespace MBrowser;

/// <summary>Portable themes contain JSON and images only; no scripts, URLs or executable content.</summary>
internal static class ThemePackage
{
    internal static void Export(string destination, ThemeDocument document)
    {
        // Write to a sibling first so a failed export never replaces an existing theme.
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var layers = new Dictionary<ThemeArea, ThemeLayer>(document.Layers);
                var added = new HashSet<string>();
                foreach (var (area, layer) in document.Layers)
                {
                    string path = ThemeMedia.Resolve(layer.File);
                    if (string.IsNullOrEmpty(layer.File) || !File.Exists(path)) { layers[area] = layer with { File = "" }; continue; }
                    if (added.Add(layer.File)) archive.CreateEntryFromFile(path, "media/" + layer.File);
                }
                using var writer = new StreamWriter(archive.CreateEntry("theme.json").Open());
                writer.Write(JsonSerializer.Serialize(document with { Layers = layers }, ThemeSettings.JsonOptions));
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static ThemeDocument Import(string source, string dataPath)
    {
        if (new FileInfo(source).Length > 85 * 1024 * 1024) throw new InvalidDataException("Пакет темы слишком большой.");
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > 7 || archive.Entries.Any(x => x.Length > 16 * 1024 * 1024) || archive.Entries.Sum(x => x.Length) > 85 * 1024 * 1024) throw new InvalidDataException("Слишком много данных в теме.");
        var settings = archive.GetEntry("theme.json");
        if (settings is null || settings.Length > 32768) throw new InvalidDataException("Нет допустимого theme.json.");
        using var reader = new StreamReader(settings.Open());
        var document = ThemeSettings.Normalize(JsonSerializer.Deserialize<ThemeDocument>(reader.ReadToEnd(), ThemeSettings.JsonOptions) ?? throw new InvalidDataException("Пустая тема."));
        var layers = new Dictionary<ThemeArea, ThemeLayer>(document.Layers);
        var imported = new Dictionary<string, string>();
        foreach (var (area, layer) in document.Layers)
        {
            if (layer.File.Length == 0) continue;
            if (!imported.TryGetValue(layer.File, out string? file))
            {
                var entry = archive.GetEntry("media/" + layer.File);
                // 4.0 packages embedded the former built-in filenames. Normalize
                // migrates settings, but the archive's original entry still exists.
                if (entry is null)
                {
                    string legacy = layer.File switch { "sakura-home.png" => "kawaii-home.jpg", "sakura-menu.png" => "kawaii-menu.jpg", "sakura-header.png" => "kawaii-chrome.jpg", _ => layer.File };
                    entry = archive.GetEntry("media/" + legacy);
                }
                if (entry is null || entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("В теме отсутствует изображение или оно слишком большое.");
                string temporary = Path.GetTempFileName();
                try { entry.ExtractToFile(temporary, true); file = ThemeMedia.Import(temporary, dataPath); imported[layer.File] = file; }
                finally { File.Delete(temporary); }
            }
            layers[area] = layer with { File = file };
        }
        return document with { Layers = layers };
    }
}
