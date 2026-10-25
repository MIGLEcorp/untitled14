using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MBrowser;

/// <summary>Downloads the unmodified official package on request, verifies it, then stages a per-profile install.</summary>
internal static class TranslatorPackage
{
    internal const string Version = "1.34.4";
    internal const string ExpectedId = "bpoadfkcbjbfhfodiogcnhhhpibjhbnh";
    internal const string Sha256 = "939AB79A01C136C12426EBA29FCFD93149BC0BBFAD79280554A7E5BD4E25C604";
    internal const string DownloadUrl = "https://github.com/immersive-translate/immersive-translate/releases/download/v1.34.4/chrome-immersive-translate-1.34.4.zip";
    internal sealed record State(bool Pending, string Id);
    internal static string Folder(string profile) => Path.Combine(profile, "Extensions", "ImmersiveTranslate-" + Version);
    internal static State? Read(string profile)
    {
        try { string path = Path.Combine(profile, "translator.json"); return File.Exists(path) && new FileInfo(path).Length < 4096 ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal static void Write(string profile, State state)
    {
        string path = Path.Combine(profile, "translator.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state)); File.Move(path + ".tmp", path, true);
    }
    internal static async Task PrepareAsync(string profile, string? archiveFile = null)
    {
        Directory.CreateDirectory(profile);
        if(archiveFile is null && Directory.Exists(Folder(profile)))
        { Write(profile,new(true,ExpectedId));return; }
        string archive = archiveFile ?? Path.Combine(profile, "Downloads", "ImmersiveTranslate-" + Version + ".zip");
        if (!File.Exists(archive))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new InvalidDataException("Пакет переводчика слишком большой.");
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var output = File.Create(archive + ".download");
            byte[] buffer = new byte[65536]; int count; long total = 0;
            while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            { total += count; if (total > 20 * 1024 * 1024) throw new InvalidDataException("Пакет переводчика слишком большой."); await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false); }
            await output.DisposeAsync().ConfigureAwait(false); File.Move(archive + ".download", archive, true);
        }
        try { VerifyArchive(archive); }
        catch (InvalidDataException) when (archiveFile is null)
        {
            File.Delete(archive); // A later attempt must download a fresh copy rather than reuse a damaged cache.
            throw;
        }
        string destination = Folder(profile), root = Path.GetFullPath(Path.Combine(profile, "Extensions")) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(destination))
        {
            string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                using var zip = ZipFile.OpenRead(archive);
                if (zip.Entries.Count > 1000 || zip.Entries.Any(entry => entry.Length > 40 * 1024 * 1024) || zip.Entries.Sum(entry => entry.Length) > 80 * 1024 * 1024) throw new InvalidDataException("Некорректный размер расширения.");
                string prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    string path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Недопустимый путь в расширении.");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path);
                }
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "manifest.json")));
                if (manifest.RootElement.GetProperty("version").GetString() != Version || manifest.RootElement.GetProperty("manifest_version").GetInt32() != 3) throw new InvalidDataException("Версия переводчика не совпадает.");
                Directory.Move(staging, destination);
            }
            finally { if (Path.GetFullPath(staging).StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        Write(profile, new(true, ExpectedId));
    }
    internal static void VerifyArchive(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 20 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(file)) != Sha256) throw new InvalidDataException("Контрольная сумма официального переводчика не совпадает.");
    }
}
