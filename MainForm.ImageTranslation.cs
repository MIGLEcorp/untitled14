using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private bool _imageTranslationRunning;
    private async Task ShowImageTranslationSettingsAsync()
    {
        var languages = await ImageText.LanguagesAsync();
        ShowMenuPage("Язык текста на картинке", row =>
        {
            row("Язык системы", () => { _preferences.ImageOcrLanguage = ""; _preferences.Save(DataPath); });
            foreach (var language in languages) row(language.DisplayName, () => { _preferences.ImageOcrLanguage = language.LanguageTag; _preferences.Save(DataPath); });
            row("Язык перевода  ›", () => ShowMenuPage("Переводить картинки на…", add =>
            {
                foreach (var (code, name) in new[] { ("ru", "Русский"), ("en", "English"), ("ja", "日本語"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"), ("zh-CN", "中文") })
                    add(name, () => { _preferences.ImageTargetLanguage = code; _preferences.Save(DataPath); });
            }));
        });
    }
}
