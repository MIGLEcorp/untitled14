using CefSharp;
using CefSharp.Enums;
using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private async Task<string> ApplyCefCookiesAsync(string json)
    {
        var entries = JsonSerializer.Deserialize<List<CookieImportEntry>>(json) ?? [];
        int applied = 0, skipped = 0, partitionFailed = 0;
        using var context = Cef.GetGlobalRequestContext();
        using var manager = await context.GetCookieManagerAsync();
        if (manager is null) throw new InvalidOperationException("CEF cookie storage is unavailable.");

        foreach (var item in entries.Take(10000))
        {
            if (item.ExpiresUtc is { } expiration && expiration <= DateTimeOffset.UtcNow)
            { skipped++; continue; }
            var host = item.Domain.TrimStart('.');
            if (string.IsNullOrWhiteSpace(host) || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or ':' or '@'))
            { skipped++; continue; }
            string url = (item.IsSecure ? "https://" : "http://") + host + "/";
            try
            {
                // Partitioned cookies cannot be silently imported as first-party cookies.
                if (item.PartitionKey is { } partition)
                {
                    if (!item.IsSecure || !_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ||
                        !CefIsAlive(runtime) || !runtime.View.IsBrowserInitialized)
                    { partitionFailed++; skipped++; continue; }
                    var parameters = new Dictionary<string, object>
                    {
                        ["name"] = item.Name, ["value"] = item.Value, ["path"] = item.Path,
                        ["secure"] = true, ["httpOnly"] = item.IsHttpOnly,
                        ["sameSite"] = item.SameSite, ["url"] = url,
                        ["partitionKey"] = new {
                            topLevelSite = partition.TopLevelSite,
                            hasCrossSiteAncestor = partition.HasCrossSiteAncestor
                        }
                    };
                    if (item.Domain.StartsWith('.')) parameters["domain"] = item.Domain;
                    if (item.ExpiresUtc is { } exp) parameters["expires"] = (double)exp.ToUnixTimeMilliseconds() / 1000;
                    using var devtools = runtime.View.GetDevToolsClient();
                    var response = await devtools.ExecuteDevToolsMethodAsync("Network.setCookie", parameters);
                    if (response.Success) applied++;
                    else { skipped++; partitionFailed++; }
                    continue;
                }
                var cookie = new Cookie
                {
                    Name = item.Name, Value = item.Value,
                    Domain = item.Domain, Path = item.Path,
                    Secure = item.IsSecure, HttpOnly = item.IsHttpOnly,
                    SameSite = item.SameSite switch
                    {
                        "None" => CookieSameSite.NoRestriction,
                        "Strict" => CookieSameSite.StrictMode,
                        _ => CookieSameSite.LaxMode
                    },
                    Expires = item.ExpiresUtc?.UtcDateTime
                };
                if (await manager.SetCookieAsync(url, cookie)) applied++;
                else skipped++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
            { skipped++; if (item.PartitionKey is not null) partitionFailed++; }
        }
        string result = $"CEF: cookie импортированы: {applied}; отклонено: {skipped}.";
        if (partitionFailed != 0)
            result += $" Разделённых cookie не импортировано: {partitionFailed}.";
        _status.Text = result;
        return result;
    }
}
