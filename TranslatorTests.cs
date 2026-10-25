using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MBrowser;

/// <summary>Opt-in online test. Normal smoke tests never send text to a translation provider.</summary>
internal static class TranslatorTests
{
    internal static async Task RunAsync(MainForm form, string profile, string archive, Action<bool, string> check)
    {
        await form.InstallTranslatorForTestAsync(archive).WaitAsync(TimeSpan.FromSeconds(40));
        var core = form.ActiveCore!;
        await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
        var extension = (await core.Profile.GetBrowserExtensionsAsync()).Single(item => item.Id == TranslatorPackage.ExpectedId);
        check(extension.IsEnabled, "The official Immersive Translate extension installs and enables in WebView2");
        await form.ShowTranslatorPageAsync("options.html");
        await SmokeTests.WaitUntilAsync(() => form.TranslatorCoreForTest?.Source.EndsWith("/options.html") == true, "Translator settings did not navigate");
        await Task.Delay(2000);
        check((await form.TranslatorCoreForTest!.ExecuteScriptAsync("document.body.innerText")).Length > 300, "The translator settings render inside the browser window");
        form.CloseOverlay();
        string folder = Path.Combine(profile, "translator-fixture"); Directory.CreateDirectory(folder);
        using (var image = new Bitmap(620, 180))
        {
            using var graphics = Graphics.FromImage(image); graphics.Clear(Color.White);
            using var font = new Font("Arial", 44, FontStyle.Bold); graphics.DrawString("HELLO WORLD", font, Brushes.Black, 20, 45);
            image.Save(Path.Combine(folder, "words.png"));
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), """
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Image translation fixture</title>
            <body style="margin:24px;min-height:1600px;font:20px Arial;background:white;color:black"><h1>Image translation</h1>
            <img id="sample" width="620" height="180" src="words.png"></body></html>
            """);
        core.SetVirtualHostNameToFolderMapping("mbrowser-translator.test", folder, CoreWebView2HostResourceAccessKind.Allow);
        core.Navigate("https://mbrowser-translator.test/index.html");
        await SmokeTests.WaitUntilAsync(() => core.DocumentTitle == "Image translation fixture" && !form.IsPageLoading, "Image fixture did not load");
        await Task.Delay(600);
        using var point = JsonDocument.Parse(await core.ExecuteScriptAsync("(()=>{let r=document.getElementById('sample').getBoundingClientRect();return {x:r.x+100,y:r.y+80}})()"));
        var p = point.RootElement;
        string args = JsonSerializer.Serialize(new { type = "mousePressed", button = "right", clickCount = 1, x = p.GetProperty("x").GetDouble(), y = p.GetProperty("y").GetDouble() });
        bool found = false;
        void Menu(object? _, CoreWebView2ContextMenuRequestedEventArgs e)
        { found = e.MenuItems.Any(item => item.Label == "Перевести текст картинки · MBrowser"); e.Handled = true; }
        core.ContextMenuRequested += Menu;
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", args);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", args.Replace("mousePressed", "mouseReleased"));
        await Task.Delay(200); core.ContextMenuRequested -= Menu;
        check(found, "The built-in image translation command appears in the real image context menu");
        using var scaled = JsonDocument.Parse(await core.ExecuteScriptAsync("(()=>{let r=document.getElementById('sample').getBoundingClientRect();return {x:r.x+100,y:r.y+80,w:innerWidth,h:innerHeight}})()"));
        var s = scaled.RootElement; Rectangle view = form.ActiveWebViewBoundsForTest;
        await form.TranslateImageForTestAsync(new Point((int)(s.GetProperty("x").GetDouble() * view.Width / s.GetProperty("w").GetDouble()), (int)(s.GetProperty("y").GetDouble() * view.Height / s.GetProperty("h").GetDouble())));
        string text = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.getElementById('mbrowser-image-translation')?.shadowRoot.textContent")) ?? "";
        check(text.Contains("ПРИВЕТ", StringComparison.OrdinalIgnoreCase) && text.Contains("МИР", StringComparison.OrdinalIgnoreCase) && !text.Contains("HELLO WORLD", StringComparison.OrdinalIgnoreCase), "Windows OCR and a real network translation replace HELLO WORLD with Russian text on the image");
        using (var screenshot = ThemeTests.Capture(form)) screenshot.Save(Path.Combine(profile, "image-translation.png"));
        await core.ExecuteScriptAsync("window.scrollTo(0,40);document.getElementById('sample').style.width='500px';document.getElementById('sample').style.height='145px'");
        await Task.Delay(300);
        check(await core.ExecuteScriptAsync("(()=>{let a=document.getElementById('sample').getBoundingClientRect(),b=document.getElementById('mbrowser-image-translation').getBoundingClientRect();return Math.abs(a.left-b.left)<1&&Math.abs(a.top-b.top)<1&&Math.abs(a.width-b.width)<1})()") == "true", "The translated layer follows the image after scrolling and resizing");
        await core.ExecuteScriptAsync("document.getElementById('mbrowser-image-translation').shadowRoot.querySelector('button').click()");
        check(await core.ExecuteScriptAsync("!document.getElementById('mbrowser-image-translation') && document.getElementById('sample').getAttribute('src')==='words.png'") == "true", "Restoring the original removes the translated layer without changing the image source");
    }
}
