using Microsoft.Playwright;

namespace Feather.WebKit;

internal static class PreviewSmokeTest
{
    internal static async Task<int> RunAsync(PreviewOptions options)
    {
        // No public websites or existing profile are needed for the actual engine check.
        foreach (string invalid in new[] { "file:///c:/Windows/win.ini", "javascript:alert(1)", "https://user:secret@example.com/" })
        {
            try { PreviewOptions.ValidateUrl(invalid); throw new InvalidOperationException("Unsafe URL accepted."); }
            catch (ArgumentException) { }
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Webkit.LaunchAsync(new() { Headless = !options.Headed });
        await using var context = await browser.NewContextAsync();
        await context.RouteAsync("https://feather-webkit.test/**", async route => await route.FulfillAsync(new()
        {
            ContentType = "text/html",
            Body = "<!doctype html><meta charset='utf-8'><title>Feather WebKit smoke</title>" +
                   "<h1 id='result'>WebKit</h1><script>document.querySelector('#result').textContent += ' JavaScript';</script>"
        }));
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://feather-webkit.test/", new() { WaitUntil = WaitUntilState.Load });
        string text = await page.Locator("#result").InnerTextAsync();
        string userAgent = await page.EvaluateAsync<string>("navigator.userAgent");
        await context.AddCookiesAsync([new Cookie { Name = "feather-test", Value = "isolated", Url = "https://feather-webkit.test/" }]);
        bool cookiesWork = (await context.CookiesAsync()).Any(cookie => cookie.Name == "feather-test" && cookie.Value == "isolated");
        await using var otherContext = await browser.NewContextAsync();
        bool isolated = !(await otherContext.CookiesAsync()).Any();
        bool passed = text == "WebKit JavaScript" && userAgent.Contains("AppleWebKit", StringComparison.Ordinal)
            && !userAgent.Contains("Chrome/", StringComparison.Ordinal) && cookiesWork && isolated;
        string browserVersion = browser.Version;
        string screenshot = Path.ChangeExtension(options.ResultPath!, ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
        await page.ScreenshotAsync(new() { Path = screenshot });
        await browser.CloseAsync();

        // Exercise the same persistent-context mode used by the interactive preview.
        string testProfile = Path.Combine(Path.GetDirectoryName(options.ResultPath!)!, "profile-" + Guid.NewGuid().ToString("N"));
        bool contextCloseEvent = false;
        await using (var persistent = await playwright.Webkit.LaunchPersistentContextAsync(testProfile, new() { Headless = !options.Headed }))
        {
            persistent.Close += (_, _) => contextCloseEvent = true;
            await persistent.AddCookiesAsync([new Cookie
            {
                Name = "feather-persistent", Value = "saved", Domain = "feather-webkit.test", Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()
            }]);
            await persistent.CloseAsync();
        }
        bool persistentProfile;
        await using (var reopened = await playwright.Webkit.LaunchPersistentContextAsync(testProfile, new() { Headless = !options.Headed }))
            persistentProfile = (await reopened.CookiesAsync()).Any(cookie => cookie.Name == "feather-persistent" && cookie.Value == "saved");
        passed &= contextCloseEvent && persistentProfile;
        Program.WriteResult(options.ResultPath!, new
        {
            passed, engine = "WebKit (Microsoft.Playwright)", browserVersion,
            userAgent, domAndJavaScript = text == "WebKit JavaScript", cookiesWork, isolated,
            persistentProfile, contextCloseEvent, testProfile, screenshot, realUserProfileUsed = false
        });
        return passed ? 0 : 1;
    }
}
