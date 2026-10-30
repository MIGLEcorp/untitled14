namespace MBrowser;

/// <summary>
/// Called only after acquiring the legacy mutex. The normal browser never sees
/// two writable user profiles at once. Migration failure never deletes source.
/// </summary>
internal static class UserProfileBootstrap
{
    internal static string Prepare(string desiredProfile)
    {
        string legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FeatherBrowser");
        if (!Directory.Exists(legacy)) return desiredProfile;

        using var progress = new Form
        {
            Text = "MBrowser — перенос старого профиля",
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(490, 160),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ControlBox = false,
            ShowInTaskbar = false
        };
        using var label = new Label
        {
            Text = "Переносим закладки, вкладки, пароли и архив WebView2…",
            Dock = DockStyle.Top, Height = 75,
            TextAlign = ContentAlignment.MiddleCenter
        };
        using var bar = new ProgressBar
        {
            Dock = DockStyle.Bottom, Height = 14,
            Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24
        };
        progress.Controls.Add(label);
        progress.Controls.Add(bar);
        progress.Show();
        progress.Refresh();

        try
        {
            var lastTick = System.Diagnostics.Stopwatch.StartNew();
            var result = ProfileMigration.MoveVerified(legacy, desiredProfile, (count, bytes) =>
            {
                if (lastTick.ElapsedMilliseconds < 350) return;
                label.Text = "Скопировано, проверка после копирования: " + count.ToString("N0") +
                    " файлов, " + (bytes / 1048576.0).ToString("N1") + " МиБ…";
                progress.Refresh();
                Application.DoEvents();
                lastTick.Restart();
            });
            if (!result.DeletedLegacy && Directory.Exists(legacy))
            {
                MessageBox.Show(
                    "Новый профиль MBrowser подготовлен, но старая папка " +
                    "FeatherBrowser пока осталась. Новая версия использует профиль MBrowser. " +
                    "Не удаляйте старую папку вручную до проверки всех данных.",
                    "MBrowser — перенос профиля", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return result.Profile;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(
                "Перенос не завершён: " + error.Message + Environment.NewLine +
                "Старый профиль FeatherBrowser сохранён. MBrowser продолжит работать " +
                "с ним до исправления проблемы.",
                "MBrowser — перенос профиля", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return legacy;
        }
        finally { progress.Close(); }
    }
}