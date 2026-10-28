namespace MBrowser;

/// <summary>
/// CEF 154 features available in stock Chromium 154. The custom decoded-image
/// feature needs a custom libcef build; silently pretending otherwise is not allowed.
/// Feature switches take effect when the CEF process initializes, not per navigation.
/// </summary>
internal static class CefMemoryFeatures
{
    internal const string CustomManifestFile = "MBrowser.CustomCef154.marker";
    internal const string CustomManifestTag = "MBROWSER_CUSTOM_CEF154_COMPACT_CACHE_V1";

    internal static bool HasVerifiedCustomBuildMarker(string appDirectory)
    {
        string dll = Path.Combine(appDirectory, "libcef.dll");
        string marker = Path.Combine(appDirectory, CustomManifestFile);
        try
        {
            return File.Exists(dll) && File.Exists(marker) &&
                File.ReadAllText(marker).Trim() == CustomManifestTag;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return false; }
    }

    internal static IReadOnlyList<string> Enabled(BrowserPreferences preferences, bool customLibCefAvailable)
    {
        var result = new List<string>();
        if (preferences.NativeSmallerRasterArea) result.Add("SmallerInterestArea");
        if (preferences.NativeReclaimTilesWhenIdle) result.Add("ReclaimPrepaintTilesWhenIdle");
        if (preferences.NativeReclaimOldPrepaintTiles) result.Add("ReclaimOldPrepaintTiles");
        if (customLibCefAvailable && preferences.NativeCompactDecodedImageCache)
            result.Add("MBrowserCompactDecodedImageCache");
        return result;
    }

    internal static string CommandLine(BrowserPreferences preferences, bool customLibCefAvailable) =>
        string.Join(",", Enabled(preferences, customLibCefAvailable));
}
