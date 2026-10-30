using CefSharp;
using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private int _cefLoginBridgeMessages;
    private int _cefRawJsMessages;
    private string _cefRawJsMessageInfo = "";
    internal int CefLoginBridgeMessagesForTest => _cefLoginBridgeMessages;
    internal int CefRawJsMessagesForTest => Volatile.Read(ref _cefRawJsMessages);
    internal string CefRawJsMessageInfoForTest => _cefRawJsMessageInfo;

    /// <summary>Opaque CEF message from the main frame; never contains stored secrets.</summary>
    private void ConfigureCefPasswordAutofill(CefTabRuntime runtime)
    {
        var browser = runtime.View;
        browser.JavascriptMessageReceived += (_, args) =>
        {
            if (_smoke) Interlocked.Increment(ref _cefRawJsMessages);
            try
            {
                // JavascriptMessageReceived runs on a ThreadPool after CEF has
                // sometimes disposed the event IFrame. Never dereference it.
                // Treat all messages as untrusted hints; on the UI thread the
                // current top document, origin and login-field geometry are
                // independently validated before any vault lookup or fill.
                var json = JsonSerializer.Serialize(args.Message);
                if (_smoke) _cefRawJsMessageInfo = "main frame, bytes=" + json.Length;
                DispatchCefUi(() => HandleCefCredentialsMessageAsync(
                    runtime, runtime.View.Address ?? "", json));
            }
            catch (Exception error) when (error is InvalidOperationException or
                ObjectDisposedException or JsonException or NotSupportedException)
            {
                if (_smoke) _cefRawJsMessageInfo = error.GetType().Name + ": " + error.Message;
                Program.LogError(DataPath, error);
            }
        };
        browser.SizeChanged += (_, _) => { if (runtime.Tab == ActiveTab) DismissPasswordPopup(); };
    }

    private async Task HandleCefCredentialsMessageAsync(CefTabRuntime runtime, string source, string json)
    {
        if (json.Length > 4096 || !IsCurrentCefPasswordDocument(runtime, source)) return;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                type.GetString() != "feather.credentials" ||
                !root.TryGetProperty("origin", out var origin) ||
                origin.GetString() != PasswordOrigin(source) ||
                !root.TryGetProperty("kind", out var kind)) return;
            var action = kind.GetString();
            if (_smoke && action is "ready" or "focus") _cefLoginBridgeMessages++;
            if (action == "dismiss") { DismissPasswordPopup(); return; }
            if (DateTimeOffset.UtcNow < _passwordSuppressUntil) return;
            if (action is "focus" or "ready")
                await OfferCefPasswordsAsync(runtime, action == "focus");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { Program.LogError(DataPath, ex); }
    }

    internal async Task<string> CefCredentialDiagnosticsForTestAsync()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ||
            !CefIsAlive(runtime) || !runtime.View.CanExecuteJavascriptInMainFrame)
            return "No active JavaScript browser";
        try
        {
            var js = "JSON.stringify({secure:window.isSecureContext,cef:typeof CefSharp,post:typeof CefSharp?.PostMessage,observer:window.__mbrowserCredObserverRan===true,postAttempts:window.__mbrowserCredPostAttempts||0,loginFound:window.__mbrowserCredLoginFound===true,passwords:document.querySelectorAll('input[type=password]').length,origin:location.origin})";
            var result = await runtime.View.EvaluateScriptAsync(js);
            var probe = await runtime.View.EvaluateScriptAsync(AutofillPageScripts.Probe(false));
            return (result.Success ? result.Result?.ToString() ?? "null" : "FAILED: " + result.Message)
                + "; probe=" + (probe.Success ? (probe.Result is null ? "null" : "object") : probe.Message);
        }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }

    private Task CefSuggestPasswordsAsync()
    {
        if (!_cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime))
            return Task.CompletedTask;
        return OfferCefPasswordsAsync(runtime, true, false);
    }

    private async Task OfferCefPasswordsAsync(CefTabRuntime runtime, bool showPopup, bool focusedOnly = true)
    {
        if (!CefIsAlive(runtime) || runtime.Tab != ActiveTab ||
            !runtime.View.CanExecuteJavascriptInMainFrame) return;
        string url = runtime.View.Address ?? "";
        string? origin = PasswordOrigin(url);
        if (origin is null) return;
        long request = ++_credentialRequest;
        try
        {
            var response = await runtime.View.EvaluateScriptAsync(AutofillPageScripts.Probe(showPopup && focusedOnly));
            if (!response.Success || response.Result is null || !IsCurrentCefPasswordDocument(runtime, url) ||
                request != _credentialRequest) return;
            string json = JsonSerializer.Serialize(response.Result);
            var probe = JsonSerializer.Deserialize<CredentialAnchor>(json, CredentialJson);
            if (probe is null || probe.Origin != origin ||
                !TryCefCredentialAnchor(runtime, probe, out Point anchor))
            {
                DismissPasswordPopup();
                return;
            }
            string listJson = FeatureModule.Invoke(DataPath, "GetAutofillSuggestions", url) as string ?? "[]";
            var suggestions = JsonSerializer.Deserialize<List<CredentialSuggestion>>(listJson, CredentialJson) ?? [];
            if (suggestions.Count == 0) { DismissPasswordPopup(); return; }
            showPopup |= probe.Focused;
            if (suggestions.Count == 1)
                await FillCefCredentialAsync(runtime, url, suggestions[0].Id, automatic: true);
            if (!IsCurrentCefPasswordDocument(runtime, url) || request != _credentialRequest) return;
            if (showPopup)
            {
                CloseCredentialPopup();
                var popup = new CredentialPopup(origin, suggestions,
                    id => RunUi(() => FillCefCredentialAsync(runtime, url, id, false)));
                _credentialPopup = popup;
                popup.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_credentialPopup, popup)) _credentialPopup = null;
                    DisposeCredentialPopupAfterDispatch(popup);
                };
                popup.Show(runtime.View, anchor);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or JsonException or InvalidOperationException)
        { if (CefIsAlive(runtime)) _status.Text = "CEF: автозаполнение недоступно: " + error.Message; }
    }

    private async Task FillCefCredentialAsync(CefTabRuntime runtime, string url, string id, bool automatic)
    {
        if (!IsCurrentCefPasswordDocument(runtime, url)) return;
        string? script = null;
        try
        {
            script = FeatureModule.Invoke(DataPath,
                automatic ? "CreateAutomaticAutofillScript" : "CreateAutofillScript", url, id) as string;
            if (script is null || !IsCurrentCefPasswordDocument(runtime, url)) return;
            if (!automatic)
            {
                _passwordSuppressUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
                runtime.View.Focus();
            }
            var response = await runtime.View.EvaluateScriptAsync(script);
            if (IsCurrentCefPasswordDocument(runtime, url) && response.Success &&
                response.Result is string text && text == "filled")
                _status.Text = "CEF: сохранённый пароль заполнен.";
        }
        finally { script = null; }
    }

    private bool IsCurrentCefPasswordDocument(CefTabRuntime runtime, string url) =>
        CefIsAlive(runtime) && runtime.Tab == ActiveTab &&
        string.Equals(runtime.View.Address, url, StringComparison.Ordinal) &&
        PasswordOrigin(url) is not null;

    private static bool TryCefCredentialAnchor(CefTabRuntime runtime, CredentialAnchor probe, out Point anchor)
    {
        anchor = Point.Empty;
        double[] values = [probe.X, probe.Y, probe.Width, probe.Height, probe.ViewportWidth, probe.ViewportHeight];
        if (values.Any(x => !double.IsFinite(x)) || probe.Width <= 0 || probe.Height <= 0 ||
            probe.ViewportWidth <= 0 || probe.ViewportHeight <= 0 ||
            probe.X < 0 || probe.Y < 0 || probe.X + probe.Width > probe.ViewportWidth + 2 ||
            probe.Y + probe.Height > probe.ViewportHeight + 2) return false;
        int x = (int)Math.Round(probe.X * runtime.View.ClientSize.Width / probe.ViewportWidth);
        int y = (int)Math.Round((probe.Y + probe.Height) * runtime.View.ClientSize.Height / probe.ViewportHeight);
        if (x < 0 || y < 0 || x >= runtime.View.ClientSize.Width || y > runtime.View.ClientSize.Height)
            return false;
        anchor = new Point(x, y);
        return true;
    }
}
