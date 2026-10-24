using System.Globalization;
using System.Net;
using System.Text.Json;

namespace MBrowser.Features;

public sealed record CookiePartitionKey(string TopLevelSite, bool HasCrossSiteAncestor = false);

public sealed record ImportedCookie(string Domain, string Name, string Value, string Path = "/", bool IsSecure = false,
    bool IsHttpOnly = false, DateTimeOffset? ExpiresUtc = null, string SameSite = "Lax", CookiePartitionKey? PartitionKey = null);

public enum CookieSkipReason { Invalid, Expired, IsolatedContext, UnsupportedPartition }

public sealed record CookieImportResult(IReadOnlyList<ImportedCookie> Cookies, int Skipped,
    IReadOnlyDictionary<CookieSkipReason, int>? SkipReasons = null)
{
    public string SkipSummary => SkipReasons is null ? $"Пропущено записей: {Skipped}." : string.Join("; ", SkipReasons.Select(pair =>
        (pair.Key switch
        {
            CookieSkipReason.Expired => "Истёк срок",
            CookieSkipReason.IsolatedContext => "Отдельные контейнеры / приватные контексты",
            CookieSkipReason.UnsupportedPartition => "Неподдерживаемый контекст разделения",
            _ => "Некорректные или несовместимые записи"
        }) + $": {pair.Value}"));
}

internal sealed class CookieImportLog
{
    private readonly Dictionary<CookieSkipReason, int> reasons = [];
    internal void Skip(CookieSkipReason reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
    internal CookieImportResult Result(List<ImportedCookie> cookies) => new(CookieImport.Deduplicate(cookies), reasons.Values.Sum(), reasons);
}

/// <summary>Reads explicit exports; never attempts to decrypt another browser's protected cookie database.</summary>
public static class CookieImport
{
    internal static string Serialize(IReadOnlyList<ImportedCookie> cookies) => System.Text.Encoding.UTF8.GetString(FeatureFiles.WriteJson(writer =>
    {
        writer.WriteStartArray();
        foreach (var cookie in cookies)
        {
            writer.WriteStartObject();
            writer.WriteString("Domain", cookie.Domain);
            writer.WriteString("Name", cookie.Name);
            writer.WriteString("Value", cookie.Value);
            writer.WriteString("Path", cookie.Path);
            writer.WriteBoolean("IsSecure", cookie.IsSecure);
            writer.WriteBoolean("IsHttpOnly", cookie.IsHttpOnly);
            if (cookie.ExpiresUtc is { } expiry) writer.WriteString("ExpiresUtc", expiry); else writer.WriteNull("ExpiresUtc");
            writer.WriteString("SameSite", cookie.SameSite);
            if (cookie.PartitionKey is { } partition)
            {
                writer.WriteStartObject("PartitionKey");
                writer.WriteString("TopLevelSite", partition.TopLevelSite);
                writer.WriteBoolean("HasCrossSiteAncestor", partition.HasCrossSiteAncestor);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }));

    public static CookieImportResult ReadFile(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Span<byte> header = stackalloc byte[16];
            if (stream.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8)) return FirefoxImport.ReadCookies(path);
        }
        if (path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) return FirefoxImport.ReadCookies(path);
        string text = FeatureFiles.ReadText(path);
        return text.TrimStart().StartsWith('[') || text.TrimStart().StartsWith('{') ? ParseJson(text) : ParseNetscape(text);
    }

