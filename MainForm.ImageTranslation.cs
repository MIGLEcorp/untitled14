using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MBrowser;

internal sealed partial class MainForm
{
    private bool _imageTranslationRunning;
    private void ConfigureImageTranslation(TabRuntime runtime)
    {
        runtime.View.CoreWebView2.ContextMenuRequested += (_, e) =>
        {
            if (e.ContextMenuTarget.Kind != CoreWebView2ContextMenuTargetKind.Image || !IsAlive(runtime)) return;
            Point point = e.Location;
            var command = _environment!.CreateContextMenuItem("Перевести текст картинки · MBrowser", null, CoreWebView2ContextMenuItemKind.Command);
            command.CustomItemSelected += (_, _) => RunUi(() => TranslateImageAtAsync(runtime, point));
            e.MenuItems.Insert(0, command);
        };
    }
    internal Task TranslateImageForTestAsync(Point point) => TranslateImageAtAsync(ActiveRuntime!, point);
    private async Task TranslateImageAtAsync(TabRuntime runtime, Point point)
    {
        if (_imageTranslationRunning || !IsAlive(runtime) || runtime != ActiveRuntime) return;
        _imageTranslationRunning = true;
        var core = runtime.View.CoreWebView2;
        string url = core.Source, token = Guid.NewGuid().ToString("N");
        try
        {
            string probe = """
                (()=>{
                  window.__mbrowserImageTranslation?.close();
                  const node=document.elementFromPoint(POINTX*innerWidth/VIEWWIDTH,POINTY*innerHeight/VIEWHEIGHT);
                  const image=node instanceof HTMLImageElement?node:node?.closest('img');
                  if(!image||!image.complete||!image.naturalWidth)throw Error('Картинка ещё не загрузилась.');
                  image.dataset.mbrowserOcr=TOKEN;
                  const r=image.getBoundingClientRect(), left=Math.max(0,r.left),top=Math.max(0,r.top);
                  const width=Math.min(innerWidth,r.right)-left,height=Math.min(innerHeight,r.bottom)-top;
                  if(width<=0||height<=0)throw Error('Картинка вне видимой области.');
                  return {left,top,width,height,viewportWidth:innerWidth,viewportHeight:innerHeight,
                    x:(left-r.left)/r.width,y:(top-r.top)/r.height,w:width/r.width,h:height/r.height,src:image.currentSrc};
                })()
                """.Replace("POINTX", point.X.ToString()).Replace("POINTY", point.Y.ToString())
                .Replace("VIEWWIDTH", runtime.View.ClientSize.Width.ToString()).Replace("VIEWHEIGHT", runtime.View.ClientSize.Height.ToString()).Replace("TOKEN", JsonSerializer.Serialize(token));
            using var geometry = JsonDocument.Parse(await core.ExecuteScriptAsync(probe));
            if (geometry.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Не удалось определить границы картинки.");
            var g = geometry.RootElement;
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); stream.Position = 0;
            using var screenshot = new Bitmap(stream);
            double sx = screenshot.Width / g.GetProperty("viewportWidth").GetDouble(), sy = screenshot.Height / g.GetProperty("viewportHeight").GetDouble();
            var bounds = Rectangle.Intersect(new Rectangle(0, 0, screenshot.Width, screenshot.Height), Rectangle.FromLTRB(
                (int)(g.GetProperty("left").GetDouble() * sx), (int)(g.GetProperty("top").GetDouble() * sy),
                (int)Math.Ceiling((g.GetProperty("left").GetDouble() + g.GetProperty("width").GetDouble()) * sx),
                (int)Math.Ceiling((g.GetProperty("top").GetDouble() + g.GetProperty("height").GetDouble()) * sy)));
            if (bounds.Width < 2 || bounds.Height < 2) throw new InvalidOperationException("Картинка слишком маленькая.");
            using var image = screenshot.Clone(bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            await ShowImageTextAsync(core, token, g, [], "Распознавание и перевод…");
            var result = await ImageText.TranslateAsync(image, _preferences.ImageOcrLanguage, _preferences.ImageTargetLanguage);
            if (!IsAlive(runtime) || runtime != ActiveRuntime || core.Source != url) return;
            var lines = result.Lines ?? [];
            await ShowImageTextAsync(core, token, g, lines, lines.Count == 0 ? "Текст не найден" : "Google · оригинал");
        }
        catch (Exception error)
        {
            if (IsAlive(runtime) && runtime == ActiveRuntime && core.Source == url)
            {
                await core.ExecuteScriptAsync("window.__mbrowserImageTranslation?.close()");
                ShowMenuPage("Перевод картинки", row => row(error.Message, () => { }));
            }
            if (_smoke) throw;
        }
        finally { _imageTranslationRunning = false; }
    }
    private static async Task ShowImageTextAsync(CoreWebView2 core, string token, JsonElement crop, IReadOnlyList<ImageTextLine> lines, string caption)
    {
        string data = JsonSerializer.Serialize(new { token, crop = JsonSerializer.Deserialize<object>(crop.GetRawText()), lines, caption });
        await core.ExecuteScriptAsync("""
            ((data)=>{
              window.__mbrowserImageTranslation?.close();
              const img=document.querySelector('img[data-mbrowser-ocr="'+data.token+'"]');
              if(!img||img.currentSrc!==data.crop.src||img.dataset.mbrowserOcrCancelled===data.token)return;
              const host=document.createElement('div');host.id='mbrowser-image-translation';
              host.style.cssText='all:initial;position:fixed;pointer-events:none;z-index:2147483646';
              (document.fullscreenElement||document.body).append(host);
              const shadow=host.attachShadow({mode:'open'}),canvas=document.createElement('canvas'),ctx=canvas.getContext('2d');
              const boxes=data.lines.map(line=>{const box=document.createElement('div');box.textContent=line.TranslatedText;
                box.style.cssText='position:absolute;box-sizing:border-box;display:flex;align-items:center;justify-content:center;color:#17171b;background:white;border-radius:4px;padding:1px;pointer-events:auto;user-select:text;overflow:hidden;font-family:Arial,sans-serif;line-height:1.05';
                shadow.append(box);return {line,box};});
              const close=document.createElement('button');close.textContent=data.caption+'  ×';close.title='Показать оригинал';
              close.style.cssText='position:absolute;right:5px;top:5px;border:0;border-radius:12px;padding:7px 11px;background:#24202f;color:white;font:12px Arial;cursor:pointer;pointer-events:auto';shadow.append(close);
              const update=()=>{if(!img.isConnected){cleanup();return;}const r=img.getBoundingClientRect();
                host.style.left=r.left+'px';host.style.top=r.top+'px';host.style.width=r.width+'px';host.style.height=r.height+'px';
                for(const {line,box} of boxes){const x=(data.crop.x+line.X*data.crop.w)*r.width,y=(data.crop.y+line.Y*data.crop.h)*r.height;
                  const w=line.Width*data.crop.w*r.width,h=line.Height*data.crop.h*r.height;
                  let font=Math.max(9,Math.min(48,h*.9));ctx.font=font+'px Arial';const measured=ctx.measureText(line.TranslatedText).width;
                  if(measured>w-4)font=Math.max(8,font*(w-4)/measured);
                  Object.assign(box.style,{left:(x-3)+'px',top:(y-3)+'px',width:Math.max(8,w+6)+'px',height:Math.max(h+6,14)+'px',fontSize:font+'px'});
                }};
              const observer=new ResizeObserver(update);observer.observe(img);
              const cleanup=()=>{observer.disconnect();removeEventListener('scroll',update,true);removeEventListener('resize',update);host.remove();if(window.__mbrowserImageTranslation?.close===cleanup)delete window.__mbrowserImageTranslation;};
              close.onclick=()=>{img.dataset.mbrowserOcrCancelled=data.token;cleanup();};addEventListener('scroll',update,true);addEventListener('resize',update);window.__mbrowserImageTranslation={close:cleanup};update();
            })(DATA)
            """.Replace("DATA", data));
    }
    private async Task ShowImageTranslationSettingsAsync()
    {
        var languages = await ImageText.LanguagesAsync();
        ShowMenuPage("Язык текста на картинке", row =>
        {
            row("Язык системы", () => { _preferences.ImageOcrLanguage = ""; _preferences.Save(DataPath); });
            foreach (var language in languages) row(language.DisplayName, () => { _preferences.ImageOcrLanguage = language.LanguageTag; _preferences.Save(DataPath); });
            row("Язык перевода  ›", () => ShowMenuPage("Переводить картинки на…", add =>
            {
                foreach (var (code, name) in new[] { ("ru", "Русский"), ("en", "English"), ("ja", "日本語"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"), ("zh-CN", "中文") })
                    add(name, () => { _preferences.ImageTargetLanguage = code; _preferences.Save(DataPath); });
            }));
        });
    }
}
