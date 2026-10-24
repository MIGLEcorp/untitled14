using System.Text.Json;

namespace MBrowser;

internal sealed partial class MainForm
{
    private CancellationTokenSource? _suggestionCancellation;
    internal TextBox AddressInputForTest => _address;
    internal bool HasSuggestionsForTest => _overlay is not null && _menu is null && !IsPermissionPromptVisible;
    private static readonly HttpClient SuggestionsHttp = new() { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 65536 };
    private void AttachSuggestions(TextBox input)
    {
        input.TextChanged += (_, _) =>
        {
            if (!input.Focused) return;
            _suggestionCancellation?.Cancel();
            _suggestionCancellation?.Dispose();
            var cancellation = _suggestionCancellation = new CancellationTokenSource();
            RunUi(() => UpdateSuggestionsAsync(input, cancellation.Token));
        };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { CloseOverlay(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Down && _overlay?.Controls.OfType<FlowLayoutPanel>().FirstOrDefault()?.Controls.OfType<Button>().FirstOrDefault() is { } first)
            { first.Focus(); e.SuppressKeyPress = true; }
        };
    }

    private async Task UpdateSuggestionsAsync(TextBox input, CancellationToken token)
    {
        string query = input.Text.Trim();
        if (query.Length is < 2 or > 256) { CloseOverlay(); return; }
        try
        {
            await Task.Delay(260, token);
            var suggestions = _bookmarks.Where(item => (item.Title + " " + item.Url).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(item => (Text: item.Title, Value: item.Url)).Take(4).ToList();
            suggestions.AddRange(_session.Tabs.SelectMany(tab => tab.History).Distinct().Where(url => url.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(3).Select(url => (url, url)));
            // Do not send URL-looking strings, credentials or local addresses to a suggestion provider.
            if (!_smoke && _preferences.NetworkSuggestions && !query.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !query.Contains('.') && !query.Contains('/') && !query.Contains(':') && !query.Contains('@'))
            {
                try
                {
                    string json = await SuggestionsHttp.GetStringAsync("https://duckduckgo.com/ac/?type=list&q=" + Uri.EscapeDataString(query), token);
                    using var response = JsonDocument.Parse(json);
                    if (response.RootElement.ValueKind == JsonValueKind.Array && response.RootElement.GetArrayLength() > 1 && response.RootElement[1].ValueKind == JsonValueKind.Array)
                        suggestions.AddRange(response.RootElement[1].EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                            .Take(7).Select(value => (value.GetString()!, value.GetString()!)));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            }
            if (token.IsCancellationRequested || !input.Focused || input.Text.Trim() != query || _closing) return;
            var rows = suggestions.DistinctBy(item => item.Value).Take(8).ToList();
            if (rows.Count == 0) { CloseOverlay(); return; }
            var panel = new GlassPanel { Size = new Size(Math.Clamp(input.Width + 35, 300, 650), rows.Count * 41 + 20), Padding = new Padding(8) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
            foreach (var row in rows)
            {
                var button = Palette.Button(row.Text, row.Text, panel.Width - 22);
                button.Height = 37;
                button.TextAlign = ContentAlignment.MiddleLeft;
                button.BackColor = Color.Transparent;
                button.Click += (_, _) => { CloseOverlay(); RunUi(() => NavigateInputAsync(row.Value)); };
                flow.Controls.Add(button);
            }
            panel.Controls.Add(flow);
            ShowOverlay(panel, _viewport.PointToClient(input.PointToScreen(new Point(-12, input.Height + 14))));
        }
        catch (OperationCanceledException) { }
    }
}
