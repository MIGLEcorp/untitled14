using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MBrowser;

internal sealed partial class MainForm
{
    private CredentialPopup? _credentialPopup;
    private long _credentialRequest;
    private DateTimeOffset _passwordSuppressUntil;
    private static readonly JsonSerializerOptions CredentialJson = new() { PropertyNameCaseInsensitive = true };

    internal bool IsCredentialPopupVisible => _credentialPopup?.Visible == true;
    internal int CredentialPopupCount => _credentialPopup?.Items.Count ?? 0;
    internal Rectangle CredentialPopupBoundsForTest => _credentialPopup is { } popup ? popup.RectangleToScreen(popup.ClientRectangle) : Rectangle.Empty;
    internal Rectangle ActiveWebViewBoundsForTest => ActiveRuntime is { } runtime ? runtime.View.RectangleToScreen(runtime.View.ClientRectangle) : Rectangle.Empty;
    internal int SelectedTabIndexForTest => _session.SelectedIndex;

    private async Task ConfigurePasswordAutofillAsync(TabRuntime runtime)
    {
        var core = runtime.View.CoreWebView2;
        core.WebMessageReceived += (_, message) =>
        {
            // Web messages are page input. Only their event kind is used; the anchor is re-probed.
            string source = message.Source;
            string json = message.WebMessageAsJson;
            RunUi(() => HandlePasswordMessageAsync(runtime, source, json));
        };
        core.DOMContentLoaded += (_, _) => RunUi(() => OfferPasswordAutofillAsync(runtime, showPopup: false));
        runtime.View.SizeChanged += (_, _) => DismissPasswordPopup();
        await core.AddScriptToExecuteOnDocumentCreatedAsync(AutofillPageScripts.Observer);
    }

    private async Task HandlePasswordMessageAsync(TabRuntime runtime, string source, string json)
    {
        if (json.Length > 4096 || !IsCurrentPasswordDocument(runtime, source)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type) || type.GetString() != "feather.credentials" ||
                !message.TryGetProperty("origin", out var origin) || origin.GetString() != PasswordOrigin(source) ||
                !message.TryGetProperty("kind", out var kind)) return;
            string? action = kind.GetString();
            if (action == "dismiss") { DismissPasswordPopup(); return; }
            if (DateTimeOffset.UtcNow < _passwordSuppressUntil) return;
            if (action is "focus" or "ready") await OfferPasswordAutofillAsync(runtime, showPopup: action == "focus");
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private Task SuggestPasswordsAsync()
    {
        var runtime = ActiveRuntime;
        return runtime is null ? Task.CompletedTask : OfferPasswordAutofillAsync(runtime, showPopup: true, focusedOnly: false);
    }

