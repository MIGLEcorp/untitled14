using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MBrowser;

internal static class WindowFrameTests
{
    internal static async Task RunAsync(MainForm form, Action<bool,string> check)
    {
        var bounds=form.Bounds;
        var state=form.WindowState;
        var restoreBounds=form.RestoreBounds;
        int chromeHeight=form.ChromeHostForTest.Height;
        try
        {
            form.ShowMainMenu();
            check(form.IsInternalMenuVisible, "Menu is a visible child panel inside the browser");
            check(form.InternalMenuBounds.Left>=0 && form.InternalMenuBounds.Top>=0, "Menu is positioned in the content client area");
            check((GetWindowLongPtr(form.Handle,-16).ToInt64() & 0x00c00000)==0, "Opening the internal menu does not restore a native caption");
            if(form.InternalMenuControlForTest is { } panel)
            {
                panel.Refresh(); await Task.Delay(180);
                using var image=ThemeTests.Capture(panel);
                image.Save("preview-menu.png",System.Drawing.Imaging.ImageFormat.Png);
            }
            form.CloseOverlay();

            await PressAndWaitAsync(form,Keys.F11,()=>form.IsFullScreen,"F11 did not enter fullscreen");
            CheckFullscreenLayout(form,check,"F11 fullscreen");
            form.ShowMainMenu();
            check(form.IsInternalMenuVisible && form.Bounds==Screen.FromControl(form).Bounds, "Internal menu opens while fullscreen fills the monitor");
            check(form.ViewportForTest.ClientRectangle.Contains(form.InternalMenuBounds), "Fullscreen menu remains inside the expanded content area");
            await PressAndWaitAsync(form,Keys.Escape,()=>!form.IsInternalMenuVisible,"Escape did not dismiss the fullscreen menu");
            check(form.IsFullScreen,"Dismissing a menu with Escape preserves fullscreen");
            await PressAndWaitAsync(form,Keys.Escape,()=>!form.IsFullScreen,"Escape did not leave fullscreen");
            check(form.Bounds==bounds && form.WindowState==state,"Fullscreen restores the previous window bounds and state");
            check(form.ChromeHostForTest.Visible && form.ChromeHostForTest.Height==chromeHeight && form.AddressInputForTest.Visible && form.BookmarksVisibleForTest,
                "Leaving fullscreen restores the full home header including its bookmarks row");

            await PressAndWaitAsync(form,Keys.F11,()=>form.IsFullScreen,"F11 did not re-enter fullscreen");
            await PressAndWaitAsync(form,Keys.Control|Keys.L,()=>!form.IsFullScreen && form.AddressInputForTest.Focused,"Ctrl+L did not reveal and focus the address bar");
            check(form.ChromeHostForTest.Visible && form.ChromeHostForTest.Height==chromeHeight && form.BookmarksVisibleForTest,
                "Ctrl+L leaves fullscreen and restores the home header before focusing its address field");

            form.WindowState=FormWindowState.Maximized;
            await Task.Delay(100);
            var maximizedBounds=form.Bounds;
            await PressAndWaitAsync(form,Keys.F11,()=>form.IsFullScreen,"F11 did not enter fullscreen from maximized");
            CheckFullscreenLayout(form,check,"Fullscreen from a maximized window");
            await PressAndWaitAsync(form,Keys.F11,()=>!form.IsFullScreen && form.WindowState==FormWindowState.Maximized,"F11 did not restore maximized state");
            check(form.Bounds==maximizedBounds && form.ChromeHostForTest.Visible && form.ChromeHostForTest.Height==chromeHeight,
                "Leaving fullscreen restores a maximized window and its complete header");
        }
        finally
        {
            form.CloseOverlay();
            if(form.IsFullScreen) form.ToggleFullScreen();
            form.WindowState=FormWindowState.Normal;
            form.Bounds=state==FormWindowState.Normal ? bounds : restoreBounds;
            form.WindowState=state;
        }
    }

