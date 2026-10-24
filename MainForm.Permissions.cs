using Microsoft.Web.WebView2.Core;

namespace Feather;

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

    private void ConfigurePermissions(TabRuntime runtime)
    {
        runtime.View.CoreWebView2.PermissionRequested += (_, args) =>
        {
            args.Handled = true;
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
            var deferral = args.GetDeferral();
            RunUi(async () =>
            {
                await _permissionQueue.WaitAsync();
                try
                {
                    if (!IsAlive(runtime) || runtime.Tab != ActiveTab || !AddressParser.TryWebUrl(args.Uri, out string url)) return;
                    var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
                    if (new Uri(url).Scheme != "https" && !new Uri(url).IsLoopback) return;
                    string name = args.PermissionKind switch
                    {
                        CoreWebView2PermissionKind.Camera => "Камера",
                        CoreWebView2PermissionKind.Microphone => "Микрофон",
                        CoreWebView2PermissionKind.Geolocation => "Местоположение",
                        CoreWebView2PermissionKind.Notifications => "Уведомления",
                        CoreWebView2PermissionKind.ClipboardRead => "Чтение буфера обмена",
                        _ => args.PermissionKind.ToString()
                    };
                    bool allowed = await AskInsideWindowAsync(name, origin + "\n\nСайт запрашивает доступ: " + name.ToLowerInvariant() + ".", remember: true);
                    if (!IsAlive(runtime)) return;
                    args.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                    args.SavesInProfile = _permissionRemember?.Checked == true;
                }
                finally
                {
                    _permissionChoice = null;
                    _permissionRemember = null;
                    if (ReferenceEquals(_overlay, _permissionPanel)) CloseOverlay();
                    _permissionPanel = null;
                    // Dispose completes this SDK's deferral. Calling Complete and then Dispose
                    // would complete it twice and raise E_ILLEGAL_METHOD_CALL.
                    try { deferral.Dispose(); } catch (System.Runtime.InteropServices.COMException) { }
                    _permissionQueue.Release();
                }
            });
        };
    }

    private void ShowPermissionSettings() => RunUi(async () =>
    {
        await WithProfileCoreAsync(async core =>
        {
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            var rows = settings.Select(setting => (Origin: setting.PermissionOrigin, Kind: setting.PermissionKind, State: setting.PermissionState)).ToArray();
            ShowMenuPage("Разрешения сайтов", add =>
            {
                if (rows.Length == 0) add("Нет сохранённых решений", () => { });
                foreach (var setting in rows.Take(20))
                    add(new Uri(setting.Origin).Host + " · " + setting.Kind + " · " + setting.State,
                        () => RunUi(() => WithProfileCoreAsync(async current => { await current.Profile.SetPermissionStateAsync(setting.Kind, setting.Origin, CoreWebView2PermissionState.Default); })));
            });
        });
    });

    internal void DecidePermissionForTest(bool allow)
    {
        if (!_smoke) throw new InvalidOperationException("Test profile required.");
        _permissionChoice?.TrySetResult(allow);
    }
}
