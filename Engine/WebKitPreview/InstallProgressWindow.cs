namespace MBrowser.WebKit;

/// <summary>The installer runs only in this optional process; the main browser stays responsive.</summary>
internal sealed class InstallProgressWindow : Form
{
    internal InstallProgressWindow()
    {
        Text = "MBrowser · установка WebKit";
        ClientSize = new Size(460, 115);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var label = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(18, 16, 18, 0),
            Text = "Скачиваем WebKit с серверов Microsoft.\nВремя зависит от подключения к интернету."
        };
        var progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 24, Style = ProgressBarStyle.Marquee };
        Controls.Add(label);
        Controls.Add(progress);
        Shown += async (_, _) =>
        {
            try
            {
                int exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "webkit"]));
                if (exitCode != 0)
                    throw new IOException("Загрузчик WebKit завершился с кодом " + exitCode + ". Проверьте подключение и попробуйте снова.");
                DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.Abort;
            }
            Close();
        };
    }
}