    private async Task OfferPasswordAutofillAsync(TabRuntime runtime, bool showPopup, bool focusedOnly = true)
    {
        if (!IsAlive(runtime) || ActiveRuntime != runtime) return;
        string url = runtime.View.CoreWebView2.Source;
        string? origin = PasswordOrigin(url);
        if (origin is null) return;
        long request = ++_credentialRequest;
        try
        {
            // Never position trusted UI from message-supplied coordinates. This independent query
            // checks the actual focused login field in the current top document.
            string probeJson = await runtime.View.CoreWebView2.ExecuteScriptAsync(AutofillPageScripts.Probe(showPopup && focusedOnly));
            if (request != _credentialRequest || !IsCurrentPasswordDocument(runtime, url)) return;
            var probe = JsonSerializer.Deserialize<CredentialAnchor>(probeJson, CredentialJson);
            if (probe is null || probe.Origin != origin || !TryCredentialAnchor(runtime, probe, out Point anchor))
            {
                DismissPasswordPopup();
                return;
            }
            string json = FeatureModule.Invoke(DataPath, "GetAutofillSuggestions", url) as string ?? "[]";
            var suggestions = JsonSerializer.Deserialize<List<CredentialSuggestion>>(json, CredentialJson) ?? [];
            if (suggestions.Count == 0) { DismissPasswordPopup(); return; }
            // A ready event can supersede a simultaneous focus event. Preserve the actual
            // focused-field intent in the latest request instead of dropping its chooser.
            showPopup |= probe.Focused;

            // A single saved login can fill without any keyboard shortcut. Do not overwrite input
            // already typed by the user; multiple accounts always wait for a chooser selection.
            if (suggestions.Count == 1)
                await FillCredentialAsync(runtime, url, suggestions[0].Id, automatic: true);
            if (request != _credentialRequest || !IsCurrentPasswordDocument(runtime, url)) return;
            if (showPopup)
            {
                CloseCredentialPopup();
                var popup = new CredentialPopup(origin, suggestions,
                    id => RunUi(() => FillCredentialAsync(runtime, url, id, automatic: false)));
                _credentialPopup = popup;
                popup.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_credentialPopup, popup)) _credentialPopup = null;
                    DisposeCredentialPopupAfterDispatch(popup);
                };
                popup.Show(runtime.View, anchor);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or
            JsonException or COMException or InvalidOperationException)
        {
            if (IsAlive(runtime)) _status.Text = "Не удалось открыть сохранённые пароли: " + error.Message;
        }
    }

    private async Task FillCredentialAsync(TabRuntime runtime, string url, string id, bool automatic)
    {
        if (!IsCurrentPasswordDocument(runtime, url)) return;
        string? script = null;
        try
        {
            script = FeatureModule.Invoke(DataPath, automatic ? "CreateAutomaticAutofillScript" : "CreateAutofillScript", url, id) as string;
            if (script is null || !IsCurrentPasswordDocument(runtime, url)) return;
            if (!automatic)
            {
                _passwordSuppressUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
                runtime.View.Focus();
            }
            string result = await runtime.View.CoreWebView2.ExecuteScriptAsync(script);
            if (IsCurrentPasswordDocument(runtime, url) && result == "\"filled\"") _status.Text = "Сохранённый пароль заполнен.";
        }
        finally { script = null; }
    }

    private bool IsCurrentPasswordDocument(TabRuntime runtime, string url) => IsAlive(runtime) && ActiveRuntime == runtime &&
        string.Equals(runtime.View.CoreWebView2.Source, url, StringComparison.Ordinal) && PasswordOrigin(url) is not null;

    private static string? PasswordOrigin(string url)
    {
        if (!AddressParser.TryWebUrl(url, out string valid)) return null;
        var uri = new Uri(valid);
        if (uri.Scheme != "https" && !uri.IsLoopback) return null;
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        return uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool TryCredentialAnchor(TabRuntime runtime, CredentialAnchor probe, out Point anchor)
    {
        anchor = Point.Empty;
        double[] values = [probe.X, probe.Y, probe.Width, probe.Height, probe.ViewportWidth, probe.ViewportHeight];
        if (values.Any(value => !double.IsFinite(value)) || probe.Width <= 0 || probe.Height <= 0 || probe.ViewportWidth <= 0 || probe.ViewportHeight <= 0 ||
            probe.X < 0 || probe.Y < 0 || probe.X + probe.Width > probe.ViewportWidth + 2 || probe.Y + probe.Height > probe.ViewportHeight + 2) return false;
        // CSS pixel bounds are mapped to the actual WinForms viewport, accounting for page zoom/DPI.
        double scaleX = runtime.View.ClientSize.Width / probe.ViewportWidth;
        double scaleY = runtime.View.ClientSize.Height / probe.ViewportHeight;
        int x = (int)Math.Round(probe.X * scaleX), y = (int)Math.Round((probe.Y + probe.Height) * scaleY);
        if (x < 0 || y < 0 || x >= runtime.View.ClientSize.Width || y > runtime.View.ClientSize.Height) return false;
        anchor = new Point(x, y);
        return true;
    }

    private sealed record CredentialAnchor(string Origin, double X, double Y, double Width, double Height, double ViewportWidth, double ViewportHeight, bool Focused = false);

    private void DismissPasswordPopup()
    {
        _credentialRequest++;
        CloseCredentialPopup();
    }

    private void CloseCredentialPopup()
    {
        var popup = _credentialPopup;
        _credentialPopup = null;
        if (popup is null) return;
        bool wasVisible = popup.Visible;
        popup.Close();
        if (!wasVisible) DisposeCredentialPopupAfterDispatch(popup);
    }

    private void DisposeCredentialPopupAfterDispatch(CredentialPopup popup)
    {
        // ToolStrip continues its click/visibility dispatch after Closed. Disposing in that
        // event invalidates the object underneath OnItemClicked, so retire it on the next UI turn.
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke((Action)(() => popup.Dispose()));
        else popup.Dispose();
    }

    // Fixture-only selection goes through the same native chooser click callback as a real click.
    internal void SelectCredentialSuggestionForTest(int index)
    {
        if (!_smoke) throw new InvalidOperationException("Credential fixture selection requires a smoke-test profile.");
        var item = _credentialPopup?.Items.ElementAtOrDefault(index);
        if (item is null) throw new InvalidOperationException("Credential suggestion does not exist.");
        item.PerformClick();
    }
}
