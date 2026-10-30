using CefSharp;
using CefSharp.Handler;

namespace MBrowser;

internal sealed class CefImageContextMenuHandler(MainForm owner) : ContextMenuHandler
{
    private static readonly CefMenuCommand TranslateImageCommand = (CefMenuCommand)((int)CefMenuCommand.CustomFirst + 50);

    protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser,
        IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
    {
        if (parameters.MediaType == ContextMenuMediaType.Image)
            model.InsertItemAt(0, TranslateImageCommand, "Перевести текст картинки · MBrowser");
    }

    protected override bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser,
        IBrowser browser, IFrame frame, IContextMenuParams parameters,
        CefMenuCommand commandId, CefEventFlags eventFlags)
    {
        if (commandId != TranslateImageCommand) return false;
        var point = new Point(parameters.XCoord, parameters.YCoord);
        owner.DispatchCefUi(() => owner.TranslateCefImageAsync(point));
        return true;
    }
}
