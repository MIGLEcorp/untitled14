namespace MBrowser;

internal sealed partial class MainForm
{
    internal void AcceptShellTargets(string[] targets) => RunUi(() => OpenShellTargetsAsync(targets));
    internal async Task OpenShellTargetsAsync(string[] targets)
    {
        await ReadyTask;
        if (_closing) return;
        foreach (string target in targets.Take(16))
        {
            if (AddressParser.TryWebUrl(target, out var web)) await OpenTabAsync(web);
            else if (BrowserDocuments.TryResolve(target, out var file))
                await TransitionAsync(async () =>
                {
                    if (TabCount >= 100) return;
                    var tab = new BrowserTab { Title = Path.GetFileName(new Uri(file).LocalPath), LocalDocuments = [file] };
                    tab.Visit(file); _session.Tabs.Add(tab); _session.SelectedIndex = _session.Tabs.Count - 1;
                    await ShowActiveTabAsync();
                });
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show(); Activate();
    }
    private void OpenDocument()
    {
        using var file = new OpenFileDialog { Filter = "Веб-документы, PDF и изображения|" + string.Join(";", BrowserDocuments.Extensions.Select(extension => "*" + extension)), Multiselect = true };
        if (file.ShowDialog(this) == DialogResult.OK) AcceptShellTargets(file.FileNames);
    }
}
