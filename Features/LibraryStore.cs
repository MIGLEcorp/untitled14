using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Feather.Features;

public sealed record Bookmark(string Title, string Url);

/// <summary>Small JSON bookmark store. Importers accept only navigable HTTP(S) links.</summary>
public sealed class BookmarkStore(string path)
{
    public IReadOnlyList<Bookmark> Load()
    {
        if (!File.Exists(path)) return [];
        return ImportJson(FeatureFiles.ReadText(path));
    }

    public int Add(IEnumerable<Bookmark> additions)
    {
        var items = Load().ToList();
        var known = items.Select(item => item.Url).ToHashSet(StringComparer.Ordinal);
        int count = 0;
        foreach (var item in additions)
        {
            if (!FeatureFiles.TryWebUrl(item.Url, out string url) || !known.Add(url)) continue;
            if (items.Count >= FeatureFiles.MaxItems) throw new InvalidDataException("В библиотеке допускается не более 100 000 закладок.");
            items.Add(new Bookmark(NormalizeTitle(item.Title, url), url));
            count++;
        }
        if (count > 0) Save(items);
        return count;
    }

    public void Remove(string url) => Save(Load().Where(item => item.Url != url));

    public int ImportFile(string source)
    {
        if (source.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            return Add(FirefoxImport.ReadBookmarks(source));
        string text = FeatureFiles.ReadText(source);
        return Add(text.TrimStart().StartsWith('<') ? ImportHtml(text) : ImportJson(text));
    }

    public void ExportHtml(string destination)
    {
        var html = new StringBuilder("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>Feather bookmarks</TITLE>\n<H1>Feather bookmarks</H1>\n<DL><p>\n");
        foreach (var item in Load())
            html.Append("<DT><A HREF=\"").Append(WebUtility.HtmlEncode(item.Url)).Append("\">").Append(WebUtility.HtmlEncode(item.Title)).Append("</A>\n");
        html.Append("</DL><p>\n");
        FeatureFiles.WriteAtomic(destination, Encoding.UTF8.GetBytes(html.ToString()));
    }

    internal static IReadOnlyList<Bookmark> ImportHtml(string text)
    {
        var items = new List<Bookmark>();
        var anchors = Regex.Matches(text, "<a\\b(?<attrs>[^>]*)>(?<title>.*?)</a\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(3));
        foreach (Match anchor in anchors)
        {
            var href = Regex.Match(anchor.Groups["attrs"].Value, "\\bhref\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            string raw = WebUtility.HtmlDecode(href.Groups["value"].Value);
            if (!FeatureFiles.TryWebUrl(raw, out string url)) continue;
            string title = WebUtility.HtmlDecode(Regex.Replace(anchor.Groups["title"].Value, "<[^>]*>", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
            items.Add(new Bookmark(NormalizeTitle(title, url), url));
            if (items.Count >= FeatureFiles.MaxItems) break;
        }
        return items.DistinctBy(item => item.Url).ToList();
    }

    internal static IReadOnlyList<Bookmark> ImportJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        var items = new List<Bookmark>();
        ReadNode(document.RootElement);
        return items.DistinctBy(item => item.Url).ToList();

        void ReadNode(JsonElement node)
        {
            if (items.Count >= FeatureFiles.MaxItems) return;
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in node.EnumerateArray()) ReadNode(child);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            string raw = FeatureFiles.JsonString(node, "url", "Url");
            if (FeatureFiles.TryWebUrl(raw, out string url))
                items.Add(new Bookmark(NormalizeTitle(FeatureFiles.JsonString(node, "name", "title", "Title"), url), url));
            foreach (var key in new[] { "roots", "children", "bookmark_bar", "other", "synced", "mobile" })
                if (node.TryGetProperty(key, out var child)) ReadNode(child);
        }
    }

    private void Save(IEnumerable<Bookmark> items)
    {
        byte[] bytes = FeatureFiles.WriteJson(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("Title", item.Title);
                writer.WriteString("Url", item.Url);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        if (bytes.Length > FeatureFiles.MaxBytes) throw new InvalidDataException("Библиотека закладок превышает ограничение 32 МБ.");
        FeatureFiles.WriteAtomic(path, bytes);
    }
    private static string NormalizeTitle(string? title, string url) => string.IsNullOrWhiteSpace(title) ? url : title.Trim()[..Math.Min(title.Trim().Length, 500)];
}

internal static class FeatureFiles
{
    internal const int MaxItems = 100000;
    internal const long MaxBytes = 32 * 1024 * 1024;

    // Reflection-based JsonSerializer metadata can retain collectible module types.
    // Explicit writers keep this optional assembly unloadable after a dialog closes.
    internal static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) write(writer);
        return stream.ToArray();
    }

    internal static bool TryWebUrl(string? value, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32768 || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.HostNameType == UriHostNameType.Unknown || uri.Host.Length == 0 || uri.UserInfo.Length > 0) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    // Browsers serialize document origins with ASCII IDN hosts and without default ports.
    internal static string Origin(Uri uri)
    {
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        return uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static string ReadText(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Файл превышает ограничение 32 МБ.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static string JsonString(JsonElement element, params string[] keys)
    {
        foreach (string key in keys)
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        return "";
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
