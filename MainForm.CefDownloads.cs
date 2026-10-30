using CefSharp;
using CefSharp.Handler;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MBrowser;

/// <summary>CEF native downloads: explicit user destination, no silent untrusted paths.</summary>
internal sealed class CefDownloadsHandler(MainForm owner, Guid tabId) : DownloadHandler
{
    protected override bool OnBeforeDownload(IWebBrowser webBrowser, IBrowser browser,
        DownloadItem item, IBeforeDownloadCallback callback)
    {
        var suggested = Path.GetFileName(item.SuggestedFileName);
        if (string.IsNullOrWhiteSpace(suggested) || suggested.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            suggested = "download";
        var url = item.Url;
        int downloadId = item.Id;
        if (!owner.DispatchCefUi(async () =>
        {
            try
            {
                if (callback.IsDisposed) return;
                using var picker = new SaveFileDialog
                {
                    Title = "Сохранить загрузку · MBrowser",
                    FileName = suggested,
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    OverwritePrompt = true,
                    AddExtension = false
                };
                if (picker.ShowDialog(owner) == DialogResult.OK && !callback.IsDisposed)
                {
                    var folder = Path.GetDirectoryName(picker.FileName)!;
                    Directory.CreateDirectory(folder);
                    callback.Continue(picker.FileName, false);
                    owner.CefDownloadStarted(tabId, downloadId, suggested, picker.FileName, url);
                }
            }
            catch (Exception error) { owner.CefDownloadError(error); }
            finally { callback.Dispose(); }
            await Task.CompletedTask;
        })) callback.Dispose();
        return true;
    }

    protected override void OnDownloadUpdated(IWebBrowser webBrowser, IBrowser browser,
        DownloadItem item, IDownloadItemCallback callback)
    {
        int id = item.Id;
        bool complete = item.IsComplete, cancelled = item.IsCancelled, interrupted = item.IsInterrupted;
        long received = item.ReceivedBytes, total = item.TotalBytes;
        owner.DispatchCefUi(() =>
        {
            owner.CefDownloadProgress(tabId, id, received, total, complete, cancelled, interrupted);
            return Task.CompletedTask;
        });
    }
}

internal sealed partial class MainForm
{
    private sealed record CefDownloadInfo(Guid TabId, int Id, string Name, string Destination, string Url,
        long Received, long Total, string Status);

    private readonly Dictionary<(Guid TabId, int Id), CefDownloadInfo> _cefDownloadList = [];
    internal int CefDownloadCountForTest => _cefDownloadList.Count;

    internal bool DispatchCefUi(Func<Task> action)
    {
        try
        {
            if (_closing || !IsHandleCreated || IsDisposed) return false;
            BeginInvoke((Action)(() => RunUi(action)));
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    internal void CefDownloadStarted(Guid tabId, int id, string name, string destination, string url)
    {
        _cefDownloadList[(tabId, id)] = new(tabId, id, name, destination, url, 0, 0, "Загрузка");
        _status.Text = "CEF: загружается " + name;
    }

    internal void CefDownloadProgress(Guid tabId, int id, long received, long total, bool complete, bool cancelled, bool interrupted)
    {
        if (!_cefDownloadList.TryGetValue((tabId, id), out var item)) return;
        string state = complete ? "Готово" : cancelled ? "Отменено" : interrupted ? "Ошибка" : "Загрузка";
        _cefDownloadList[(tabId, id)] = item with { Received = received, Total = total, Status = state };
        if (complete && !string.IsNullOrWhiteSpace(item.Destination))
            _status.Text = "Сохранено: " + item.Name;
    }

    internal void CefDownloadError(Exception error)
    {
        Program.LogError(DataPath, error);
        if (!_closing) _status.Text = "Ошибка загрузки: " + error.Message;
    }

    private void ShowCefDownloads()
    {
        ShowMenuPage("Загрузки Chromium CEF", row =>
        {
            var downloads = _cefDownloadList.Values.OrderByDescending(x => x.Id).Take(24).ToArray();
            if (downloads.Length == 0) row("Нет загрузок в текущей сессии", () => { });
            foreach (var item in downloads)
            {
                string progress = item.Total > 0
                    ? " · " + Math.Min(100, (int)(100.0 * item.Received / item.Total)) + "%"
                    : "";
                row(item.Name + " · " + item.Status + progress, () =>
                {
                    if (item.Status == "Готово" && File.Exists(item.Destination))
                        Process.Start(new ProcessStartInfo("explorer.exe",
                            "/select,\"" + item.Destination.Replace("\"", "") + "\"")
                        { UseShellExecute = true });
                    else ShowCefDownloads();
                });
            }
            row("Открыть папку загрузок", () =>
            {
                var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloadsPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", downloadsPath) { UseShellExecute = true });
            });
        });
    }
}
