namespace Feather.Features;

public sealed record BrowserProfile(string Browser, string Name, string DirectoryPath, string? BookmarkPath, string? CookiePath, bool IsFirefox)
{
    public override string ToString() => Browser + " — " + Name;
}

/// <summary>Lists known profile directories only. Data is opened after a user chooses an import action.</summary>
public static class BrowserProfiles
{
    public static IReadOnlyList<BrowserProfile> Discover()
    {
        var profiles = new List<BrowserProfile>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var (name, root) in new[]
        {
            ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data")),
            ("Chromium", Path.Combine(local, "Chromium", "User Data"))
        })
        {
            foreach (string directory in Subdirectories(root))
            {
                string leaf = Path.GetFileName(directory);
                if (leaf != "Default" && !leaf.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                Add(name, leaf, directory, false);
            }
        }
        foreach (var (name, root) in new[]
        {
            ("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
            ("Opera GX", Path.Combine(roaming, "Opera Software", "Opera GX Stable"))
        })
        {
            if (Directory.Exists(root)) Add(name, "Default", root, false);
            foreach (string directory in Subdirectories(root).Where(directory => Path.GetFileName(directory) == "Default")) Add(name, "Default", directory, false);
        }
        foreach (string directory in Subdirectories(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles")))
            Add("Firefox", Path.GetFileName(directory), directory, true);
        return profiles;

        void Add(string browser, string name, string directory, bool firefox)
        {
            string bookmarks = Path.Combine(directory, firefox ? "places.sqlite" : "Bookmarks");
            string cookies = Path.Combine(directory, firefox ? "cookies.sqlite" : "Network\\Cookies");
            if (!File.Exists(cookies) && !firefox) cookies = Path.Combine(directory, "Cookies");
            profiles.Add(new BrowserProfile(browser, name, directory, File.Exists(bookmarks) ? bookmarks : null, File.Exists(cookies) ? cookies : null, firefox));
        }
    }

    private static IEnumerable<string> Subdirectories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
