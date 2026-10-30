using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private void ShowBookmarks() => OpenFeature("ShowBookmarks", this, ActiveTab.CurrentUrl,
        ActiveTab.Title, (Action<string>)(url => RunUi(() => OpenTabAsync(url))));

    private void ShowPasswords() => OpenFeature("ShowPasswords", this);

    private void ShowImport() => OpenFeature("ShowImport", this,
        (Func<string, Task<string>>)ApplyCookiesAsync,
        (Action<string>)(url => RunUi(() => OpenTabAsync(url))));

    private void OpenFeature(string method, params object?[] arguments)
    {
        try { FeatureModule.Invoke(DataPath, method, arguments); RefreshBookmarks(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task FillPasswordAsync() => CefSuggestPasswordsAsync();

    private sealed record CookieImportEntry(string Domain, string Name, string Value,
        string Path, bool IsSecure, bool IsHttpOnly, DateTimeOffset? ExpiresUtc,
        string SameSite, CookiePartitionKey? PartitionKey = null);

    private sealed record CookiePartitionKey(string TopLevelSite, bool HasCrossSiteAncestor);

    internal Task<string> ApplyCookiesAsync(string json) => ApplyCefCookiesAsync(json);

    internal void SelectTheme(BrowserTheme theme)
    {
        ThemeSettings.Select(theme, DataPath);
        ApplyCurrentTheme();
    }

    private void ApplyCurrentTheme()
    {
        Palette.Apply(this);
        if (_menu is not null) Palette.Apply(_menu);
        _home.RefreshTheme();
        foreach (var runtime in _cefRuntimes.Values)
            if (!runtime.View.IsDisposed) runtime.View.BackColor = Palette.Background;
    }

    private void SetUnloadTimeout(int minutes)
    {
        _preferences.UnloadAfterMinutes = minutes;
        try
        {
            _preferences.Save(DataPath);
            _status.Text = $"Фоновые вкладки будут выгружаться через {minutes} мин.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex); }
    }
}
