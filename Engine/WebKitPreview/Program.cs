using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace MBrowser.WebKit;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        PreviewOptions? options = null;
        try
        {
            options = PreviewOptions.Parse(args);
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", options.BrowsersPath);
            if (options.Install)
                return Microsoft.Playwright.Program.Main(["install", "webkit"]);
            if (options.SmokeTest)
                return PreviewSmokeTest.RunAsync(options).GetAwaiter().GetResult();

            // A persistent WebKit profile may have only one owner. Never attach to another browser.
            string profileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                options.ProfilePath.ToUpperInvariant())))[..24];
            using var instance = new Mutex(true, "Local\\FeatherWebKit-" + profileId, out bool first);
            if (!first)
            {
                MessageBox.Show("Экспериментальное окно WebKit уже открыто.", "MBrowser · WebKit");
                return 0;
            }

            using var playwright = Playwright.CreateAsync().GetAwaiter().GetResult();
            if (!File.Exists(playwright.Webkit.ExecutablePath))
            {
                var answer = MessageBox.Show(
                    "Для экспериментального WebKit нужно один раз скачать движок с серверов Microsoft. " +
                    "Потребуется несколько сотен МБ на диске.\n\n" +
                    "Это тестовая сборка Playwright в отдельном окне и профиле. Скачать сейчас?",
                    "MBrowser · установка WebKit", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes)
                    return 0;
                using var progress = new InstallProgressWindow();
                if (progress.ShowDialog() != DialogResult.OK)
                    return 1;
                if (!File.Exists(playwright.Webkit.ExecutablePath))
                    throw new IOException("Установка завершилась, но исполняемый файл WebKit не найден.");
            }

            RunPreviewAsync(playwright, options).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            string message = error.GetBaseException().Message;
            if (options?.ResultPath is { } resultPath)
                WriteResult(resultPath, new { passed = false, error = message });
            else
                MessageBox.Show("Не удалось открыть WebKit.\n\n" + message,
                    "MBrowser · WebKit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static async Task RunPreviewAsync(IPlaywright playwright, PreviewOptions options)
    {
        Directory.CreateDirectory(options.ProfilePath);
        await using var context = await playwright.Webkit.LaunchPersistentContextAsync(options.ProfilePath, new()
        {
            Headless = false,
            ViewportSize = ViewportSize.NoViewport,
            AcceptDownloads = false
        });
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Close += (_, _) => closed.TrySetResult();
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        try
        {
            await page.GotoAsync(options.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        catch (PlaywrightException) when (page.IsClosed)
        {
            return;
        }
        catch (PlaywrightException error)
        {
            // Retain the engine's own error page so the user can inspect a network failure.
            MessageBox.Show("WebKit открыт, но страница не загрузилась.\n\n" + error.Message,
                "MBrowser · WebKit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        await closed.Task;
    }

    internal static void WriteResult(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
