using System.Security.Cryptography;
using System.Text.Json;

namespace MBrowser;

/// <summary>
/// One-time, offline migration. Never remove the original until the new profile
/// is a verified byte-for-byte copy. The Chromium WebView2 store is retained as
/// an archive, not passed off as active CEF cookies.
/// </summary>
internal static class ProfileMigration
{
    internal const string CompleteMarker = "profile-migrated-from-feather.json";
    internal const string LegacyWebViewArchive = "LegacyWebView2";

    internal sealed record Result(string Profile, bool Migrated, bool DeletedLegacy, int Files, long Bytes);

    internal static Result MoveVerified(string oldProfile, string newProfile,
        Action<int, long>? progress = null)
    {
        string oldRoot = Path.GetFullPath(oldProfile).TrimEnd(Path.DirectorySeparatorChar);
        string newRoot = Path.GetFullPath(newProfile).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(oldRoot) != Path.GetDirectoryName(newRoot) ||
            newRoot.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Недопустимые пути миграции.");

        if (!Directory.Exists(oldRoot))
            return new(newRoot, false, false, 0, 0);

        if (Directory.Exists(newRoot))
        {
            if (File.Exists(Path.Combine(newRoot, CompleteMarker)))
            {
                // Previous profile transfer completed; never overwrite newer MBrowser data.
                // A leftover old profile must be handled by a separate explicit cleanup.
                return new(newRoot, false, false, 0, 0);
            }
            throw new IOException("Новый профиль уже существует без отметки переноса. Старый профиль не изменён.");
        }

        string staging = newRoot + ".migration-staging";
        if (Directory.Exists(staging))
            throw new IOException("Обнаружен незавершённый перенос. Старые данные не изменены.");

        var copied = new List<(string Old, string New, long Size)>();
        int count = 0;
        long bytes = 0;
        bool committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            CopyFolder(oldRoot, staging, "");
            foreach (var entry in copied)
            {
                if (!File.Exists(entry.New) ||
                    new FileInfo(entry.New).Length != entry.Size ||
                    !HashesMatch(entry.Old, entry.New))
                    throw new IOException("Не совпадает проверка перенесённого файла: " +
                        Path.GetFileName(entry.Old));
            }

            var manifest = new
            {
                sourceFolder = Path.GetFileName(oldRoot),
                migratedUtc = DateTimeOffset.UtcNow,
                filesVerified = count,
                bytesVerified = bytes,
                legacyChromiumArchive = LegacyWebViewArchive,
                note = "WebView2 cookies and browser-native passwords are archived, not active in CEF."
            };
            File.WriteAllText(Path.Combine(staging, CompleteMarker),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            // Check that no files appeared during the snapshot. This matters
            // if a child WebView2 process lingers after the old UI exits.
            int sourceFileCount = Directory.EnumerateFiles(oldRoot, "*",
                SearchOption.AllDirectories).Count();
            if (sourceFileCount != count)
                throw new IOException("Содержимое старого профиля изменилось во время переноса.");

            // Rename the fully checked staging folder into its final location.
            Directory.Move(staging, newRoot);
            committed = true;

            // Register valid unpacked MV3 packages against their final CEF paths.
            // Their original files remain in Extensions/ even if activation fails.
            string legacyPackages = Path.Combine(newRoot, "Extensions");
            if (Directory.Exists(legacyPackages))
                foreach (string folder in Directory.EnumerateDirectories(legacyPackages))
                    try
                    {
                        _ = ExtensionPackage.Read(folder);
                        CefExtensionRegistry.Add(newRoot, folder);
                    }
                    catch (Exception error) when (error is IOException or JsonException or
                        InvalidDataException or InvalidOperationException or UnauthorizedAccessException or
                        ArgumentException or FormatException or KeyNotFoundException)
                    { /* Original extension data is still preserved. */ }

            // Deletion is deliberately last. If deletion fails, the original
            // stays available and subsequent launches continue using newRoot.
            bool deleted = false;
            try
            {
                Directory.Delete(oldRoot, recursive: true);
                deleted = !Directory.Exists(oldRoot);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return new(newRoot, true, deleted, count, bytes);
        }
        finally
        {
            if (!committed && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        void CopyFolder(string from, string to, string relative)
        {
            if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Нельзя переносить ссылочную папку в профиле.");
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Нельзя переносить ссылочный файл профиля.");
                string target = Path.Combine(to, Path.GetFileName(file));
                File.Copy(file, target, overwrite: false);
                long length = new FileInfo(file).Length;
                copied.Add((file, target, length));
                count++;
                bytes += length;
                progress?.Invoke(count, bytes);
            }

            foreach (string folder in Directory.EnumerateDirectories(from))
            {
                string name = Path.GetFileName(folder);
                // The WebView2 user-data store cannot serve as the CEF runtime's
                // live cookie database. Preserve the original bytes separately.
                string mappedName = string.IsNullOrEmpty(relative) &&
                    name.Equals("WebView2", StringComparison.OrdinalIgnoreCase)
                    ? LegacyWebViewArchive : name;
                CopyFolder(folder, Path.Combine(to, mappedName),
                    Path.Combine(relative, mappedName));
            }
        }
    }

    private static bool HashesMatch(string left, string right)
    {
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
}