namespace Feather;

internal sealed record CredentialSuggestion(string Id, string Username, string Title);

/// <summary>Trusted, in-window chooser. Credentials are never sent in page-to-host messages.</summary>
internal sealed class CredentialPopup : GlassPanel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal List<Button> Items { get; } = [];
    internal event EventHandler? Closed;
    internal CredentialPopup(string origin, IReadOnlyList<CredentialSuggestion> suggestions, Action<string> choose)
    {
        Size = new Size(350, Math.Min(440, 68 + suggestions.Count * 60));
        AccessibleName = "Сохранённые пароли Feather";
        var flow = new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,BackColor=Color.Transparent };
        flow.Controls.Add(new Label { Text="Пароли · " + new Uri(origin).Host,Width=305,Height=30,ForeColor=Palette.Muted,BackColor=Color.Transparent });
        foreach(var suggestion in suggestions)
        {
            string username=Display(suggestion.Username,"Без имени пользователя");
            var item=Palette.Button(username+"\n"+Display(suggestion.Title,"Сохранённый пароль"),"Заполнить: "+username,305);
            item.Height=54; item.TextAlign=ContentAlignment.MiddleLeft; item.BackColor=Color.Transparent;
            string id=suggestion.Id;
            item.Click+=(_,_)=>{Close();choose(id);};
            Items.Add(item);flow.Controls.Add(item);
        }
        Controls.Add(flow);
    }
    internal void Show(Control owner,Point anchor)
    {
        var parent=owner.Parent ?? throw new InvalidOperationException("Missing credential popup parent.");
        Width=Math.Min(Width,parent.Width-20); Height=Math.Min(Height,parent.Height-20);
        Location=new Point(Math.Clamp(owner.Left+anchor.X,10,Math.Max(10,parent.Width-Width-10)),
            Math.Clamp(owner.Top+anchor.Y,10,Math.Max(10,parent.Height-Height-10)));
        parent.Controls.Add(this); BringToFront(); Visible=true;
    }
    internal void Close()
    {
        if(IsDisposed)return;
        Visible=false; Parent?.Controls.Remove(this); Closed?.Invoke(this,EventArgs.Empty);
    }
    private static string Display(string value,string fallback)
    {
        string text=new(value.Where(c=>!char.IsControl(c)).Take(72).ToArray());
        return string.IsNullOrWhiteSpace(text)?fallback:text;
    }
}
