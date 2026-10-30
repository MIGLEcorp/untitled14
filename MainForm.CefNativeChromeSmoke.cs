using CefSharp;
using CefSharp.WinForms;

namespace MBrowser;

internal sealed partial class MainForm
{
    internal async Task<string> CefNativePasswordUiForTestAsync()
    {
        const string url = "chrome://password-manager/";
        using var control = new ChromiumWebBrowser(url) { Dock = DockStyle.Fill };
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        control.LoadingStateChanged += (_, e) =>
        {
            if (!e.IsLoading) loaded.TrySetResult(true);
        };
        control.LoadError += (_, e) =>
        {
            if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted)
                loaded.TrySetResult(false);
        };
        _viewport.Controls.Add(control);
        control.BringToFront();
        try
        {
            var finished = await Task.WhenAny(loaded.Task, Task.Delay(14000));
            if (finished != loaded.Task || !await loaded.Task ||
                !control.IsBrowserInitialized || !control.CanExecuteJavascriptInMainFrame)
                return "NOT_AVAILABLE";
            var response = await control.EvaluateScriptAsync(
                "JSON.stringify({url:location.href,title:document.title,body:document.body?.innerText?.length||0})");
            return response.Success ? response.Result?.ToString() ?? "EMPTY" : "JS_FAILED";
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or ObjectDisposedException)
        { return error.GetType().Name; }
        finally { _viewport.Controls.Remove(control); }
    }
}