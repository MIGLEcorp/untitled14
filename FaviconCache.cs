using System.Security.Cryptography;
using System.Text;

namespace Feather;

internal sealed class FaviconCache(string dataPath) : IDisposable
{
    private readonly Dictionary<string, Image> _images = [];
    private readonly HashSet<string> _attempted = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 262144 };
    private readonly SemaphoreSlim _downloads = new(4);
    private bool _disposed;
    private static string? Origin(string? url) => AddressParser.TryWebUrl(url ?? "", out var valid) ? new Uri(valid).GetLeftPart(UriPartial.Authority) : null;
    private string PathFor(string origin) => Path.Combine(dataPath, "Favicons", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin))) + ".png");

    internal Image? GetCached(string? url)
    {
        if (_disposed || Origin(url) is not { } origin) return null;
        if (_images.TryGetValue(origin, out var image)) return image;
        string path = PathFor(origin);
        if (!File.Exists(path)) return null;
        try { using var stream = File.OpenRead(path); return Save(origin, stream, persist: false); }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { return null; }
    }

    internal Image? Save(string url, Stream stream, bool persist = true)
    {
        if (_disposed || Origin(url) is not { } origin) return null;
        using var source = Image.FromStream(stream);
        if (source.Width > 1024 || source.Height > 1024) return null;
        var icon = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(icon)) graphics.DrawImage(source, 0, 0, 32, 32);
        // Keep replaced images alive until controls stop referencing them; normally there is one per origin.
        if (_images.ContainsKey(origin)) { icon.Dispose(); return _images[origin]; }
        _images[origin] = icon;
        if (persist)
        {
            string path = PathFor(origin);
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); icon.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
        }
        return icon;
    }

    internal async Task<Image?> GetAsync(string url)
    {
        if (GetCached(url) is { } cached) return cached;
        if (_disposed || Origin(url) is not { } origin || !_attempted.Add(origin)) return null;
        await _downloads.WaitAsync();
        try
        {
            using var response = await _http.GetAsync(origin + "/favicon.ico", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 262144) return null;
            await using var input = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(chunk)) > 0)
            { if (buffer.Length + read > 262144) return null; buffer.Write(chunk, 0, read); }
            buffer.Position = 0;
            return Save(origin, buffer);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or ArgumentException or OutOfMemoryException or ObjectDisposedException) { return null; }
        finally { _downloads.Release(); }
    }
    public void Dispose() { _disposed = true; _http.Dispose(); foreach (var image in _images.Values) image.Dispose(); _images.Clear(); }
}
