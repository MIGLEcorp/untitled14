using System.Text.Json;
using System.Text.Json.Serialization;

namespace MBrowser;

internal enum BrowserTheme { Anime, Dark, Light }
internal sealed record ThemeColors(Color Background, Color Surface, Color Raised, Color Text, Color Muted, Color Accent, Color Sky);
internal enum ThemeArea { Home, Tabs, Toolbar, Bookmarks, Menu }
internal sealed record ThemeLayer(string File = "", string Fit = "cover", int Tint = 150);
internal sealed record ThemeDocument
{
    public string Name { get; init; } = "Pink kawaii";
    public BrowserTheme Base { get; init; } = BrowserTheme.Anime;
    public Dictionary<string, string> Colors { get; init; } = [];
    public Dictionary<ThemeArea, ThemeLayer> Layers { get; init; } = [];
}

/// <summary>One small settings file; themes use native colors and vector artwork.</summary>
internal static class ThemeSettings
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly IReadOnlyDictionary<BrowserTheme, ThemeColors> Themes = new Dictionary<BrowserTheme, ThemeColors>
    {
        [BrowserTheme.Anime] = new(Color.FromArgb(21, 19, 35), Color.FromArgb(32, 28, 49), Color.FromArgb(48, 41, 68), Color.FromArgb(249, 243, 252), Color.FromArgb(180, 168, 199), Color.FromArgb(242, 176, 210), Color.FromArgb(34, 27, 56)),
        [BrowserTheme.Dark] = new(Color.FromArgb(16, 22, 30), Color.FromArgb(24, 33, 43), Color.FromArgb(34, 46, 58), Color.FromArgb(235, 242, 245), Color.FromArgb(151, 168, 181), Color.FromArgb(111, 227, 181), Color.FromArgb(20, 33, 44)),
        [BrowserTheme.Light] = new(Color.FromArgb(247, 245, 249), Color.FromArgb(237, 232, 241), Color.FromArgb(226, 218, 233), Color.FromArgb(42, 32, 53), Color.FromArgb(107, 95, 120), Color.FromArgb(135, 64, 106), Color.FromArgb(232, 224, 240))
    };

    internal static ThemeDocument Document { get; private set; } = Preset(BrowserTheme.Anime);
    internal static BrowserTheme Current => Document.Base;
    internal static string DataPath { get; private set; } = "";
    private static ThemeColors? _previousColors;
    internal static ThemeDocument Preset(BrowserTheme theme) => new()
    {
        Base = theme, Name = theme == BrowserTheme.Anime ? "Pink kawaii" : theme.ToString(),
        Layers = Enum.GetValues<ThemeArea>().ToDictionary(area => area, area => new ThemeLayer(
            theme == BrowserTheme.Anime ? area == ThemeArea.Home ? "kawaii-home.jpg" : area == ThemeArea.Menu ? "kawaii-menu.jpg" : "kawaii-chrome.jpg" : "",
            "cover", area == ThemeArea.Home ? 0 : area == ThemeArea.Menu ? 135 : 115))
    };
    internal static ThemeColors Colors
    {
        get
        {
            var c = Themes[Current];
            Color Read(string key, Color fallback) => Document.Colors.TryGetValue(key, out string? value) &&
                value.Length == 7 && value[0] == '#' && int.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb)
                ? Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255) : fallback;
            return new(Read("Background", c.Background), Read("Surface", c.Surface), Read("Raised", c.Raised),
                Read("Text", c.Text), Read("Muted", c.Muted), Read("Accent", c.Accent), c.Sky);
        }
    }
    internal static ThemeLayer Layer(ThemeArea area) => Document.Layers.GetValueOrDefault(area) ?? new();
    internal static void Preview(ThemeDocument document) { _previousColors = Colors; Document = Normalize(document); ThemeMedia.ReleaseAll(); }
    internal static ThemeDocument Normalize(ThemeDocument doc) => doc with
    {
        Name = (doc.Name ?? "Моя тема")[..Math.Min(80, (doc.Name ?? "Моя тема").Length)],
        Base = Enum.IsDefined(doc.Base) ? doc.Base : BrowserTheme.Anime,
        Colors = doc.Colors?.Where(x => x.Value is { Length: 7 } && x.Value[0] == '#' &&
            int.TryParse(x.Value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _)).Take(6).ToDictionary() ?? [],
        Layers = (doc.Layers ?? []).Where(x => Enum.IsDefined(x.Key) && x.Value is not null).ToDictionary(x => x.Key,
            x => x.Value with { File = Path.GetFileName(x.Value.File ?? ""), Tint = Math.Clamp(x.Value.Tint, 0, 255),
                Fit = x.Value.Fit is "contain" or "tile" ? x.Value.Fit : "cover" })
    };

    internal static void Load(string dataPath)
    {
        DataPath = dataPath;
        ThemeMedia.ReleaseAll();
        Document = Preset(BrowserTheme.Anime);
        string path = Path.Combine(dataPath, "theme.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32768) return;
            string json = File.ReadAllText(path);
            using var root = JsonDocument.Parse(json);
            if (root.RootElement.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<BrowserTheme>(root.RootElement.GetString(), true, out var theme) && Enum.IsDefined(theme)) Document = Preset(theme);
            }
            else if (JsonSerializer.Deserialize<ThemeDocument>(json, JsonOptions) is { } doc) Document = Normalize(doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal static void Select(BrowserTheme theme, string dataPath)
    {
        if (!Themes.ContainsKey(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        Save(Preset(theme), dataPath);
    }

    internal static void Save(ThemeDocument document, string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        string path = Path.Combine(dataPath, "theme.json");
        string temporary = path + ".tmp";
        var normalized = Normalize(document);
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporary, path, true);
        DataPath = dataPath;
        Preview(normalized);
    }

    internal static Color Translate(Color color)
    {
        if (color.IsEmpty || color.A == 0) return color;
        foreach (var previous in (_previousColors is { } prior ? new[] { prior }.Concat(Themes.Values) : Themes.Values))
        {
            if (color.ToArgb() == previous.Background.ToArgb()) return Colors.Background;
            if (color.ToArgb() == previous.Surface.ToArgb()) return Colors.Surface;
            if (color.ToArgb() == previous.Raised.ToArgb()) return Colors.Raised;
            if (color.ToArgb() == previous.Text.ToArgb()) return Colors.Text;
            if (color.ToArgb() == previous.Muted.ToArgb()) return Colors.Muted;
            if (color.ToArgb() == previous.Accent.ToArgb()) return Colors.Accent;
        }
        return color;
    }
}
