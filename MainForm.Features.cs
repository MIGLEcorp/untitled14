using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Feather;

internal sealed partial class MainForm
{
    private void ShowBookmarks() => OpenFeature("ShowBookmarks", this, ActiveTab.CurrentUrl, ActiveTab.Title,
        (Action<string>)(url => RunUi(() => OpenTabAsync(url))));

    private void ShowPasswords() => OpenFeature("ShowPasswords", this);

    private void ShowImport() => OpenFeature("ShowImport", this, (Func<string, Task<string>>)ApplyCookiesAsync,
        (Action<string>)(url => RunUi(() => OpenTabAsync(url))));

    private void OpenFeature(string method, params object?[] arguments)
    {
        try { FeatureModule.Invoke(DataPath, method, arguments); RefreshBookmarks(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task FillPasswordAsync() => SuggestPasswordsAsync();

    private sealed record CookieImportEntry(string Domain, string Name, string Value, string Path,
        bool IsSecure, bool IsHttpOnly, DateTimeOffset? ExpiresUtc, string SameSite, CookiePartitionKey? PartitionKey = null);

    private sealed record CookiePartitionKey(string TopLevelSite, bool HasCrossSiteAncestor);

    internal async Task<string> ApplyCookiesAsync(string json)
    {
        string report = "Импорт отменён: браузер закрывается.";
        await TransitionAsync(async () =>
        {
            var cookies = JsonSerializer.Deserialize<List<CookieImportEntry>>(json) ?? [];
            WebView2? temporary = null;
            try
            {
                var core = ActiveCore;
                if (core is null)
                {
                    // Import may be opened from the native home. Do not create a dummy tab or navigate away.
                    _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataPath, "WebView2"),
                        EnvironmentOptions());
                    temporary = new WebView2 { Visible = false, Size = new Size(1, 1) };
                    _viewport.Controls.Add(temporary);
                    await temporary.EnsureCoreWebView2Async(_environment);
                    core = temporary.CoreWebView2;
                }
                int applied = 0, skipped = 0, partitionFailures = 0;
                // Older protocol versions can ignore unknown fields. Require the version that
                // supports the complete partition key rather than risk creating an ordinary cookie.
                bool supportsPartitionKey = int.TryParse(core.Environment.BrowserVersionString.Split('.')[0], out int chromiumMajor) && chromiumMajor >= 136;
                foreach (var source in cookies)
                {
                    if (source.ExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow) { skipped++; continue; }
                    try
                    {
                        if (source.PartitionKey is { } partition)
                        {
                            // The WinForms CookieManager has no partition key. CDP retains the top-level
                            // site and ancestor bit; rejected partitions never fall back to ordinary cookies.
                            if (!source.IsSecure || !supportsPartitionKey) { skipped++; partitionFailures++; continue; }
                            var parameters = new Dictionary<string, object?>
                            {
                                ["name"] = source.Name, ["value"] = source.Value, ["path"] = source.Path,
                                ["secure"] = true, ["httpOnly"] = source.IsHttpOnly, ["sameSite"] = source.SameSite,
                                ["url"] = "https://" + source.Domain.TrimStart('.') + "/",
                                ["partitionKey"] = new { topLevelSite = partition.TopLevelSite, hasCrossSiteAncestor = partition.HasCrossSiteAncestor }
                            };
                            // Supplying domain changes a host-only cookie into a domain cookie in CDP.
                            if (source.Domain.StartsWith('.')) parameters["domain"] = source.Domain;
                            if (source.ExpiresUtc is { } partitionExpiry) parameters["expires"] = partitionExpiry.ToUnixTimeMilliseconds() / 1000d;
                            using var response = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Network.setCookie", JsonSerializer.Serialize(parameters)));
                            if (response.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True) applied++;
                            else { skipped++; partitionFailures++; }
                            continue;
                        }
                        var cookie = core.CookieManager.CreateCookie(source.Name, source.Value, source.Domain, source.Path);
                        cookie.IsSecure = source.IsSecure;
                        cookie.IsHttpOnly = source.IsHttpOnly;
                        cookie.SameSite = source.SameSite switch
                        {
                            "None" => CoreWebView2CookieSameSiteKind.None,
                            "Strict" => CoreWebView2CookieSameSiteKind.Strict,
                            _ => CoreWebView2CookieSameSiteKind.Lax
                        };
                        if (source.ExpiresUtc is { } expires) cookie.Expires = expires.UtcDateTime;
                        core.CookieManager.AddOrUpdateCookie(cookie);
                        applied++;
                    }
                    catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException)
                    { skipped++; if (source.PartitionKey is not null) partitionFailures++; }
                    if ((applied + skipped) % 100 == 0) await Task.Yield();
                }
                report = $"Cookie импортированы: {applied}; отклонено движком: {skipped}.";
                if (partitionFailures > 0) report += $" Из них разделённые cookie: {partitionFailures} (движок не поддержал этот контекст).";
                _status.Text = report + " Обновите нужные сайты.";
            }
            finally
            {
                if (temporary is not null) { _viewport.Controls.Remove(temporary); temporary.Dispose(); }
            }
        });
        return report;
    }

    internal void SelectTheme(BrowserTheme theme)
    {
        ThemeSettings.Select(theme, DataPath);
        Palette.Apply(this);
        if (_menu is not null) Palette.Apply(_menu);
        _home.RefreshTheme();
        foreach (var runtime in _runtimes.Values)
        {
            runtime.View.DefaultBackgroundColor = Palette.Background;
            if (runtime.View.CoreWebView2 is { } core) SetPageColorScheme(core);
        }
    }

    private void SetUnloadTimeout(int minutes)
    {
        _preferences.UnloadAfterMinutes = minutes;
        try { _preferences.Save(DataPath); _status.Text = $"Фоновые вкладки будут выгружаться через {minutes} мин."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex); }
    }
}
