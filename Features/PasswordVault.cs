using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Feather.Features;

public sealed record PasswordEntry(Guid Id, string Title, string Url, string Username, string Password)
{
    public string Origin => FeatureFiles.Origin(new Uri(Url));
}

/// <summary>Entire vault is protected by Windows DPAPI for the current Windows account.</summary>
public sealed class PasswordVault(string path)
{
    public IReadOnlyList<PasswordEntry> Load()
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > FeatureFiles.MaxBytes) throw new InvalidDataException("Хранилище паролей слишком большое.");
        byte[] plain = WindowsDataProtection.Transform(File.ReadAllBytes(path), protect: false);
        try
        {
            using var document = JsonDocument.Parse(plain);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Хранилище паролей повреждено.");
            var entries = new List<PasswordEntry>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !Guid.TryParse(FeatureFiles.JsonString(item, "Id"), out var id))
                    throw new InvalidDataException("Хранилище паролей повреждено.");
                entries.Add(new PasswordEntry(id, FeatureFiles.JsonString(item, "Title"), FeatureFiles.JsonString(item, "Url"), FeatureFiles.JsonString(item, "Username"), FeatureFiles.JsonString(item, "Password")));
            }
            if (entries.Any(entry => !IsValid(entry))) throw new InvalidDataException("Хранилище паролей повреждено.");
            return entries;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(IEnumerable<PasswordEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count > 10000 || list.Any(entry => !IsValid(entry))) throw new InvalidDataException("Некорректная запись пароля.");
        byte[] plain = FeatureFiles.WriteJson(writer =>
        {
            writer.WriteStartArray();
            foreach (var entry in list)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", entry.Id);
                writer.WriteString("Title", entry.Title);
                writer.WriteString("Url", entry.Url);
                writer.WriteString("Username", entry.Username);
                writer.WriteString("Password", entry.Password);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        try
        {
            if (plain.Length > FeatureFiles.MaxBytes - 1024) throw new InvalidDataException("Хранилище паролей превышает ограничение 32 МБ.");
            FeatureFiles.WriteAtomic(path, WindowsDataProtection.Transform(plain, protect: true));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public int ImportCsv(string source)
    {
        var existing = Load().ToList();
        var known = existing.Select(entry => (entry.Origin, entry.Username)).ToHashSet();
        int count = 0;
        foreach (var entry in ParseCsv(FeatureFiles.ReadText(source)))
        {
            if (!known.Add((entry.Origin, entry.Username))) continue;
            existing.Add(entry);
            count++;
        }
        if (count > 0) Save(existing);
        return count;
    }

    internal static IReadOnlyList<PasswordEntry> ParseCsv(string text)
    {
        var rows = CsvRows(text).GetEnumerator();
        if (!rows.MoveNext()) throw new InvalidDataException("CSV-файл пуст.");
        var headers = rows.Current.Select(header => header.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToList();
        int urlColumn = headers.FindIndex(header => header is "url" or "origin" or "hostname");
        int userColumn = headers.FindIndex(header => header is "username" or "user" or "login");
        int passwordColumn = headers.FindIndex(header => header == "password");
        int titleColumn = headers.FindIndex(header => header is "name" or "title");
        if (urlColumn < 0 || userColumn < 0 || passwordColumn < 0)
            throw new InvalidDataException("Нужны столбцы url, username и password (экспорт Chrome, Edge или Firefox).");
        var entries = new List<PasswordEntry>();
        while (rows.MoveNext())
        {
            var row = rows.Current;
            if (row.Count <= Math.Max(urlColumn, Math.Max(userColumn, passwordColumn))) continue;
            if (!FeatureFiles.TryWebUrl(row[urlColumn], out string url) || row[passwordColumn].Length == 0) continue;
            string title = titleColumn >= 0 && titleColumn < row.Count ? row[titleColumn] : new Uri(url).Host;
            var entry = new PasswordEntry(Guid.NewGuid(), title, url, row[userColumn], row[passwordColumn]);
            if (IsValid(entry)) entries.Add(entry);
            if (entries.Count > 10000) throw new InvalidDataException("Не более 10 000 паролей за один импорт.");
        }
        return entries;
    }

    internal static string BuildFillScript(string expectedOrigin, PasswordEntry entry, bool overwrite = true)
    {
        // Every value is JSON encoded. No untrusted credential text becomes JavaScript source.
        string payload = Encoding.UTF8.GetString(FeatureFiles.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("origin", expectedOrigin);
            writer.WriteString("username", entry.Username);
            writer.WriteString("password", entry.Password);
            writer.WriteEndObject();
        }));
        return "(() => { const entry = " + payload + "; if (window !== window.top || location.origin !== entry.origin || !window.isSecureContext) return 'wrong-origin'; " +
            AutofillPageScripts.LoginFields +
            "const login = findLogin(); if (!login) return 'no-password-field'; const { user, password } = login; " +
            (overwrite ? "" : "if (password.value || user && user.value && user.value !== entry.username) return 'user-entry'; ") +
            "const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set; " +
            "const fill = (element,value) => { setter.call(element,value); element.dispatchEvent(new Event('input',{bubbles:true})); element.dispatchEvent(new Event('change',{bubbles:true})); }; " +
            "if (user) fill(user,entry.username); fill(password,entry.password); " + (overwrite ? "password.focus(); " : "") +
            "entry.username = entry.password = ''; return 'filled'; })()";
    }

    private static bool IsValid(PasswordEntry? entry) => entry is not null && entry.Id != Guid.Empty && entry.Title is not null && entry.Username is not null &&
        entry.Password is not null && entry.Password.Length is > 0 and <= 32768 && entry.Username.Length <= 4096 && FeatureFiles.TryWebUrl(entry.Url, out _);

    private static IEnumerable<List<string>> CsvRows(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (quoted)
            {
                if (c == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(value => value.Length > 0)) yield return row;
                row = [];
            }
            else field.Append(c);
        }
        if (quoted) throw new InvalidDataException("Незакрытая кавычка в CSV.");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); yield return row; }
    }
}

internal static class WindowsDataProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { internal int Length; internal nint Data; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Protect(ref Blob input, string description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool Unprotect(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    internal static byte[] Transform(byte[] data, bool protect)
    {
        nint library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "crypt32.dll"));
        nint inputMemory = Marshal.AllocHGlobal(data.Length);
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, inputMemory, data.Length);
            var input = new Blob { Length = data.Length, Data = inputMemory };
            bool success = protect
                ? Marshal.GetDelegateForFunctionPointer<Protect>(NativeLibrary.GetExport(library, "CryptProtectData"))(ref input, "Feather password vault", 0, 0, 0, 1, out output)
                : Marshal.GetDelegateForFunctionPointer<Unprotect>(NativeLibrary.GetExport(library, "CryptUnprotectData"))(ref input, 0, 0, 0, 0, 1, out output);
            if (!success) throw new CryptographicException("Windows не смог открыть хранилище паролей. Оно привязано к вашей учётной записи Windows.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < data.Length; i++) Marshal.WriteByte(inputMemory, i, 0);
            Marshal.FreeHGlobal(inputMemory);
            if (output.Data != 0)
            {
                if (!protect) for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            NativeLibrary.Free(library);
        }
    }
}
