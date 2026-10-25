using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using System.Diagnostics;

namespace MBrowser.ImageOcr;

/// <summary>WinRT lives only in this short-lived process, outside the browser's normal working set.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2) return 2;
        string output = Path.GetFullPath(args[1]);
        try
        {
            if (args[0] == "--languages")
            {
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(OcrEngine.AvailableRecognizerLanguages.Select(l => new { l.LanguageTag, l.DisplayName })));
                return 0;
            }
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(args[0]));
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double scale = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale) },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var engine = args.Length > 2 && args[2].Length > 0 ? OcrEngine.TryCreateFromLanguage(new Language(args[2])) : OcrEngine.TryCreateFromUserProfileLanguages();
            engine ??= OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e is not null);
            if (engine is null) throw new InvalidOperationException("В Windows не установлен язык распознавания текста. Добавьте языковой пакет в параметрах Windows.");
            var result = await engine.RecognizeAsync(bitmap);
            var recognized = result.Lines.Where(l => l.Words.Count > 0).Take(80).ToArray();
            string[] translations = recognized.Select(l => l.Text).ToArray();
            if (args.Length > 3 && args[3].Length > 0 && translations.Length > 0)
            {
                // The web service can rate-limit requests; errors are returned to the browser, never hidden.
                for (int start = 0; start < translations.Length; start += 8)
                {
                    string[] batch = translations.Skip(start).Take(8).ToArray();
                    async Task<string> Translate(string text)
                    {
                        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(args[3]) + "&dt=t&q=" + Uri.EscapeDataString(text);
                        string json = await RequestTranslationAsync(url);
                        if (json.Length > 256000) throw new InvalidDataException("Слишком большой ответ сервиса перевода.");
                        using var parsed = JsonDocument.Parse(json);
                        return string.Concat(parsed.RootElement[0].EnumerateArray().Select(segment => segment[0].GetString()));
                    }
                    string translated = await Translate(string.Join('\n', batch));
                    string[] rows = translated.TrimEnd('\r', '\n').Split('\n');
                    for (int i = 0; i < batch.Length; i++) translations[start + i] = rows.Length == batch.Length ? rows[i].TrimEnd('\r') : await Translate(batch[i]);
                }
            }
            var lines = recognized.Select((line, index) =>
            {
                var bounds = line.Words.Select(w => w.BoundingRect).ToArray();
                double x = bounds.Min(b => b.X), y = bounds.Min(b => b.Y);
                return new
                {
                    Text = line.Text,
                    TranslatedText = translations[index],
                    X = x / bitmap.PixelWidth,
                    Y = y / bitmap.PixelHeight,
                    Width = (bounds.Max(b => b.Right) - x) / bitmap.PixelWidth,
                    Height = (bounds.Max(b => b.Bottom) - y) / bitmap.PixelHeight
                };
            }).ToArray();
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Language = engine.RecognizerLanguage.LanguageTag, Lines = lines }));
            return 0;
        }
        catch (Exception error)
        { await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Error = error.Message })); return 1; }
    }
    private static async Task<string> RequestTranslationAsync(string url)
    {
        // Windows ships curl. Keep network/TLS code and OCR outside the persistent browser process.
        string curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
        if (!File.Exists(curl)) throw new FileNotFoundException("В Windows не найден curl.exe для подключения к сервису перевода.");
        var start = new ProcessStartInfo(curl)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (string arg in new[] { "--silent", "--show-error", "--fail", "--max-time", "15", "--max-filesize", "262144", "--proto", "=https", "--user-agent", "MBrowser/4.2.0", "--config", "-" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось подключиться к переводу.");
        // The OCR text travels over stdin, rather than appearing in the child process command line.
        await process.StandardInput.WriteLineAsync("url = \"" + url + "\""); process.StandardInput.Close();
        Task<string> body = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw new TimeoutException("Сервис перевода не ответил вовремя."); }
        if (process.ExitCode != 0) throw new IOException("Сервис перевода недоступен: " + await error);
        return await body;
    }
}
