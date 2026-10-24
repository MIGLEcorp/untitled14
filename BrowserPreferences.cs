using System.Text.Json;

namespace Feather;

/// <summary>Small startup settings. Heavy feature stores are opened only from their menus.</summary>
internal sealed class BrowserPreferences
{
    public int UnloadAfterMinutes { get; set; } = 5;
    public int MaximumResidentTabs { get; set; } = 8;

    internal static BrowserPreferences Load(string dataPath)
    {
        try
        {
            var path = Path.Combine(dataPath, "preferences.json");
            if (!File.Exists(path)) return new();
            var settings = JsonSerializer.Deserialize<BrowserPreferences>(File.ReadAllText(path)) ?? new();
            settings.UnloadAfterMinutes = Math.Clamp(settings.UnloadAfterMinutes, 1, 120);
            settings.MaximumResidentTabs = Math.Clamp(settings.MaximumResidentTabs, 1, 32);
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

internal static class TabUnloadPolicy
{
    internal static bool ShouldUnload(bool isActive, bool protectedActivity, DateTimeOffset lastUsed,
        DateTimeOffset now, TimeSpan timeout) => !isActive && !protectedActivity && now - lastUsed >= timeout;
}
