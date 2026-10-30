using CefSharp;
using CefSharp.Handler;
using CefSharp.WinForms;

namespace MBrowser;

internal sealed class CefPasswordManagerPageHandler(MainForm owner) : RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser webBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain) return false;
        if (request.Url.StartsWith("chrome://password-manager/", StringComparison.OrdinalIgnoreCase)
            || request.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return false;
        if (AddressParser.TryWebUrl(request.Url, out var target))
            owner.DispatchCefUi(() => owner.OpenTabAsync(target));
        return true;
    }
}

internal sealed partial class MainForm
{
    private void ShowNativeChromiumPasswordManager()
    {
        if (!_preferences.ExperimentalChromePasswordManager)
        {
            _status.Text = "Включите экспериментальный режим и перезапустите браузер.";
            return;
        }
        var panel = new GlassPanel { Size = new Size(920, 640), Padding = new Padding(6) };
        var close = Palette.Button("Закрыть · Менеджер Chromium", "Закрыть", 360);
        close.Dock = DockStyle.Top;
        close.Click += (_, _) => CloseOverlay();
        var view = new ChromiumWebBrowser("chrome://password-manager/passwords")
        {
            Dock = DockStyle.Fill,
            RequestHandler = new CefPasswordManagerPageHandler(this)
        };
        panel.Controls.Add(view);
        panel.Controls.Add(close);
        ShowOverlay(panel, new Point(18, 10));
    }
}