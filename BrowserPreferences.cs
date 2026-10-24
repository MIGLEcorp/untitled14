using System.Text.Json;

namespace MBrowser;

/// <summary>Small startup settings. Heavy feature stores are opened only from their menus.</summary>
internal sealed class BrowserPreferences
{
    public int UnloadAfterMinutes { get; set; } = 5;
    public bool NetworkSuggestions { get; set; } = true;
    public string Wallpaper { get; set; } = "rain-city.jpg";

    internal static BrowserPreferences Load(string dataPath)
    {
        try
        {
            var path = Path.Combine(dataPath, "preferences.json");
            if (!File.Exists(path)) return new();
            var settings = JsonSerializer.Deserialize<BrowserPreferences>(File.ReadAllText(path)) ?? new();
            settings.UnloadAfterMinutes = Math.Clamp(settings.UnloadAfterMinutes, 1, 120);
            if (settings.Wallpaper is not ("rain-city.jpg" or "save-point.jpg")) settings.Wallpaper = "rain-city.jpg";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save(string dataPath)
    {
        var path = Path.Combine(dataPath, "preferences.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

internal static class TabFreezePolicy
{
    internal static bool ShouldFreeze(bool foreground, bool protectedActivity, DateTimeOffset inactiveSince,
        DateTimeOffset lastNetworkActivity, DateTimeOffset now, TimeSpan timeout) => !foreground && !protectedActivity &&
        now - inactiveSince >= timeout && now - lastNetworkActivity >= timeout;
}
