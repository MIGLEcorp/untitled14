using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MBrowser;

internal sealed partial class MainForm
{
    private bool _translatorChecked;
    private bool _translatorInstalling;
    private async Task EnsureTranslatorAsync(CoreWebView2 core)
    {
        if (_translatorChecked) return;
        _translatorChecked = true;
        var state = TranslatorPackage.Read(DataPath);
        if (state is not { Pending: true }) return;
        try
        {
            var installed = (await core.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(item => item.Id == state.Id);
            installed ??= await core.Profile.AddBrowserExtensionAsync(TranslatorPackage.Folder(DataPath));
            await installed.EnableAsync(true);
            TranslatorPackage.Write(DataPath, new(false, installed.Id));
        }
        catch { _translatorChecked = false; throw; }
    }
    private void ShowTranslatorMenu() => ShowMenuPage("Переводчик", add =>
    {
        add("Перевод картинок · язык и настройки", () => RunUi(ShowImageTranslationSettingsAsync));
        var state = TranslatorPackage.Read(DataPath);
        add(state is null ? "Загрузить Immersive Translate" : "Подключить / включить переводчик", () => RunUi(InstallTranslatorAsync));
        if (state is not null)
        {
            add("Панель перевода", () => RunUi(() => ShowTranslatorPageAsync("popup.html")));
            add("Настройки переводчика", () => RunUi(() => ShowTranslatorPageAsync("options.html")));
        }
        add("Перевод картинок — как пользоваться", () => ShowMenuPage("Перевод картинок", row =>
        {
            row("ПКМ → Перевести текст картинки · MBrowser", () => { });
            row("Перевод поверх видимой части картинки", () => { });
            row("× на картинке возвращает оригинал", () => { });
            row("OCR Windows · перевод текста Google", () => { });
            row("Выбрать языки", () => RunUi(ShowImageTranslationSettingsAsync));
        }));
        add("О сервисе и обработке данных", () => RunUi(() => OpenTabAsync("https://immersivetranslate.com/en/docs/usage/#image-translation-beta")));
    });
    private async Task InstallTranslatorAsync()
    {
        if (_translatorInstalling) return;
        _translatorInstalling = true;
        var progress = new GlassPanel { Size = new Size(420, 150) };
        progress.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 12),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Загрузка и подключение переводчика…"
        });
        ShowOverlay(progress, new Point((_viewport.Width - progress.Width) / 2, 20));
        try
        {
            await TranslatorPackage.PrepareAsync(DataPath);
            _translatorChecked = false;
            await WithProfileCoreAsync(EnsureTranslatorAsync);
            if (!_closing) ShowTranslatorMenu();
        }
        finally { _translatorInstalling = false; if (_overlay == progress) CloseOverlay(); }
    }
    internal async Task ShowTranslatorPageAsync(string page)
    {
        var state = TranslatorPackage.Read(DataPath);
        if (state is null || state.Pending) { await InstallTranslatorAsync(); state = TranslatorPackage.Read(DataPath)!; }
        var panel = new GlassPanel { UseArtwork = false, Size = new Size(900, 650), Padding = new Padding(6) };
        var close = Palette.Button("Закрыть переводчик", "Закрыть переводчик", 240); close.Dock = DockStyle.Top;
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Palette.Background };
        panel.Controls.Add(view); panel.Controls.Add(close); close.Click += (_, _) => CloseOverlay();
        ShowOverlay(panel, new Point((_viewport.Width - panel.Width) / 2, 10));
        try
        {
            _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataPath, "WebView2"), EnvironmentOptions());
            await view.EnsureCoreWebView2Async(_environment);
            if (view.IsDisposed) return;
            view.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (AddressParser.TryWebUrl(e.Uri, out var url)) RunUi(() => OpenTabAsync(url)); };
            view.CoreWebView2.Navigate("chrome-extension://" + state.Id + "/" + page);
        }
        catch { if (_overlay == panel) CloseOverlay(); throw; }
    }
    internal Task InstallTranslatorForTestAsync(string archive) => RunTranslatorTestInstallAsync(archive);
    internal CoreWebView2? TranslatorCoreForTest => _overlay?.Controls.OfType<WebView2>().FirstOrDefault()?.CoreWebView2;
    private async Task RunTranslatorTestInstallAsync(string archive)
    { await TranslatorPackage.PrepareAsync(DataPath, archive); _translatorChecked = false; await WithProfileCoreAsync(EnsureTranslatorAsync); }
}
