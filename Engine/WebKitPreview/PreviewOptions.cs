namespace MBrowser.WebKit;

/// <summary>Explicit paths keep the optional engine separate from every browser's real profile.</summary>
internal sealed record PreviewOptions
{
    internal string Url { get; private set; } = "https://example.com/";
    internal string ProfilePath { get; private set; } = Path.Combine(DefaultDataPath, "WebKitPreview");
    internal string BrowsersPath { get; private set; } = Path.Combine(DefaultDataPath, "Engines", "WebKit");
    internal string? ResultPath { get; private set; }
    internal bool Install { get; private set; }
    internal bool SmokeTest { get; private set; }
    internal bool Headed { get; private set; }

    private static string DefaultDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FeatherBrowser");

    internal static PreviewOptions Parse(string[] args)
    {
        var options = new PreviewOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string NextValue()
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Не указано значение параметра " + args[i - 1]);
                return args[i];
            }

            switch (args[i])
            {
                case "--url": options.Url = ValidateUrl(NextValue()); break;
                case "--profile": options.ProfilePath = Path.GetFullPath(NextValue()); break;
                case "--browsers-path": options.BrowsersPath = Path.GetFullPath(NextValue()); break;
                case "--result": options.ResultPath = Path.GetFullPath(NextValue()); break;
                case "--install": options.Install = true; break;
                case "--smoke-test": options.SmokeTest = true; break;
                case "--headed": options.Headed = true; break;
                default: throw new ArgumentException("Неизвестный параметр: " + args[i]);
            }
        }

        if (options.SmokeTest && options.ResultPath is null)
            throw new ArgumentException("Для --smoke-test нужен --result с путём к JSON-отчёту.");
        if (options.Install && options.SmokeTest)
            throw new ArgumentException("Параметры --install и --smoke-test используются отдельно.");
        return options;
    }

    internal static string ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("WebKit открывает только HTTP/HTTPS-адреса без логина и пароля в URL.");
        return uri.AbsoluteUri;
    }
}