    internal static async Task RunSiteFullscreenAsync(MainForm form,Action<bool,string> check)
    {
        var core=form.ActiveCore ?? throw new InvalidOperationException("Site fullscreen needs a live test page.");
        var bounds=form.Bounds;
        int chromeHeight=form.ChromeHostForTest.Height;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled","{\"enabled\":true}");
            await RequestSiteFullscreenAsync(core);
            await SmokeTests.WaitUntilAsync(()=>core.ContainsFullScreenElement && form.IsFullScreen,"The DOM fullscreen request did not enter host fullscreen");
            await Task.Delay(100);
            CheckFullscreenLayout(form,check,"Site-requested fullscreen");
            check(form.ActiveWebViewBoundsForTest==form.ViewportForTest.RectangleToScreen(form.ViewportForTest.ClientRectangle),
                "A real fullscreen page fills the entire viewport without an address strip");
            await core.ExecuteScriptAsync("document.exitFullscreen?.()");
            await SmokeTests.WaitUntilAsync(()=>!core.ContainsFullScreenElement && !form.IsFullScreen,"Leaving DOM fullscreen did not restore the host window");
            await Task.Delay(100);
            check(form.Bounds==bounds && form.ChromeHostForTest.Visible && form.ChromeHostForTest.Height==chromeHeight && form.AddressInputForTest.Visible && !form.BookmarksVisibleForTest,
                "Leaving site fullscreen restores the website header without exposing home-only bookmarks");

            await PressAndWaitAsync(form,Keys.F11,()=>form.IsFullScreen,"F11 did not enter manual fullscreen before the site test");
            await RequestSiteFullscreenAsync(core);
            await SmokeTests.WaitUntilAsync(()=>core.ContainsFullScreenElement,"The site did not enter fullscreen inside manual fullscreen");
            await core.ExecuteScriptAsync("document.exitFullscreen?.()");
            await SmokeTests.WaitUntilAsync(()=>!core.ContainsFullScreenElement,"The site did not leave its nested fullscreen element");
            await Task.Delay(100);
            check(form.IsFullScreen,"Leaving site fullscreen preserves a previously entered F11 fullscreen mode");
            CheckFullscreenLayout(form,check,"Manual fullscreen after the site exits fullscreen");
            await PressAndWaitAsync(form,Keys.F11,()=>!form.IsFullScreen,"F11 did not leave the preserved manual fullscreen mode");
            check(form.Bounds==bounds && form.ChromeHostForTest.Visible && form.ChromeHostForTest.Height==chromeHeight && !form.BookmarksVisibleForTest,
                "F11 restores the original website layout after nested site fullscreen");
        }
        finally
        {
            try { if(core.ContainsFullScreenElement) await core.ExecuteScriptAsync("document.exitFullscreen?.()"); }
            finally
            {
                if(form.IsFullScreen) form.ToggleFullScreen();
                form.CloseOverlay();
                await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled","{\"enabled\":false}");
            }
        }
    }

    private static void CheckFullscreenLayout(MainForm form,Action<bool,string> check,string mode)
    {
        check(!form.ChromeHostForTest.Visible && form.ChromeHostForTest.Height==0 && !form.AddressInputForTest.Visible,
            mode+" hides the whole header and removes its reserved height");
        check(form.ViewportForTest.Bounds==form.ClientRectangle,
            mode+" gives the content the entire client area");
    }

    private static async Task PressAndWaitAsync(MainForm form,Keys keys,Func<bool> condition,string message)
    {
        var command=typeof(MainForm).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var nativeMessage=Message.Create(form.Handle,0x0100,(nint)(int)(keys&Keys.KeyCode),0);
        if(command.Invoke(form,[nativeMessage,keys]) is not true) throw new InvalidOperationException("Shortcut was not handled: "+keys);
        await SmokeTests.WaitUntilAsync(condition,message);
        await Task.Delay(100);
    }

    private static async Task RequestSiteFullscreenAsync(CoreWebView2 core)
    {
        string parameters=JsonSerializer.Serialize(new { expression="document.documentElement.requestFullscreen({navigationUI:'hide'})",userGesture=true,awaitPromise=true,returnByValue=true });
        using var response=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",parameters).WaitAsync(TimeSpan.FromSeconds(10)));
        if(response.RootElement.TryGetProperty("exceptionDetails",out var error)) throw new InvalidOperationException("The test page rejected fullscreen: "+error.GetRawText());
    }

    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window,int index);
}
