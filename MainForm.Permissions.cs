
namespace MBrowser;

internal sealed partial class MainForm
{
    private TaskCompletionSource<bool>? _permissionChoice;
    private CheckBox? _permissionRemember;
    private Control? _permissionPanel;
    private readonly SemaphoreSlim _permissionQueue = new(1, 1);
    internal bool IsPermissionPromptVisible => _permissionChoice is { Task.IsCompleted: false };

    private async Task<bool> AskInsideWindowAsync(string title, string detail, bool remember = false)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new GlassPanel { Size = new Size(390, 265) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = Color.Transparent };
        flow.Controls.Add(new Label { Text = title, ForeColor = Palette.Text, BackColor = Color.Transparent,
            Width = 350, Height = 38, Font = new Font("Segoe UI", 13, FontStyle.Bold) });
        flow.Controls.Add(new TextBox { Text = detail, ForeColor = Palette.Text, BackColor = Palette.Surface,
            Width = 350, Height = 100, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical });
        _permissionRemember = new CheckBox { Text = "Запомнить для этого сайта", ForeColor = Palette.Text, BackColor = Color.Transparent,
            Width = 350, Height = 28, Visible = remember };
        flow.Controls.Add(_permissionRemember);
        var actions = new FlowLayoutPanel { Width = 350, Height = 42, BackColor = Color.Transparent };
        foreach (var (text, value) in new[] { ("Разрешить", true), ("Отказать", false) })
        {
            var button = Palette.Button(text, text, 155);
            button.Click += (_, _) => completion.TrySetResult(value);
            actions.Controls.Add(button);
        }
        flow.Controls.Add(actions);
        panel.Controls.Add(flow);
        panel.Disposed += (_, _) => completion.TrySetResult(false);
        ShowOverlay(panel, new Point(16, 10));
        _permissionPanel = panel;
        _permissionChoice = completion;
        bool allowed = await completion.Task;
        return allowed;
    }

    internal void DecidePermissionForTest(bool allow)
    {
        if (!_smoke) throw new InvalidOperationException("Test profile required.");
        _permissionChoice?.TrySetResult(allow);
    }
}
