using CefSharp;
using CefSharp.Handler;

namespace MBrowser;

/// <summary>Keep untrusted page navigation inside the browser's HTTP/HTTPS and explicitly opened file policy.</summary>
internal sealed class CefNavigationHandler(MainForm owner, BrowserTab tab) : RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain) return false;
        string url = request.Url;
        if (url == "about:blank" || AddressParser.TryWebUrl(url, out _) ||
            BrowserDocuments.IsApproved(url, tab.LocalDocuments))
            return false;
        owner.DispatchCefUi(() =>
        {
            owner.CefNavigationBlocked(url);
            return Task.CompletedTask;
        });
        return true;
    }
}

internal sealed class CefPopupHandler(MainForm owner) : LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, string targetUrl, string targetFrameName,
        WindowOpenDisposition targetDisposition, bool userGesture,
        IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
        ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        // Block unsupported schemes. A page must not open an arbitrary local file/command.
        if (AddressParser.TryWebUrl(targetUrl, out var validated))
            owner.DispatchCefUi(() => owner.OpenTabAsync(validated));
        return true;
    }
}

internal sealed class CefDisplayHandler(MainForm owner) : DisplayHandler
{
    protected override void OnFullscreenModeChange(IWebBrowser chromiumWebBrowser, IBrowser browser, bool fullscreen)
    {
        owner.DispatchCefUi(() =>
        {
            owner.CefSetFullscreen(fullscreen);
            return Task.CompletedTask;
        });
    }
}

internal sealed partial class MainForm
{
    internal void CefSetFullscreen(bool fullscreen) => SetSiteFullscreen(fullscreen);
    internal void CefNavigationBlocked(string url) =>
        _status.Text = "CEF: адрес заблокирован политикой безопасности.";
}
