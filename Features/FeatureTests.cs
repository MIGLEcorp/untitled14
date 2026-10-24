using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MBrowser.Features;

/// <summary>Fixture-only tests. Never inspects installed browsers or real credentials.</summary>
public static class FeatureTests
{
    public static void Run(Action<string, Action> check, string folder)
    {
        string root = Path.Combine(folder, "features");
        Directory.CreateDirectory(root);
        PasswordAutofillFeatureTests.Run(check, root);

        check("Bookmark HTML import, URL safety and export roundtrip", () =>
        {
            const string html = "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><DT><A HREF=\"https://example.test/?a=1&amp;b=2\">Тест &amp; книга</A><DT><A href='javascript:alert(1)'>bad</A><DT><A HREF='https://example.test/?a=1&amp;b=2'>duplicate</A></DL>";
            var imported = BookmarkStore.ImportHtml(html);
            Assert(imported.Count == 1 && imported[0].Title == "Тест & книга" && imported[0].Url.EndsWith("?a=1&b=2"), "HTML parsing or validation failed");
            var store = new BookmarkStore(Path.Combine(root, "bookmarks.json"));
            Assert(store.Add(imported) == 1 && store.Add(imported) == 0, "Bookmark deduplication failed");
            string export = Path.Combine(root, "bookmarks.html");
            store.ExportHtml(export);
            Assert(BookmarkStore.ImportHtml(File.ReadAllText(export)).SequenceEqual(imported), "HTML export roundtrip failed");
            store.Remove(imported[0].Url);
            Assert(store.Load().Count == 0, "Bookmark deletion did not persist");
        });

        check("Chromium bookmark tree and corrupt store behavior", () =>
        {
            const string json = "{\"roots\":{\"bookmark_bar\":{\"children\":[{\"name\":\"Folder\",\"children\":[{\"name\":\"Page\",\"url\":\"https://example.test/page\"},{\"name\":\"Bad\",\"url\":\"file:///private\"}]}]},\"other\":{\"children\":[{\"name\":\"Second\",\"url\":\"https://second.test/\"}]}}}";
            var entries = BookmarkStore.ImportJson(json);
            Assert(entries.Count == 2 && entries[0].Title == "Page", "Nested Chromium folders not read");
            string corrupt = Path.Combine(root, "corrupt-bookmarks.json");
            File.WriteAllText(corrupt, "{broken");
            bool rejected = false;
            try { new BookmarkStore(corrupt).Add(entries); } catch (System.Text.Json.JsonException) { rejected = true; }
            Assert(rejected && File.ReadAllText(corrupt) == "{broken", "Corrupt store was silently overwritten");
        });

        check("CSV passwords preserve commas, quotes, multiline and Unicode", () =>
        {
            const string csv = "\uFEFFname,url,username,password\r\n\"Example, test\",https://example.test/login,\"user\"\"name\",\"line1\nline2,секрет\"\r\nBad,javascript:alert(1),user,secret\r\n";
            var entries = PasswordVault.ParseCsv(csv);
            Assert(entries.Count == 1 && entries[0].Title == "Example, test" && entries[0].Username == "user\"name" && entries[0].Password == "line1\nline2,секрет", "CSV escaping failed");
            bool rejected = false;
            try { PasswordVault.ParseCsv("url,username,password\nhttps://example.test/,user,\"unterminated"); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Malformed CSV was accepted");
        });

        check("Password vault encrypts at rest and detects tampering", () =>
        {
            string path = Path.Combine(root, "passwords.dpapi");
            var vault = new PasswordVault(path);
            var entry = new PasswordEntry(Guid.NewGuid(), "Fixture", "https://example.test/login", "fixture-user", "unique-plaintext-fixture-secret");
            vault.Save([entry]);
            byte[] encrypted = File.ReadAllBytes(path);
            Assert(!Encoding.UTF8.GetString(encrypted).Contains(entry.Password, StringComparison.Ordinal), "Vault contains plaintext password");
            Assert(vault.Load().Single() == entry, "DPAPI vault roundtrip failed");
            encrypted[encrypted.Length / 2] ^= 0x7F;
            File.WriteAllBytes(path, encrypted);
            bool rejected = false;
            try { vault.Load(); } catch (CryptographicException) { rejected = true; }
            Assert(rejected, "Tampered vault was accepted");
        });

        check("Cookie exports preserve flags and reject unsafe records", () =>
        {
            string future = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var result = CookieImport.ParseNetscape("# Netscape HTTP Cookie File\n#HttpOnly_.example.test\tTRUE\t/\tTRUE\t" + future + "\tsid\tfixture\nexample.test\tFALSE\t/\tFALSE\t0\tpref\tdark\nexample.test\tFALSE\t/\tFALSE\t1\texpired\told\n");
            Assert(result.Cookies.Count == 2 && result.Skipped == 1 && result.Cookies[0].IsHttpOnly && result.Cookies[0].IsSecure && result.Cookies[0].Domain == ".example.test" && result.Cookies[1].ExpiresUtc is null, "Netscape cookie flags or expiration failed");
            var json = CookieImport.ParseJson("[{\"domain\":\"example.test\",\"name\":\"sid\",\"value\":\"ok\",\"secure\":true,\"sameSite\":\"no_restriction\",\"session\":true},{\"domain\":\"example.test\",\"name\":\"partition\",\"value\":\"x\",\"partitionKey\":\"https://other.test\"},{\"domain\":\"example.test\",\"name\":\"unsafe\",\"value\":\"x\",\"sameSite\":\"none\"},{\"domain\":\"example.test\",\"name\":\"bad;name\",\"value\":\"x\"},{\"domain\":\"example.test\",\"name\":\"encrypted\",\"encrypted_value\":\"fixture-ciphertext\"}]");
            Assert(json.Cookies.Count == 1 && json.Skipped == 4 && json.Cookies[0].SameSite == "None", "JSON cookie safety or partition handling failed");
            Assert(CookieImport.ParseJson(CookieImport.Serialize(result.Cookies)).Cookies.SequenceEqual(result.Cookies), "Cookie module-boundary JSON changed flags or expiry");
        });

        check("Partitioned exports preserve site, ancestor bit and separate stores", () =>
        {
            const string json = """
                [
                  {"domain":"third.test","name":"sid","value":"normal","secure":true},
                  {"domain":"third.test","name":"sid","value":"partition-one","secure":true,"partitionKey":{"topLevelSite":"https://one.test","hasCrossSiteAncestor":true}},
                  {"domain":"third.test","name":"sid","value":"partition-two","secure":true,"partitionKey":"https://two.test"},
                  {"domain":"third.test","name":"sid","value":"container","storeId":"firefox-container-1"},
                  {"domain":"third.test","name":"sid","value":"isolated","firstPartyDomain":"one.test"},
                  {"domain":"third.test","name":"sid","value":"insecure","partitionKey":"https://one.test"},
                  {"domain":"third.test","name":"sid","value":"no-context","secure":true,"partitioned":true},
                  {"domain":"third.test","name":"sid","value":"invalid-site","secure":true,"partitionKey":"https://one.test/path"}
                ]
                """;
            var result = CookieImport.ParseJson(json);
            Assert(result.Cookies.Count == 3 && result.Skipped == 5, "Partitions were merged or unsupported contexts accepted");
            Assert(result.Cookies[1].PartitionKey == new CookiePartitionKey("https://one.test", true) &&
                result.Cookies[2].PartitionKey == new CookiePartitionKey("https://two.test"), "Partition key lost site or ancestor bit");
            Assert(result.SkipReasons![CookieSkipReason.IsolatedContext] == 2 && result.SkipReasons[CookieSkipReason.UnsupportedPartition] == 3,
                "Cookie skip reasons do not identify isolated or unsupported contexts");
            Assert(CookieImport.ParseJson(CookieImport.Serialize(result.Cookies)).Cookies.SequenceEqual(result.Cookies), "Partition identity lost at module boundary");
        });

        check("Password fill script encodes secrets and checks exact origin", () =>
        {
            var entry = new PasswordEntry(Guid.NewGuid(), "Fixture", "https://example.test:444/login", "\"user", "</script>'\nsecret");
            string script = PasswordVault.BuildFillScript(entry.Origin, entry);
            Assert(script.Contains("location.origin !== entry.origin", StringComparison.Ordinal) && script.Contains("!window.isSecureContext", StringComparison.Ordinal), "Origin or secure-context guard missing");
            Assert(script.Contains("https://example.test:444", StringComparison.Ordinal) && !script.Contains("</script>", StringComparison.Ordinal) && !script.Contains(".submit(", StringComparison.Ordinal), "Secret encoding or manual-submit contract failed");
            Assert(FeatureFiles.Origin(new Uri("https://пример.рф:443/login")) == "https://xn--e1afmkfd.xn--p1ai", "IDN origin does not match browser serialization");
            Assert(FeatureFiles.Origin(new Uri("http://[::1]:8080/login")) == "http://[::1]:8080", "IPv6 origin does not match browser serialization");
        });

        check("Firefox SQLite imports are read-only and preserve isolation", () =>
        {
            string path = Path.Combine(root, "fixture-cookies");
            string future = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            CreateSqliteFixture(path,
                "PRAGMA user_version=15; CREATE TABLE moz_places(id INTEGER PRIMARY KEY,title TEXT,url TEXT); CREATE TABLE moz_bookmarks(type INTEGER,title TEXT,fk INTEGER); " +
                "INSERT INTO moz_places VALUES(1,'Fixture','https://example.test/'); INSERT INTO moz_bookmarks VALUES(1,'Saved fixture',1); " +
                "CREATE TABLE moz_cookies(host TEXT,name TEXT,value TEXT,path TEXT,isSecure INTEGER,isHttpOnly INTEGER,expiry INTEGER,sameSite INTEGER,originAttributes TEXT); " +
                $"INSERT INTO moz_cookies VALUES('.example.test','sid','fixture','/',1,1,{future},2,''); " +
                $"INSERT INTO moz_cookies VALUES('.example.test','container','isolated','/',1,1,{future},2,'^userContextId=1');");
            byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            var bookmarks = FirefoxImport.ReadBookmarks(path);
            var cookies = CookieImport.ReadFile(path);
            Assert(bookmarks.Count == 1 && bookmarks[0].Title == "Saved fixture", "Firefox bookmark query failed");
            Assert(cookies.Cookies.Count == 1 && cookies.Skipped == 1 && cookies.Cookies[0].SameSite == "Strict" && cookies.Cookies[0].IsHttpOnly, "Firefox cookies lost flags or container isolation");
            Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "SQLite source was modified");
            File.Delete(path);
            Assert(!File.Exists(path), "SQLite source still held open");
        });

        check("Modern Firefox millisecond expiry and Total Cookie Protection import", () =>
        {
            string path = Path.Combine(root, "firefox-schema17.sqlite");
            var future = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds());
            long expiry = future.ToUnixTimeMilliseconds();
            CreateSqliteFixture(path,
                "PRAGMA user_version=17; CREATE TABLE moz_cookies(host TEXT,name TEXT,value TEXT,path TEXT,isSecure INTEGER,isHttpOnly INTEGER,expiry INTEGER,sameSite INTEGER,originAttributes TEXT,inBrowserElement INTEGER,isSession INTEGER); " +
                $"INSERT INTO moz_cookies VALUES('third.test','sid','ordinary','/',1,1,{expiry},2,'',0,0); " +
                $"INSERT INTO moz_cookies VALUES('third.test','sid','partition1','/',1,1,{expiry},0,'^partitionKey=%28https%2Cone.test%29',0,0); " +
                $"INSERT INTO moz_cookies VALUES('third.test','sid','partition2','/',1,1,{expiry},0,'^partitionKey=%28https%2Ctwo.test%2Cf%29',0,0); " +
                $"INSERT INTO moz_cookies VALUES('third.test','sid','container','/',1,1,{expiry},0,'^userContextId=1&partitionKey=%28https%2Cone.test%29',0,0); " +
                $"INSERT INTO moz_cookies VALUES('third.test','sid','insecure-partition','/',0,0,{expiry},1,'^partitionKey=%28https%2Cone.test%29',0,0); " +
                "INSERT INTO moz_cookies VALUES('third.test','session','session','/',1,0,0,1,'',0,0); " +
                "INSERT INTO moz_cookies VALUES('third.test','expired','expired','/',1,0,1,1,'',0,0); " +
                "INSERT INTO moz_cookies VALUES('third.test','restored-session','session','/',1,0,1,1,'',0,1); ");
            byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            var result = CookieImport.ReadFile(path);
            Assert(result.Cookies.Count == 5 && result.Skipped == 3, "Modern Firefox cookies were rejected or partition stores merged");
            Assert(result.Cookies[0].ExpiresUtc == future && result.Cookies[0].IsHttpOnly && result.Cookies[0].SameSite == "Strict", "Millisecond expiry was read as seconds or flags lost");
            Assert(result.Cookies[1].PartitionKey == new CookiePartitionKey("https://one.test") &&
                result.Cookies[2].PartitionKey == new CookiePartitionKey("https://two.test", true), "Firefox partition suffix decoding failed");
            Assert(result.Cookies.Count(cookie => cookie.ExpiresUtc is null) == 2, "Session cookies became persistent or expired");
            Assert(result.SkipReasons![CookieSkipReason.IsolatedContext] == 1 && result.SkipReasons[CookieSkipReason.UnsupportedPartition] == 1 &&
                result.SkipReasons[CookieSkipReason.Expired] == 1, "Skip reasons do not match source records");
            Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Modern Firefox source was modified");
            File.Delete(path);
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CreateSqliteFixture(string path, string sql)
    {
        nint library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"));
        nint database = 0;
        var close = Marshal.GetDelegateForFunctionPointer<SqliteClose>(NativeLibrary.GetExport(library, "sqlite3_close"));
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<SqliteOpen>(NativeLibrary.GetExport(library, "sqlite3_open_v2"));
            var exec = Marshal.GetDelegateForFunctionPointer<SqliteExec>(NativeLibrary.GetExport(library, "sqlite3_exec"));
            Assert(open(Encoding.UTF8.GetBytes(path + '\0'), out database, 6, 0) == 0, "Could not create SQLite fixture");
            Assert(exec(database, Encoding.UTF8.GetBytes(sql + '\0'), 0, 0, 0) == 0, "Could not populate SQLite fixture");
        }
        finally
        {
            if (database != 0) close(database);
            NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SqliteOpen(byte[] path, out nint database, int flags, nint vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SqliteClose(nint database);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SqliteExec(nint database, byte[] sql, nint callback, nint argument, nint error);
}
