using CefSharp;
using CefSharp.Handler;
using System.Text.Json;

namespace MBrowser;

/// <summary>CEF permission callbacks stay alive until the user's explicit UI choice.</summary>
internal sealed class CefPermissionsHandler(MainForm owner, Guid tabId) : PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser,
        ulong promptId, string origin, PermissionRequestType requested, IPermissionPromptCallback callback)
    {
        if (!owner.DispatchCefUi(async () =>
        {
            try
            {
                bool allow = await owner.AskCefPermissionAsync(tabId, origin, requested.ToString());
                callback.Continue(allow ? PermissionRequestResult.Accept : PermissionRequestResult.Deny);
            }
            catch (Exception e) { owner.CefDownloadError(e); callback.Continue(PermissionRequestResult.Deny); }
            finally { callback.Dispose(); }
        })) callback.Dispose();
        return true;
    }

    protected override bool OnRequestMediaAccessPermission(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, string origin, MediaAccessPermissionType requested, IMediaAccessCallback callback)
    {
        if (!owner.DispatchCefUi(async () =>
        {
            try
            {
                bool allow = await owner.AskCefPermissionAsync(tabId, origin, "media:" + requested);
                if (allow) callback.Continue(requested);
                else callback.Cancel();
            }
            catch (Exception error) { owner.CefDownloadError(error); callback.Cancel(); }
            finally { callback.Dispose(); }
        })) callback.Dispose();
        return true;
    }
}

internal sealed partial class MainForm
{
    private readonly Dictionary<string, bool> _cefStoredPermissions = new(StringComparer.Ordinal);
    private bool _cefPermissionsLoaded;
    private string CefPermissionPath => Path.Combine(DataPath, "cef-permissions.json");

    private void LoadCefPermissions()
    {
        if (_cefPermissionsLoaded) return;
        _cefPermissionsLoaded = true;
        try
        {
            if (!File.Exists(CefPermissionPath)) return;
            if (new FileInfo(CefPermissionPath).Length > 1024 * 1024) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(CefPermissionPath));
            if (saved != null)
                foreach (var pair in saved) _cefStoredPermissions[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { Program.LogError(DataPath, ex); }
    }

    internal async Task<bool> AskCefPermissionAsync(Guid tabId, string requestingOrigin, string permissionType)
    {
        if (_closing || !Uri.TryCreate(requestingOrigin, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0) return false;
        string origin = uri.GetLeftPart(UriPartial.Authority);
        if (!IsActiveCefPermissionOrigin(tabId, origin)) return false;
        string key = origin + "|" + permissionType;
        await _permissionQueue.WaitAsync();
        try
        {
            LoadCefPermissions();
            if (!IsActiveCefPermissionOrigin(tabId, origin)) return false;
            if (_cefStoredPermissions.TryGetValue(key, out bool known)) return known;
            if (_closing || IsDisposed) return false;
            bool result = await AskInsideWindowAsync(
                "Разрешение сайта · Chromium CEF",
                origin + "\n\nЗапрос: " + permissionType + "\nРазрешайте только доверенным сайтам.",
                remember: true);
            // Navigation, focus changes or tab closure invalidate prior consent.
            bool originStillActive = IsActiveCefPermissionOrigin(tabId, origin);
            result &= originStillActive;
            if (originStillActive && _permissionRemember?.Checked == true)
            {
                _cefStoredPermissions[key] = result;
                string draft = CefPermissionPath + ".tmp";
                try
                {
                    File.WriteAllText(draft, JsonSerializer.Serialize(_cefStoredPermissions));
                    File.Move(draft, CefPermissionPath, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Program.LogError(DataPath, error); }
            }
            return result;
        }
        finally
        {
            _permissionChoice = null;
            _permissionRemember = null;
            if (ReferenceEquals(_overlay, _permissionPanel)) CloseOverlay();
            _permissionPanel = null;
            _permissionQueue.Release();
        }
    }

    private bool IsActiveCefPermissionOrigin(Guid tabId, string origin)
    {
        if (_closing || !_cefRuntimes.TryGetValue(tabId, out var runtime) ||
            !CefIsAlive(runtime) || runtime.Tab != ActiveTab ||
            !Uri.TryCreate(runtime.View.Address, UriKind.Absolute, out var activeUri))
            return false;
        return activeUri.GetLeftPart(UriPartial.Authority)
            .Equals(origin, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowCefPermissions()
    {
        LoadCefPermissions();
        ShowMenuPage("Разрешения сайтов · CEF", add =>
        {
            if (_cefStoredPermissions.Count == 0)
                add("Нет сохранённых разрешений", () => { });
            foreach (var item in _cefStoredPermissions.Take(30))
            {
                string key = item.Key;
                add(key + " · " + (item.Value ? "разрешено" : "запрещено"), () =>
                {
                    _cefStoredPermissions.Remove(key);
                    try { File.WriteAllText(CefPermissionPath, JsonSerializer.Serialize(_cefStoredPermissions)); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { Program.LogError(DataPath, error); }
                    ShowCefPermissions();
                });
            }
        });
    }
}
