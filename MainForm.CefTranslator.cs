namespace MBrowser;

internal sealed partial class MainForm
{
    private bool _translatorInstalling;

    private void ShowTranslatorMenu() => ShowMenuPage("Перевод · Chromium CEF", row =>
    {
        row("Перевести текущую страницу через Google Translate", () =>
            RunUi(OpenCurrentPageInTranslationAsync));
        row("Перевод текста на изображениях · настройки", () => RunUi(ShowImageTranslationSettingsAsync));
        row("Установить Immersive Translate (экспериментальный MV3)", () =>
            RunUi(InstallTranslatorAsync));
        row("Управление расширениями", () => RunUi(ShowExtensionsAsync));
        row("Справка по Immersive Translate", () =>
            RunUi(() => OpenTabAsync("https://immersivetranslate.com/en/docs/")));
        row("Перевод через Google передаёт адрес страницы стороннему сервису", () => { });
    });

    private Task OpenCurrentPageInTranslationAsync()
    {
        string? address = ActiveTab.CurrentUrl;
        if (address is null || !AddressParser.TryWebUrl(address, out string verified))
        {
            _status.Text = "Откройте веб-страницу, которую нужно перевести.";
            return Task.CompletedTask;
        }
        if (new Uri(verified).IsLoopback)
        {
            _status.Text = "Перевод локальных сайтов через внешний сервис отключён.";
            return Task.CompletedTask;
        }
        string language = _preferences.ImageTargetLanguage;
        if (string.IsNullOrWhiteSpace(language)) language = "ru";
        var page = "https://translate.google.com/translate?sl=auto&tl=" +
            Uri.EscapeDataString(language) + "&u=" + Uri.EscapeDataString(verified);
        return OpenTabAsync(page);
    }

    private async Task InstallTranslatorAsync()
    {
        if (_translatorInstalling) return;
        _translatorInstalling = true;
        try
        {
            await TranslatorPackage.PrepareAsync(DataPath);
            string folder = TranslatorPackage.Folder(DataPath);
            if (!File.Exists(Path.Combine(folder, "manifest.json")))
                throw new InvalidDataException("Не обнаружен manifest.json переводчика.");
            try { CefExtensionRegistry.Add(DataPath, folder); }
            catch (InvalidOperationException error) when (error.Message.Contains("уже установлено", StringComparison.Ordinal))
            { }
            _status.Text = "Пакет Immersive Translate сохранён. Перезапустите MBrowser. " +
                "Поддержка его API зависит от CEF.";
        }
        finally { _translatorInstalling = false; }
    }
}
