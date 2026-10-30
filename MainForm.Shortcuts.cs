using CefSharp;

namespace MBrowser;

internal sealed partial class MainForm
{
    private void FocusAddress()
    {
        if (IsFullScreen)
        {
            RunUi(async () => { await ExitFullscreenAsync(); _address.Focus(); _address.SelectAll(); });
            return;
        }
        _address.Focus();
        _address.SelectAll();
    }

    private void ShowDownloads() => ShowCefDownloads();

    private bool HandleShortcut(Keys keys)
    {
        Action? action = keys switch
        {
            Keys.F10 => ShowMainMenu,
            Keys.Escape when _overlay is not null => CloseOverlay,
            Keys.Control | Keys.L => FocusAddress,
            Keys.Alt | Keys.D => FocusAddress,
            Keys.Control | Keys.T => () => RunUi(() => OpenTabAsync()),
            Keys.Control | Keys.O => OpenDocument,
            Keys.Control | Keys.W => () => RunUi(() => CloseTabAsync(_session.SelectedIndex)),
            Keys.Control | Keys.Tab => () => RunUi(() => SelectTabAsync((_session.SelectedIndex + 1) % TabCount)),
            Keys.Control | Keys.Shift | Keys.Tab => () => RunUi(() => SelectTabAsync((_session.SelectedIndex + TabCount - 1) % TabCount)),
            Keys.Alt | Keys.Left => () => RunUi(GoBackAsync),
            Keys.Alt | Keys.Right => () => RunUi(GoForwardAsync),
            Keys.Alt | Keys.Home => () => RunUi(HomeAsync),
            Keys.F5 => () => RunUi(ReloadAsync),
            Keys.Control | Keys.R => () => RunUi(ReloadAsync),
            Keys.Control | Keys.J => ShowDownloads,
            Keys.Control | Keys.D => ShowBookmarks,
            Keys.Control | Keys.Shift | Keys.B => ShowBookmarks,
            Keys.Control | Keys.Shift | Keys.P => ShowPasswords,
            Keys.Control | Keys.Shift | Keys.L => () => RunUi(FillPasswordAsync),
            Keys.F11 => () => { if (IsFullScreen) RunUi(ExitFullscreenAsync); else ToggleFullScreen(); },
            Keys.Escape when IsFullScreen => () => RunUi(ExitFullscreenAsync),
            _ => null
        };
        if (action is null) return false;
        BeginInvoke(action);
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    private bool _siteEnteredFullscreen;
    private void SetSiteFullscreen(bool requested)
    {
        if (requested && !IsFullScreen)
        { _siteEnteredFullscreen = true; ToggleFullScreen(); }
        else if (!requested && _siteEnteredFullscreen)
        {
            _siteEnteredFullscreen = false;
            if (IsFullScreen) ToggleFullScreen();
        }
    }

    private async Task ExitFullscreenAsync()
    {
        if (_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) &&
            CefIsAlive(runtime) && runtime.View.CanExecuteJavascriptInMainFrame)
        {
            try { await runtime.View.EvaluateScriptAsync("if(document.fullscreenElement)document.exitFullscreen()"); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
            { Program.LogError(DataPath, error); }
        }
        _siteEnteredFullscreen = false;
        if (IsFullScreen) ToggleFullScreen();
    }

    private async Task LeaveSiteFullscreenAsync()
    {
        if (!_siteEnteredFullscreen) return;
        await ExitFullscreenAsync();
    }
}
