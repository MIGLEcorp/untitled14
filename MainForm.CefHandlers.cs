using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CefSharp;
using CefSharp.Handler;

namespace MBrowser;

/// <summary>
/// Records only slow/failed resource timings. URL paths, query strings, headers,
/// cookies and bodies are deliberately excluded from diagnostics.
/// </summary>
internal sealed class CefResourceTimingHandler(string dataPath, Guid tabId) : ResourceRequestHandler
{
    private readonly string _logPath = Path.Combine(dataPath, "cef-resource-timings.log");
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<ulong, ResourceStart> _started = new();
    private readonly ConcurrentDictionary<ulong, byte> _pendingReported = new();

    private readonly record struct ResourceStart(long Timestamp, string Host, string Type, bool MainFrame);

    protected override CefReturnValue OnBeforeResourceLoad(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IRequestCallback callback)
    {
        if (request.Identifier != 0)
        {
            string host = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                ? uri.Host : "unknown";
            _started[request.Identifier] = new ResourceStart(
                Stopwatch.GetTimestamp(), host, request.ResourceType.ToString(), frame?.IsMain == true);
        }
        return CefReturnValue.Continue;
    }

    protected override void OnResourceLoadComplete(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IResponse response, UrlRequestStatus status, long receivedContentLength)
    {
        if (request.Identifier == 0 || !_started.TryRemove(request.Identifier, out var start))
            return;

        _pendingReported.TryRemove(request.Identifier, out _);
        double elapsedMs = Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds;
        int statusCode = response?.StatusCode ?? 0;
        bool failed = status != UrlRequestStatus.Success || statusCode >= 400;
        if (!failed && elapsedMs < 750) return;

        QueueWrite(FormatLine(start, status.ToString(), statusCode, elapsedMs, receivedContentLength));
    }

    internal void LogPendingResources()
    {
        foreach (var pair in _started)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(pair.Value.Timestamp).TotalMilliseconds;
            if (elapsedMs < 4000 || !_pendingReported.TryAdd(pair.Key, 0)) continue;
            QueueWrite(FormatLine(pair.Value, "PENDING", 0, elapsedMs, 0));
        }
    }

    private string FormatLine(ResourceStart start, string status, int statusCode,
        double elapsedMs, long bytes) =>
        $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} " +
        $"tab={tabId:N} host={start.Host} type={start.Type} main_frame={start.MainFrame} " +
        $"status={status} http={statusCode} elapsed_ms={elapsedMs:F0} bytes={bytes}";

    private void QueueWrite(string line)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                lock (_writeGate)
                {
                    if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 4 * 1024 * 1024)
                        File.Move(_logPath, _logPath + ".1", overwrite: true);
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        });
    }
}

/// <summary>Keep untrusted page navigation inside the browser's HTTP/HTTPS and explicitly opened file policy.</summary>
internal sealed class CefNavigationHandler : RequestHandler
{
    private readonly MainForm _owner;
    private readonly BrowserTab _tab;
    // Per-resource managed callbacks are expensive on script-heavy SPAs.
    // Keep detailed tracing opt-in instead of intercepting every request.
    private readonly CefResourceTimingHandler? _resourceTimings;
    internal bool ResourceTracingEnabled => _resourceTimings is not null;

    internal CefNavigationHandler(MainForm owner, BrowserTab tab)
    {
        _owner = owner;
        _tab = tab;
        if (Environment.GetEnvironmentVariable("MBROWSER_TRACE_CEF_RESOURCES") == "1")
            _resourceTimings = new CefResourceTimingHandler(owner.DataPath, tab.Id);
    }

    internal void LogPendingResources() => _resourceTimings?.LogPendingResources();

    protected override IResourceRequestHandler GetResourceRequestHandler(
        IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
        bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling) =>
        _resourceTimings!;

    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain) return false;
        string url = request.Url;
        if (url == "about:blank" || AddressParser.TryWebUrl(url, out _) ||
            BrowserDocuments.IsApproved(url, _tab.LocalDocuments))
            return false;
        _owner.DispatchCefUi(() =>
        {
            _owner.CefNavigationBlocked(url);
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
