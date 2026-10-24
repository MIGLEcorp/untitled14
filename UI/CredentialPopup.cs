namespace Feather;

internal sealed record CredentialSuggestion(string Id, string Username, string Title);

/// <summary>Owned native popup: websites cannot draw or populate this credential chooser.</summary>
internal sealed class CredentialPopup : ToolStripDropDownMenu
{
    internal CredentialPopup(string origin, IReadOnlyList<CredentialSuggestion> suggestions, Action<string> choose)
    {
        AutoClose = true;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        MaximumSize = new Size(360, 440);
        Padding = new Padding(6);
        Font = new Font("Segoe UI", 10);
        AccessibleName = "Сохранённые пароли Feather";
        var heading = new ToolStripLabel("Пароли Feather · " + new Uri(origin).Host)
        {
            AutoSize = false, Size = new Size(330, 32), TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Palette.Muted, Padding = new Padding(8, 0, 0, 0)
        };
        Items.Add(heading);
        Items.Add(new ToolStripSeparator());
        foreach (var suggestion in suggestions)
        {
            string username = Display(suggestion.Username, "Без имени пользователя");
            string title = Display(suggestion.Title, "Сохранённый пароль");
            var item = new ToolStripMenuItem(username + "\n" + title)
            {
                AutoSize = false, Size = new Size(330, 58), TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 6, 8, 6), AccessibleName = "Заполнить: " + username
            };
            string id = suggestion.Id;
            item.Click += (_, _) => { Close(); choose(id); };
            Items.Add(item);
        }
        Palette.Apply(this);
    }

    private static string Display(string value, string fallback)
    {
        string text = new(value.Where(character => !char.IsControl(character)).Take(72).ToArray());
        // ToolStrip treats an ampersand as an access-key marker unless escaped.
        return (string.IsNullOrWhiteSpace(text) ? fallback : text).Replace("&", "&&", StringComparison.Ordinal);
    }
}
