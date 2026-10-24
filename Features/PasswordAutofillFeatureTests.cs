using System.Text.Json;

namespace MBrowser.Features;

internal static class PasswordAutofillFeatureTests
{
    internal static void Run(Action<string, Action> check, string folder)
    {
        check("Autofill metadata excludes secrets and matches exact origins", () =>
        {
            string root = Path.Combine(folder, "autofill-api");
            Directory.CreateDirectory(root);
            string csv = Path.Combine(root, "fixture.csv");
            File.WriteAllText(csv, "name,url,username,password\nFixture,https://example.test:444/login,fixture-user,fixture-metadata-secret\nDefault,https://example.test/,other-user,other-secret\n");
            var library = new BrowserLibrary(root);
            Assert(library.ImportPasswordsCsv(csv) == 2, "Fixture import failed");
            string json = library.GetAutofillSuggestions("https://example.test:444/other");
            using var metadata = JsonDocument.Parse(json);
            Assert(metadata.RootElement.GetArrayLength() == 1 && metadata.RootElement[0].GetProperty("username").GetString() == "fixture-user", "Scheme/host/port isolation failed");
            Assert(!json.Contains("secret", StringComparison.Ordinal) && !json.Contains("password", StringComparison.OrdinalIgnoreCase), "Suggestion metadata contains a credential secret");
            string id = metadata.RootElement[0].GetProperty("id").GetString()!;
            Assert(library.CreateAutofillScript("https://example.test:445/", id) is null, "Credential selected across ports");
            Assert(library.CreateAutofillScript("https://other.test/", id) is null, "Credential selected across hosts");
            Assert(library.CreateAutofillScript("http://example.test:444/", id) is null, "Credential selected over insecure HTTP");
            Assert(library.CreateAutofillScript("https://example.test:444/", Guid.NewGuid().ToString()) is null, "Unknown credential ID accepted");
        });

        check("Autofill origin canonicalization accepts HTTPS and loopback only", () =>
        {
            Assert(BrowserLibrary.AutofillOrigin("https://пример.рф:443/login") == "https://xn--e1afmkfd.xn--p1ai", "IDN origin mismatch");
            Assert(BrowserLibrary.AutofillOrigin("http://[::1]:8080/") == "http://[::1]:8080", "Loopback origin mismatch");
            foreach (string url in new[] { "http://example.test/", "http://localhost.evil.test/", "file:///private", "https://user:secret@example.test/" })
                Assert(BrowserLibrary.AutofillOrigin(url) is null, "Untrusted autofill origin accepted");
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
