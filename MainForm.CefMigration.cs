namespace MBrowser;

/// <summary>Migration guidance never reads or modifies the legacy Edge/WebView2 profile.</summary>
internal static class CefMigrationNotice
{
    internal static bool NeedsNotice(string profile) =>
        Directory.Exists(Path.Combine(profile, ProfileMigration.LegacyWebViewArchive)) &&
        !File.Exists(Path.Combine(profile, "cef-migration-notice-dismissed"));

    internal static void Dismiss(string profile)
    {
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "cef-migration-notice-dismissed"),
            "CEF-only migration guidance acknowledged");
    }
}

internal sealed partial class MainForm
{
    private void ShowCefMigrationNotice()
    {
        ShowMenuPage("Переход с WebView2 на CEF", add =>
        {
            add("Сеанс, закладки, настройки и пароли MBrowser сохранены", () => { });
            add("Cookie и пароли Edge сохранены в архиве, но не импортированы в CEF", () => { });
            add("Импортировать cookie и закладки из файла", ShowImport);
            add("Импортировать пароли CSV в защищённое хранилище", ShowPasswords);
            add("Архив WebView2 сохранён в папке MBrowser", () => { });
            add("Напомнить позже", () => { });
            add("Понятно, больше не показывать", () =>
            {
                try { CefMigrationNotice.Dismiss(DataPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { ShowError(ex); }
            });
        });
    }
}