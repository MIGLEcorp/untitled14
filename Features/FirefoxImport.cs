using System.Runtime.InteropServices;
using System.Text;

namespace MBrowser.Features;

internal static class FirefoxImport
{
    internal static IReadOnlyList<Bookmark> ReadBookmarks(string path)
    {
        using var database = new ReadOnlySqlite(path);
        return database.Query("SELECT COALESCE(b.title,p.title,''), p.url FROM moz_bookmarks b JOIN moz_places p ON p.id=b.fk WHERE b.type=1 LIMIT 100000", 2)
            .Where(row => FeatureFiles.TryWebUrl(row[1], out _))
            .Select(row => new Bookmark(string.IsNullOrWhiteSpace(row[0]) ? row[1] : row[0], row[1])).ToList();
    }

    internal static CookieImportResult ReadCookies(string path)
    {
        using var database = new ReadOnlySqlite(path);
        var columns = database.Query("PRAGMA table_info(moz_cookies)", 2).Select(row => row[1]).ToHashSet(StringComparer.Ordinal);
        if (!columns.Contains("host") || !columns.Contains("name") || !columns.Contains("value"))
            throw new InvalidDataException("Это не база cookie Firefox. Для Chromium-браузеров выберите экспорт cookie в JSON или Netscape TXT.");
        string sameSite = columns.Contains("sameSite") ? "sameSite" : "0";
        string attributes = columns.Contains("originAttributes") ? "originAttributes" : "''";
        string browserElement = columns.Contains("inBrowserElement") ? "inBrowserElement" : "0";
        string session = columns.Contains("isSession") ? "isSession" : "0";
        int.TryParse(database.Query("PRAGMA user_version", 1).Single()[0], out int schemaVersion);
        // Firefox 142 migrated schema 16 from Unix seconds to milliseconds.
        // https://github.com/mozilla-firefox/firefox/blob/main/netwerk/cookie/CookiePersistentStorage.cpp
        bool milliseconds = schemaVersion >= 16;
        var rows = database.Query($"SELECT host,name,value,path,isSecure,isHttpOnly,expiry,{sameSite},{attributes},{browserElement},{session} FROM moz_cookies LIMIT 100000", 11);
        var cookies = new List<ImportedCookie>();
        var log = new CookieImportLog();
        foreach (var row in rows)
        {
            if (row[9] != "0") { log.Skip(CookieSkipReason.IsolatedContext); continue; }
            if (!TryOriginAttributes(row[8], out var partition, out var reason)) { log.Skip(reason); continue; }
            if (!long.TryParse(row[6], out long expiry)) { log.Skip(CookieSkipReason.Invalid); continue; }
            DateTimeOffset? expires = null;
            if (expiry != 0 && row[10] != "1")
            {
                try { expires = milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(expiry) : DateTimeOffset.FromUnixTimeSeconds(expiry); }
                catch (ArgumentOutOfRangeException) { log.Skip(CookieSkipReason.Invalid); continue; }
            }
            var cookie = new ImportedCookie(row[0], row[1], row[2], row[3], row[4] == "1", row[5] == "1", expires,
                row[7] switch { "2" => "Strict", "1" => "Lax", _ => row[4] == "1" ? "None" : "Lax" }, partition);
            CookieImport.AddValidated(cookies, cookie, log);
        }
        return log.Result(cookies);
    }

    private static bool TryOriginAttributes(string attributes, out CookiePartitionKey? partition, out CookieSkipReason reason)
    {
        partition = null;
        reason = CookieSkipReason.IsolatedContext;
        if (attributes.Length == 0) return true;
        if (!attributes.StartsWith('^')) return false;
        foreach (string attribute in attributes[1..].Split('&'))
        {
            var pair = attribute.Split('=', 2);
            if (pair.Length != 2) return false;
            string key = Uri.UnescapeDataString(pair[0]);
            string value = Uri.UnescapeDataString(pair[1]);
            // Containers, private browsing and first-party isolation are distinct stores.
            if (key != "partitionKey")
            {
                if ((key is "userContextId" or "privateBrowsingId" && value == "0") || (key == "firstPartyDomain" && value.Length == 0)) continue;
                return false;
            }
            reason = CookieSkipReason.UnsupportedPartition;
            if (partition is not null || value.Length < 5 || value[0] != '(' || value[^1] != ')') return false;
            string[] fields = value[1..^1].Split(',');
            if (fields.Length is < 2 or > 3 || (fields.Length == 3 && fields[2] != "f") ||
                !CookieImport.TryPartitionSite(fields[0] + "://" + fields[1], out string site)) return false;
            partition = new CookiePartitionKey(site, fields.Length == 3);
            reason = CookieSkipReason.IsolatedContext;
        }
        return true;
    }
}

/// <summary>Loads the system SQLite library only for an explicit import and releases it afterwards.</summary>
internal sealed class ReadOnlySqlite : IDisposable
{
    private nint library;
    private nint database;
    private readonly Close close = null!;
    private readonly Prepare prepare = null!;
    private readonly Step step = null!;
    private readonly FinalizeStatement finalize = null!;
    private readonly ColumnText columnText = null!;
    private readonly ErrorMessage errorMessage = null!;

    internal ReadOnlySqlite(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл профиля не найден.", path);
        library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"));
        try
        {
            close = Function<Close>("sqlite3_close");
            prepare = Function<Prepare>("sqlite3_prepare_v2");
            step = Function<Step>("sqlite3_step");
            finalize = Function<FinalizeStatement>("sqlite3_finalize");
            columnText = Function<ColumnText>("sqlite3_column_text");
            errorMessage = Function<ErrorMessage>("sqlite3_errmsg");
            int result = Function<Open>("sqlite3_open_v2")(Utf8(Path.GetFullPath(path)), out database, 1, 0);
            if (result != 0) throw Error();
            Function<BusyTimeout>("sqlite3_busy_timeout")(database, 1000);
        }
        catch { Dispose(); throw; }
    }

    internal IReadOnlyList<string[]> Query(string sql, int columns)
    {
        nint statement = 0;
        try
        {
            if (prepare(database, Utf8(sql), -1, out statement, 0) != 0) throw Error();
            var rows = new List<string[]>();
            int result;
            while ((result = step(statement)) == 100)
            {
                var row = new string[columns];
                for (int column = 0; column < columns; column++) row[column] = Marshal.PtrToStringUTF8(columnText(statement, column)) ?? "";
                rows.Add(row);
                if (rows.Count > FeatureFiles.MaxItems) throw new InvalidDataException("Слишком много записей в профиле.");
            }
            if (result != 101) throw Error();
            return rows;
        }
        finally { if (statement != 0) finalize(statement); }
    }

    public void Dispose()
    {
        if (database != 0) { close(database); database = 0; }
        if (library != 0) { NativeLibrary.Free(library); library = 0; }
    }

    private IOException Error() => new("Не удалось прочитать профиль. Закройте исходный браузер и повторите импорт. " + (database == 0 ? "" : Marshal.PtrToStringUTF8(errorMessage(database))));
    private T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(byte[] path, out nint database, int flags, nint vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Close(nint database);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BusyTimeout(nint database, int milliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Prepare(nint database, byte[] sql, int length, out nint statement, nint tail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Step(nint statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FinalizeStatement(nint statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ColumnText(nint statement, int column);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ErrorMessage(nint database);
}
