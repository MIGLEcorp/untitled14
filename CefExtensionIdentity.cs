using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MBrowser;

/// <summary>Derives the Chromium ID for an unpacked extension from its actual on-disk path.</summary>
internal static class CefExtensionIdentity
{
    internal static string FromFolder(string folder)
    {
        var manifest = Path.Combine(folder, "manifest.json");
        byte[] input;
        if (File.Exists(manifest) && new FileInfo(manifest).Length <= 1024 * 1024)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.TryGetProperty("key", out var keyNode) &&
                keyNode.ValueKind == JsonValueKind.String &&
                keyNode.GetString() is { Length: > 0 } key)
            {
                input = Convert.FromBase64String(key);
                if (input.Length is < 32 or > 100000) throw new InvalidDataException("Invalid extension public key length.");
                return FromBytes(input);
            }
        }

        string path = Path.GetFullPath(folder);
        // Chromium's Windows path normalization uppercases the drive letter
        // before hashing native UTF-16LE path bytes.
        if (path.Length > 1 && path[1] == ':')
            path = char.ToUpperInvariant(path[0]) + path[1..];
        input = Encoding.Unicode.GetBytes(path);
        return FromBytes(input);
    }

    private static string FromBytes(ReadOnlySpan<byte> input)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        Span<char> id = stackalloc char[32];
        for (int i = 0; i < 16; i++)
        {
            id[2 * i] = (char)('a' + (digest[i] >> 4));
            id[2 * i + 1] = (char)('a' + (digest[i] & 15));
        }
        return new string(id);
    }
}
