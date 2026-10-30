using CefSharp;
using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    internal async Task TranslateCefImageAsync(Point point)
    {
        if (_imageTranslationRunning) return;
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) || runtime is null) return;
        if (!CefIsAlive(runtime) || !runtime.View.CanExecuteJavascriptInMainFrame) return;
        _imageTranslationRunning = true;
        var view = runtime.View;
        string url = view.Address ?? "";
        string token = Guid.NewGuid().ToString("N");
        try
        {
            string probe = """
                (()=>{
                  window.__mbrowserImageTranslation?.close();
                  const node=document.elementFromPoint(POINTX*innerWidth/VIEWWIDTH,POINTY*innerHeight/VIEWHEIGHT);
                  const image=node instanceof HTMLImageElement?node:node?.closest('img');
                  if(!image||!image.complete||!image.naturalWidth)throw Error('Картинка не загрузилась.');
                  image.dataset.mbrowserOcr=TOKEN;
                  const r=image.getBoundingClientRect(), left=Math.max(0,r.left),top=Math.max(0,r.top);
                  const width=Math.min(innerWidth,r.right)-left,height=Math.min(innerHeight,r.bottom)-top;
                  if(width<=0||height<=0)throw Error('Картинка вне экрана.');
                  return {left,top,width,height,viewportWidth:innerWidth,viewportHeight:innerHeight,
                    x:(left-r.left)/r.width,y:(top-r.top)/r.height,w:width/r.width,h:height/r.height,src:image.currentSrc};
                })()
                """.Replace("POINTX", point.X.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("POINTY", point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("VIEWWIDTH", Math.Max(1, view.ClientSize.Width).ToString())
                .Replace("VIEWHEIGHT", Math.Max(1, view.ClientSize.Height).ToString())
                .Replace("TOKEN", JsonSerializer.Serialize(token));
            var geometry = await view.EvaluateScriptAsync(probe);
            if (!geometry.Success || geometry.Result is null)
                throw new InvalidOperationException("Не удалось определить границы изображения.");
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(geometry.Result));
            var crop = doc.RootElement;
            using var devtools = view.GetDevToolsClient();
            var response = await devtools.Page.CaptureScreenshotAsync();
            if (response.Data is null) throw new InvalidOperationException("CEF не получил кадр.");
            using var stream = new MemoryStream(response.Data);
            using var screenshot = new Bitmap(stream);
            double sx = screenshot.Width / crop.GetProperty("viewportWidth").GetDouble();
            double sy = screenshot.Height / crop.GetProperty("viewportHeight").GetDouble();
            double left = crop.GetProperty("left").GetDouble(), top = crop.GetProperty("top").GetDouble();
            double width = crop.GetProperty("width").GetDouble(), height = crop.GetProperty("height").GetDouble();
            var bounds = Rectangle.Intersect(new Rectangle(0, 0, screenshot.Width, screenshot.Height),
                Rectangle.FromLTRB((int)(left * sx), (int)(top * sy),
                    (int)Math.Ceiling((left + width) * sx), (int)Math.Ceiling((top + height) * sy)));
            if (bounds.Width < 2 || bounds.Height < 2) throw new InvalidOperationException("Изображение слишком маленькое.");
            using var image = screenshot.Clone(bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            await ShowCefImageTextAsync(view, token, crop, [], "Распознавание и перевод…");
            var result = await ImageText.TranslateAsync(image, _preferences.ImageOcrLanguage, _preferences.ImageTargetLanguage);
            if (!CefIsAlive(runtime) || runtime.Tab != ActiveTab || view.Address != url) return;
            await ShowCefImageTextAsync(view, token, crop, result.Lines ?? [],
                result.Lines is { Count: > 0 } ? "Google · оригинал" : "Текст не найден");
        }
        catch (Exception error)
        {
            if (CefIsAlive(runtime) && runtime.Tab == ActiveTab)
            {
                view.ExecuteScriptAsync("window.__mbrowserImageTranslation?.close()");
                ShowMenuPage("Перевод картинки", row => row(error.Message, () => { }));
            }
            if (_smoke) throw;
        }
        finally { _imageTranslationRunning = false; }
    }

    private static async Task ShowCefImageTextAsync(CefSharp.WinForms.ChromiumWebBrowser view,
        string token, JsonElement crop, IReadOnlyList<ImageTextLine> lines, string caption)
    {
        string data = JsonSerializer.Serialize(new { token, crop = JsonSerializer.Deserialize<object>(crop.GetRawText()), lines, caption });
        string script = """
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
              close.onclick=()=>{img.dataset.mbrowserOcrCancelled=data.token;cleanup();};
              addEventListener('scroll',update,true);addEventListener('resize',update);
              window.__mbrowserImageTranslation={close:cleanup};update();
            })(DATA)
            """.Replace("DATA", data);
        await view.EvaluateScriptAsync(script);
    }
}
