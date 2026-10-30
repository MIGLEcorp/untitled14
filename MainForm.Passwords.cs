using System.Runtime.InteropServices;
using System.Text.Json;

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
    internal Rectangle ActiveWebViewBoundsForTest => _cefRuntimes.TryGetValue(ActiveTab.Id, out var runtime) ? runtime.View.RectangleToScreen(runtime.View.ClientRectangle) : Rectangle.Empty;
    internal int SelectedTabIndexForTest => _session.SelectedIndex;

    private static string? PasswordOrigin(string url)
    {
        if (!AddressParser.TryWebUrl(url, out string valid)) return null;
        var uri = new Uri(valid);
        if (uri.Scheme != "https" && !uri.IsLoopback) return null;
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        return uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
