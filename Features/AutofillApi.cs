using System.Text;

namespace MBrowser.Features;

public sealed partial class BrowserLibrary
{
    /// <summary>Only display metadata crosses the collectible-module boundary.</summary>
    public string GetAutofillSuggestions(string url)
    {
        string? origin = AutofillOrigin(url);
        return Encoding.UTF8.GetString(FeatureFiles.WriteJson(writer =>
        {
            writer.WriteStartArray();
            if (origin is not null)
                foreach (var entry in passwords.Load().Where(entry => entry.Origin == origin).Take(50))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", entry.Id);
                    writer.WriteString("username", entry.Username);
                    writer.WriteString("title", entry.Title);
                    writer.WriteEndObject();
                }
            writer.WriteEndArray();
        }));
    }

    public string? CreateAutofillScript(string url, string id) => CreateCredentialScript(url, id, overwrite: true);

    public string? CreateAutomaticAutofillScript(string url, string id) => CreateCredentialScript(url, id, overwrite: false);

    // Also usable by non-modal import flows and fixture tests; no vault object escapes the module.
    public int ImportPasswordsCsv(string path) => passwords.ImportCsv(path);

    private string? CreateCredentialScript(string url, string id, bool overwrite)
    {
        string? origin = AutofillOrigin(url);
        if (origin is null || !Guid.TryParse(id, out var entryId)) return null;
        var entry = passwords.Load().FirstOrDefault(item => item.Id == entryId && item.Origin == origin);
        return entry is null ? null : PasswordVault.BuildFillScript(origin, entry, overwrite);
    }

    internal static string? AutofillOrigin(string url)
    {
        if (!FeatureFiles.TryWebUrl(url, out string valid)) return null;
        var uri = new Uri(valid);
        if (uri.Scheme != "https" && !uri.IsLoopback) return null;
        return FeatureFiles.Origin(uri);
    }
}
