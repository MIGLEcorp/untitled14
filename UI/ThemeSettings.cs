using System.Text.Json;

namespace Feather;

internal enum BrowserTheme { Anime, Dark, Light }
internal sealed record ThemeColors(Color Background, Color Surface, Color Raised, Color Text, Color Muted, Color Accent, Color Sky);

/// <summary>One small settings file; themes use native colors and vector artwork.</summary>
internal static class ThemeSettings
{
    private static readonly IReadOnlyDictionary<BrowserTheme, ThemeColors> Themes = new Dictionary<BrowserTheme, ThemeColors>
    {
        [BrowserTheme.Anime] = new(Color.FromArgb(21, 19, 35), Color.FromArgb(32, 28, 49), Color.FromArgb(48, 41, 68), Color.FromArgb(249, 243, 252), Color.FromArgb(180, 168, 199), Color.FromArgb(242, 176, 210), Color.FromArgb(34, 27, 56)),
        [BrowserTheme.Dark] = new(Color.FromArgb(16, 22, 30), Color.FromArgb(24, 33, 43), Color.FromArgb(34, 46, 58), Color.FromArgb(235, 242, 245), Color.FromArgb(151, 168, 181), Color.FromArgb(111, 227, 181), Color.FromArgb(20, 33, 44)),
        [BrowserTheme.Light] = new(Color.FromArgb(247, 245, 249), Color.FromArgb(237, 232, 241), Color.FromArgb(226, 218, 233), Color.FromArgb(42, 32, 53), Color.FromArgb(107, 95, 120), Color.FromArgb(135, 64, 106), Color.FromArgb(232, 224, 240))
    };

    internal static BrowserTheme Current { get; private set; } = BrowserTheme.Anime;
    internal static ThemeColors Colors => Themes[Current];

    internal static void Load(string dataPath)
    {
        Current = BrowserTheme.Anime;
        string path = Path.Combine(dataPath, "theme.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return;
            string? value = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
            if (Enum.TryParse<BrowserTheme>(value, true, out var theme) && Enum.IsDefined(theme)) Current = theme;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal static void Select(BrowserTheme theme, string dataPath)
    {
        if (!Themes.ContainsKey(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        Directory.CreateDirectory(dataPath);
        string path = Path.Combine(dataPath, "theme.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(theme.ToString()));
        File.Move(temporary, path, true);
        Current = theme;
    }

    internal static Color Translate(Color color)
    {
        if (color.IsEmpty || color.A == 0) return color;
        foreach (var previous in Themes.Values)
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