    internal static CookieImportResult ParseNetscape(string text)
    {
        var cookies = new List<ImportedCookie>();
        var log = new CookieImportLog();
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            bool httpOnly = line.StartsWith("#HttpOnly_", StringComparison.Ordinal);
            if (httpOnly) line = line[10..];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Split('\t', 7);
            if (fields.Length != 7 || !bool.TryParse(fields[1], out bool subdomains) || !bool.TryParse(fields[3], out bool secure) ||
                !long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long expiry))
            { log.Skip(CookieSkipReason.Invalid); continue; }
            string domain = fields[0].TrimStart('.');
            if (subdomains) domain = "." + domain;
            var cookie = new ImportedCookie(domain, fields[5], fields[6], fields[2], secure, httpOnly, FromUnix(expiry), "Lax");
            if (expiry != 0 && cookie.ExpiresUtc is null) log.Skip(CookieSkipReason.Invalid);
            else AddValidated(cookies, cookie, log);
            if (cookies.Count >= FeatureFiles.MaxItems) throw new InvalidDataException("Слишком много cookie в одном файле.");
        }
        return log.Result(cookies);
    }

    internal static CookieImportResult ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cookies", out var child)) root = child;
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Ожидается JSON-массив cookie или объект с полем cookies.");
        var cookies = new List<ImportedCookie>();
        var log = new CookieImportLog();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { log.Skip(CookieSkipReason.Invalid); continue; }
            if (!(item.TryGetProperty("value", out var exportedValue) && exportedValue.ValueKind == JsonValueKind.String) &&
                !(item.TryGetProperty("Value", out exportedValue) && exportedValue.ValueKind == JsonValueKind.String))
            { log.Skip(CookieSkipReason.Invalid); continue; }
            string store = FeatureFiles.JsonString(item, "storeId", "cookieStoreId");
            if (FeatureFiles.JsonString(item, "firstPartyDomain").Length > 0 ||
                (store.Length > 0 && store != "firefox-default" && store != "0"))
            { log.Skip(CookieSkipReason.IsolatedContext); continue; }
            CookiePartitionKey? partitionKey = null;
            bool hasPartition = item.TryGetProperty("partitionKey", out var partition) || item.TryGetProperty("PartitionKey", out partition);
            if (hasPartition && partition.ValueKind != JsonValueKind.Null)
            {
                string site = partition.ValueKind == JsonValueKind.String ? partition.GetString()! :
                    partition.ValueKind == JsonValueKind.Object ? FeatureFiles.JsonString(partition, "topLevelSite", "TopLevelSite") : "";
                if (!TryPartitionSite(site, out string normalized)) { log.Skip(CookieSkipReason.UnsupportedPartition); continue; }
                partitionKey = new CookiePartitionKey(normalized, partition.ValueKind == JsonValueKind.Object && Bool(partition, "hasCrossSiteAncestor", "HasCrossSiteAncestor"));
            }
            else if (Bool(item, "partitioned")) { log.Skip(CookieSkipReason.UnsupportedPartition); continue; }
            string domain = FeatureFiles.JsonString(item, "domain", "host", "Domain");
            if (item.TryGetProperty("hostOnly", out var hostOnly) && hostOnly.ValueKind == JsonValueKind.True) domain = domain.TrimStart('.');
            DateTimeOffset? expiry = null;
            bool badExpiry = false;
            if (!Bool(item, "session"))
            {
                foreach (string name in new[] { "expirationDate", "expires", "ExpiresUtc" })
                {
                    if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double seconds))
                    {
                        if (seconds > 0 && seconds <= 253402300799) expiry = FromUnix((long)seconds);
                        else if (seconds != 0 && seconds != -1) badExpiry = true;
                    }
                    else if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) expiry = date;
                    else badExpiry = true;
                    break;
                }
            }
            string sameSite = FeatureFiles.JsonString(item, "sameSite", "SameSite").ToLowerInvariant() switch
            {
                "none" or "no_restriction" => "None",
                "strict" => "Strict",
                _ => "Lax"
            };
            string cookiePath = FeatureFiles.JsonString(item, "path", "Path");
            var cookie = new ImportedCookie(domain, FeatureFiles.JsonString(item, "name", "Name"), FeatureFiles.JsonString(item, "value", "Value"),
                cookiePath.Length == 0 ? "/" : cookiePath, Bool(item, "secure", "IsSecure"), Bool(item, "httpOnly", "IsHttpOnly"), expiry, sameSite, partitionKey);
            if (badExpiry) log.Skip(CookieSkipReason.Invalid);
            else AddValidated(cookies, cookie, log);
            if (cookies.Count >= FeatureFiles.MaxItems) throw new InvalidDataException("Слишком много cookie в одном файле.");
        }
        return log.Result(cookies);
    }

    internal static void AddValidated(List<ImportedCookie> cookies, ImportedCookie cookie, CookieImportLog log)
    {
        if (cookie.ExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow) log.Skip(CookieSkipReason.Expired);
        // Chromium requires Secure for CHIPS. Never weaken the cookie's original isolation.
        else if (cookie.PartitionKey is not null && !cookie.IsSecure) log.Skip(CookieSkipReason.UnsupportedPartition);
        else if (!IsValid(cookie)) log.Skip(CookieSkipReason.Invalid);
        else cookies.Add(cookie);
    }

    internal static bool TryPartitionSite(string text, out string site)
    {
        site = "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.IsDefaultPort)
            return false;
        site = FeatureFiles.Origin(uri);
        return true;
    }

    internal static bool IsValid(ImportedCookie cookie)
    {
        if (cookie.Domain.Length is 0 or > 254 || cookie.Domain.Any(char.IsControl) || cookie.Domain.Contains('/') || cookie.Domain.Contains('\\') || cookie.Domain.Contains('@') || cookie.Domain.Contains(':')) return false;
        string host = cookie.Domain.TrimStart('.');
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Length == 0 || cookie.Domain.StartsWith("..", StringComparison.Ordinal)) return false;
        if (cookie.Name.Length is 0 or > 1024 || cookie.Name.Any(c => c <= 32 || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c))) return false;
        if (cookie.Value.Length > 16384 || cookie.Value.Any(c => c < 32 || c == 127 || c == ';')) return false;
        if (!cookie.Path.StartsWith('/') || cookie.Path.Length > 4096 || cookie.Path.Any(char.IsControl)) return false;
        if (cookie.ExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow) return false;
        if (cookie.SameSite == "None" && !cookie.IsSecure) return false;
        if (cookie.Name.StartsWith("__Secure-", StringComparison.Ordinal) && !cookie.IsSecure) return false;
        if (cookie.Name.StartsWith("__Host-", StringComparison.Ordinal) && (!cookie.IsSecure || cookie.Path != "/" || cookie.Domain.StartsWith('.'))) return false;
        if (cookie.PartitionKey is { } partition && (!cookie.IsSecure || !TryPartitionSite(partition.TopLevelSite, out _))) return false;
        return true;
    }

    internal static DateTimeOffset? FromUnix(long seconds)
    {
        if (seconds == 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool Bool(JsonElement item, params string[] names) => names.Any(name => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True);
    internal static IReadOnlyList<ImportedCookie> Deduplicate(List<ImportedCookie> cookies) => cookies.DistinctBy(cookie => (cookie.Domain, cookie.Name, cookie.Path, cookie.PartitionKey)).ToList();
}
