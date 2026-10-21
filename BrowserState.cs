using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Feather;

public sealed class BrowserTab
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "تبويب جديد";
    public List<string> History { get; set; } = [];
    public int HistoryIndex { get; set; } = -1;

    [JsonIgnore]
    public string? CurrentUrl => HistoryIndex >= 0 && HistoryIndex < History.Count ? History[HistoryIndex] : null;

    [JsonIgnore]
    public bool CanGoBack => HistoryIndex > 0 && HistoryIndex < History.Count;

    [JsonIgnore]
    public bool CanGoForward => HistoryIndex >= 0 && HistoryIndex < History.Count - 1;

    public void Visit(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || string.Equals(CurrentUrl, url, StringComparison.Ordinal))
            return;

        HistoryIndex = Math.Clamp(HistoryIndex, -1, History.Count - 1);
        if (HistoryIndex + 1 < History.Count)
            History.RemoveRange(HistoryIndex + 1, History.Count - HistoryIndex - 1);

        History.Add(url);
        if (History.Count > 80)
            History.RemoveRange(0, History.Count - 80);
        HistoryIndex = History.Count - 1;
    }

    public void ReplaceCurrent(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (CurrentUrl is null)
            Visit(url);
        else
            History[HistoryIndex] = url;
    }

    public string? Back()
    {
        if (CanGoBack)
            HistoryIndex--;
        return CurrentUrl;
    }

    public string? Forward()
    {
        if (CanGoForward)
            HistoryIndex++;
        return CurrentUrl;
    }
}

public sealed class AppSession
{
    public List<BrowserTab> Tabs { get; set; } = [new BrowserTab()];
    public int SelectedIndex { get; set; }
}

public static class AddressParser
{
    private static readonly Regex ExplicitScheme = new("^[a-zA-Z][a-zA-Z0-9+.-]*:", RegexOptions.CultureInvariant);

    public static bool TryResolve(string input, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        input = input.Trim();
        if (input.Any(char.IsControl))
            return false;

        if (string.Equals(input, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            url = "about:blank";
            return true;
        }

        if (input.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            return TryWebUrl(input, out url);

        // A host with a port also looks like a URI scheme, so resolve it first.
        if (!input.Any(char.IsWhiteSpace) && TryHostAddress(input, out url))
            return true;

        if (ExplicitScheme.IsMatch(input))
            return false;

        url = "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
        return true;
    }

    internal static bool TryWebUrl(string input, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Any(char.IsControl) || input.Contains('\\'))
            return false;
        if (!input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !IsValidHost(uri))
            return false;
        url = uri.AbsoluteUri;
        return true;
    }

    private static bool TryHostAddress(string input, out string url)
    {
        url = string.Empty;
        if (input.Contains('\\') || input.Contains('@') || input.Contains("://", StringComparison.Ordinal))
            return false;

        // Brackets are optional for a bare IPv6 address, but required with a port.
        if (IPAddress.TryParse(input, out var bareIp) && input.Contains(':') && !input.StartsWith('['))
            input = "[" + bareIp + "]";

        if (!Uri.TryCreate("https://" + input, UriKind.Absolute, out var candidate) || !IsValidHost(candidate))
            return false;

        var host = candidate.IdnHost.Trim('[', ']');
        var isIp = IPAddress.TryParse(host, out var address);
        var isLocalhost = string.Equals(host.TrimEnd('.'), "localhost", StringComparison.OrdinalIgnoreCase);
        if (!isIp && !isLocalhost && !host.TrimEnd('.').Contains('.'))
            return false;

        var scheme = isLocalhost || (isIp && IPAddress.IsLoopback(address!)) ? "http://" : "https://";
        return TryWebUrl(scheme + input, out url);
    }

    private static bool IsValidHost(Uri uri)
    {
        try
        {
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                return true;
            if (uri.HostNameType != UriHostNameType.Dns)
                return false;
            var host = new IdnMapping().GetAscii(uri.IdnHost).TrimEnd('.');
            if (host.Length is 0 or > 253)
                return false;
            return host.Split('.').All(label => label.Length is > 0 and <= 63 &&
                char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]) &&
                label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public static class SessionStore
{
    private const long MaxSessionBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSession Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxSessionBytes)
                return new AppSession();
            using var stream = File.OpenRead(path);
            return Normalize(JsonSerializer.Deserialize<AppSession>(stream, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new AppSession();
        }
    }

    public static void Save(string path, AppSession session)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, Normalize(session), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // The temporary file is on the same volume: replacement leaves a complete JSON file.
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AppSession Normalize(AppSession? session)
    {
        var clean = new AppSession { Tabs = [] };
        var ids = new HashSet<Guid>();
        foreach (var tab in (session?.Tabs ?? []).Take(100))
        {
            if (tab is null)
                continue;
            var copy = new BrowserTab
            {
                Id = tab.Id != Guid.Empty && ids.Add(tab.Id) ? tab.Id : Guid.NewGuid(),
                Title = string.IsNullOrWhiteSpace(tab.Title) ? "تبويب جديد" : tab.Title[..Math.Min(tab.Title.Length, 300)],
                History = [],
                HistoryIndex = -1
            };
            ids.Add(copy.Id);
            var source = tab.History ?? [];
            var sourceIndex = Math.Clamp(tab.HistoryIndex, -1, source.Count - 1);
            for (var i = 0; i < source.Count; i++)
            {
                var entry = source[i];
                if (entry is null || entry.Length > 32768)
                    continue;
                string validUrl;
                if (entry == "about:blank")
                    validUrl = entry;
                else if (!AddressParser.TryWebUrl(entry, out validUrl))
                    continue;
                copy.History.Add(validUrl);
                if (i <= sourceIndex)
                    copy.HistoryIndex = copy.History.Count - 1;
            }
            if (copy.History.Count > 80)
            {
                // Keep a window containing the selected entry, including useful forward history.
                var start = Math.Clamp(copy.HistoryIndex - 79, 0, copy.History.Count - 80);
                copy.History = copy.History.Skip(start).Take(80).ToList();
                copy.HistoryIndex -= start;
            }
            if (copy.History.Count > 0)
                copy.HistoryIndex = Math.Clamp(copy.HistoryIndex, 0, copy.History.Count - 1);
            clean.Tabs.Add(copy);
        }
        if (clean.Tabs.Count == 0)
            clean.Tabs.Add(new BrowserTab());
        clean.SelectedIndex = Math.Clamp(session?.SelectedIndex ?? 0, 0, clean.Tabs.Count - 1);
        return clean;
    }
}
