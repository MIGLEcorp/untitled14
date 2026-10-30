using CefSharp;
using CefSharp.Handler;
using CefSharp.WinForms;
using System.Text.Json;

namespace MBrowser;

internal sealed class CefExtensionPageRequestHandler(MainForm owner, string extensionId) : RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser webBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain) return false;
        string prefix = "chrome-extension://" + extensionId + "/";
        if (request.Url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (AddressParser.TryWebUrl(request.Url, out var url))
            owner.DispatchCefUi(() => owner.OpenTabAsync(url));
        return true;
    }
}

internal sealed partial class MainForm
{
    internal async Task<bool> CefExtensionPopupReadyForTestAsync(string extensionId)
    {
        if (extensionId.Length != 32 || extensionId.Any(c => c is < 'a' or > 'p'))
            return false;
        string uri = "chrome-extension://" + extensionId + "/popup.html";
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var browser = new ChromiumWebBrowser(uri) { Dock = DockStyle.Fill };
        browser.FrameLoadEnd += (_, e) =>
        {
            if (e.Frame.IsMain && e.Url.StartsWith(uri, StringComparison.OrdinalIgnoreCase))
                loaded.TrySetResult(e.HttpStatusCode == 200);
        };
        _viewport.Controls.Add(browser);
        browser.BringToFront();
        try
        {
            bool success = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(12));
            if (!success || !browser.CanExecuteJavascriptInMainFrame) return false;
            var response = await browser.EvaluateScriptAsync(
                "document.getElementById('mbrowser-popup-smoke')?.textContent === 'popup-ok'");
            return response.Success && response.Result is true;
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException)
        { Program.LogError(DataPath, error); return false; }
        finally { _viewport.Controls.Remove(browser); }
    }

    private static (string? Popup, string? Options) CefExtensionPages(CefExtensionEntry entry)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(entry.Folder, "manifest.json")));
        var root = document.RootElement;
        string? popup = null, options = null;
        if (root.TryGetProperty("action", out var action) &&
            action.TryGetProperty("default_popup", out var popupNode))
            popup = popupNode.GetString();
        if (root.TryGetProperty("options_page", out var optionNode))
            options = optionNode.GetString();
        if (root.TryGetProperty("options_ui", out var optionsUi) &&
            optionsUi.TryGetProperty("page", out var pageNode))
            options = pageNode.GetString();
        return (popup, options);
    }

    private void ShowCefExtensionPage(CefExtensionEntry entry, string path)
    {
        if (!entry.Enabled || entry.Id.Length != 32 ||
            entry.Id.Any(c => c < 'a' || c > 'p'))
            throw new InvalidOperationException("Расширение выключено или идентификатор некорректен.");
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['?', '#', ':', '\\']) >= 0 ||
            path.StartsWith('/') || path.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new InvalidDataException("Некорректный адрес страницы расширения.");
        string baseDir = Path.GetFullPath(entry.Folder);
        string absolutePath = Path.GetFullPath(Path.Combine(baseDir, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!absolutePath.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(absolutePath))
            throw new FileNotFoundException("Страница расширения отсутствует.");
        var panel = new GlassPanel { UseArtwork = false, Size = new Size(850, 600), Padding = new Padding(6) };
        var close = Palette.Button("Закрыть расширение", "Закрыть страницу расширения", 260);
        close.Dock = DockStyle.Top;
        close.Click += (_, _) => CloseOverlay();
        var uri = "chrome-extension://" + entry.Id + "/" + path;
        var view = new ChromiumWebBrowser(uri)
        {
            Dock = DockStyle.Fill,
            RequestHandler = new CefExtensionPageRequestHandler(this, entry.Id),
            LifeSpanHandler = new CefPopupHandler(this),
            DownloadHandler = new CefDownloadsHandler(this)
        };
        panel.Controls.Add(view);
        panel.Controls.Add(close);
        ShowOverlay(panel, new Point((_viewport.Width - panel.Width) / 2, 10));
    }
}
