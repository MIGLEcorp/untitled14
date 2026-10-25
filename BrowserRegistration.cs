using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MBrowser;

/// <summary>Registers candidates; Windows alone writes protected default UserChoice associations.</summary>
internal static class BrowserRegistration
{
    internal const string Client = @"Software\Clients\StartMenuInternet\MBrowser";
    internal const string Capabilities = Client + @"\Capabilities";
    internal const string UrlId = "MBrowser.Url", DocumentId = "MBrowser.Document";
    internal static string Command(string executable) => $"\"{executable}\" --open \"%1\"";
    internal static void Register(string executable, RegistryKey? root = null)
    {
        root ??= Registry.CurrentUser;
        executable = Path.GetFullPath(executable);
        string icon = $"\"{executable}\",0";
        void Set(string path, string name, string value) { using var key = root.CreateSubKey(path); key.SetValue(name, value); }
        Set(Client, "", "MBrowser"); Set(Client + @"\DefaultIcon", "", icon);
        Set(Client + @"\shell\open\command", "", $"\"{executable}\"");
        Set(Capabilities, "ApplicationName", "MBrowser");
        Set(Capabilities, "ApplicationDescription", "MBrowser — ссылки, веб-документы, PDF и изображения");
        Set(Capabilities, "ApplicationIcon", icon); Set(Capabilities + @"\Startmenu", "StartMenuInternet", "MBrowser");
        foreach (string scheme in new[] { "http", "https" }) Set(Capabilities + @"\URLAssociations", scheme, UrlId);
        foreach (string extension in BrowserDocuments.Extensions)
        {
            Set(Capabilities + @"\FileAssociations", extension, DocumentId);
            Set(@"Software\Classes\" + extension + @"\OpenWithProgids", DocumentId, "");
        }
        foreach (string id in new[] { UrlId, DocumentId })
        {
            Set(@"Software\Classes\" + id, "", "MBrowser");
            Set(@"Software\Classes\" + id + @"\DefaultIcon", "", icon);
            Set(@"Software\Classes\" + id + @"\shell\open\command", "", Command(executable));
            Set(@"Software\Classes\" + id + @"\Application", "ApplicationName", "MBrowser");
            Set(@"Software\Classes\" + id + @"\Application", "ApplicationIcon", icon);
        }
        Set(@"Software\Classes\" + UrlId, "URL Protocol", "");
        Set(@"Software\RegisteredApplications", "MBrowser", Capabilities);
        if (root.Name == Registry.CurrentUser.Name) SHChangeNotify(0x08000000, 0, 0, 0);
    }
    internal static void Unregister(string executable, RegistryKey? root = null)
    {
        root ??= Registry.CurrentUser;
        string? command;
        using (var registered = root.OpenSubKey(@"Software\Classes\" + UrlId + @"\shell\open\command")) command = registered?.GetValue("") as string;
        if (!string.Equals(command, Command(Path.GetFullPath(executable)), StringComparison.OrdinalIgnoreCase)) return;
        foreach (string path in new[] { Client, @"Software\Classes\" + UrlId, @"Software\Classes\" + DocumentId }) root.DeleteSubKeyTree(path, false);
        using var apps = root.OpenSubKey(@"Software\RegisteredApplications", true); apps?.DeleteValue("MBrowser", false);
        foreach (string extension in BrowserDocuments.Extensions)
        { using var key = root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true); key?.DeleteValue(DocumentId, false); }
        if (root.Name == Registry.CurrentUser.Name) SHChangeNotify(0x08000000, 0, 0, 0);
    }
    internal static void OpenSettings()
    {
        Register(Application.ExecutablePath);
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=MBrowser") { UseShellExecute = true });
    }
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint action, uint flags, nint item1, nint item2);
}

internal static class BrowserDocuments
{
    internal static readonly string[] Extensions = [".htm", ".html", ".shtml", ".xht", ".xhtml", ".mht", ".mhtml", ".pdf", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".ico", ".txt", ".xml", ".json"];
    internal static bool TryResolve(string input, out string url)
    {
        url = "";
        try
        {
            string path = input;
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
            if (!Path.IsPathFullyQualified(path) || path.Length < 3 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return false;
            path = Path.GetFullPath(path);
            if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            url = new Uri(path).AbsoluteUri; return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
    internal static bool IsApproved(string url, IEnumerable<string> approved)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        string plain = new UriBuilder(uri) { Fragment = "", Query = "" }.Uri.AbsoluteUri;
        return approved.Contains(plain, StringComparer.OrdinalIgnoreCase);
    }
    internal static string[] ParseArguments(string[] args) => args.Where(value => value != "--open")
        .Take(16).Where(value => AddressParser.TryWebUrl(value, out _) || TryResolve(value, out _)).ToArray();
}
