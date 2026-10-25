using System.Diagnostics;
using System.Text.Json;

namespace MBrowser;

internal sealed record ImageTextLine(string Text, string TranslatedText, double X, double Y, double Width, double Height);
internal sealed record ImageTextResult(string? Language, List<ImageTextLine>? Lines, string? Error);
internal sealed record OcrLanguage(string LanguageTag, string DisplayName);

/// <summary>Starts Windows OCR only for a user request. Screenshots and OCR output are temporary.</summary>
internal static class ImageText
{
    private static readonly HashSet<Process> ActiveProcesses = [];
    private static readonly HashSet<string> TemporaryFolders = [];
    internal static void StopAll()
    {
        foreach (var process in ActiveProcesses.ToArray())
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(1000); } }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        foreach (string folder in TemporaryFolders.ToArray())
        { try { DeleteFolder(folder); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
    private static void DeleteFolder(string folder)
    {
        string expectedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "MBrowser-OCR-";
        if (Path.GetFullPath(folder).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder)) Directory.Delete(folder, true);
    }
    private static string Helper
    {
        get
        {
            string published = Path.Combine(AppContext.BaseDirectory, "Features", "ImageOcr", "MBrowser.Ocr.exe");
            if (File.Exists(published)) return published;
            string development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Engine", "ImageOcr", "bin", "Release", "net10.0-windows10.0.26100.0", "MBrowser.Ocr.exe"));
            return File.Exists(development) ? development : throw new FileNotFoundException("Не найден модуль OCR. Запустите полную сборку через build.ps1.");
        }
    }
    private static async Task<string> RunAsync(string[] arguments, string output)
    {
        var start = new ProcessStartInfo(Helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить OCR.");
        ActiveProcesses.Add(process);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw new TimeoutException("Время ожидания перевода истекло."); }
            if (!File.Exists(output) || new FileInfo(output).Length > 1024 * 1024) throw new IOException("Модуль OCR не вернул результат.");
            string result = await File.ReadAllTextAsync(output);
            if (process.ExitCode != 0)
            {
                using var json = JsonDocument.Parse(result);
                throw new IOException(json.RootElement.TryGetProperty("Error", out var error) ? error.GetString() : "Не удалось распознать изображение.");
            }
            return result;
        }
        finally { ActiveProcesses.Remove(process); }
    }
    internal static async Task<ImageTextResult> TranslateAsync(Bitmap image, string language, string target)
    {
        string folder = Path.Combine(Path.GetTempPath(), "MBrowser-OCR-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        TemporaryFolders.Add(folder);
        try
        {
            string input = Path.Combine(folder, "image.png"), output = Path.Combine(folder, "result.json");
            image.Save(input, System.Drawing.Imaging.ImageFormat.Png);
            string json = await RunAsync([input, output, language, target], output);
            return JsonSerializer.Deserialize<ImageTextResult>(json) ?? throw new InvalidDataException("Пустой результат OCR.");
        }
        finally
        {
            TemporaryFolders.Remove(folder);
            DeleteFolder(folder);
        }
    }
    internal static async Task<OcrLanguage[]> LanguagesAsync()
    {
        string output = Path.Combine(Path.GetTempPath(), "MBrowser-OCR-Languages-" + Guid.NewGuid().ToString("N") + ".json");
        try { return JsonSerializer.Deserialize<OcrLanguage[]>(await RunAsync(["--languages", output], output)) ?? []; }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
