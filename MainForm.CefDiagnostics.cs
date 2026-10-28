using System.Globalization;
using System.Text;

namespace MBrowser;

internal sealed partial class MainForm
{
    private readonly string _cefStartupFeatures;

    private void ShowCefDiagnostics()
    {
        if (!_cefEngine) return;
        MemorySnapshot snapshot = ReadMemory();
        string host = ActiveTab.CurrentUrl ?? "about:blank";
        string csvPath = Path.Combine(DataPath, "cef-memory-samples.csv");
        string[] values =
        [
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Csv(host),
            snapshot.ProcessCount.ToString(CultureInfo.InvariantCulture),
            snapshot.PrivateBytes.ToString(CultureInfo.InvariantCulture),
            snapshot.WorkingSetBytes.ToString(CultureInfo.InvariantCulture),
            Csv(_cefStartupFeatures)
        ];
        try
        {
            Directory.CreateDirectory(DataPath);
            bool header = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
            using var stream = new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            if (header)
                writer.WriteLine("timestamp_utc,url,processes,private_bytes,working_set_bytes,cef_startup_features");
            writer.WriteLine(string.Join(",", values));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Program.LogError(DataPath, ex); }

        ShowMenuPage("CEF — замер памяти", rows =>
        {
            rows("Процессов MBrowser + CEF: " + snapshot.ProcessCount, ShowCefDiagnostics);
            rows("Private bytes: " + (snapshot.PrivateBytes / 1048576.0).ToString("N1") + " MiB",
                ShowCefDiagnostics);
            rows("Working set: " + (snapshot.WorkingSetBytes / 1048576.0).ToString("N1") + " MiB",
                ShowCefDiagnostics);
            rows("Активные флаги с момента запуска: " +
                (string.IsNullOrEmpty(_cefStartupFeatures) ? "НЕТ" : _cefStartupFeatures),
                ShowCefDiagnostics);
            rows("Эти числа относятся ко всей группе процессов CEF.", ShowCefDiagnostics);
            rows("Снять ещё один замер и записать CSV", ShowCefDiagnostics);
            rows("Скопировать путь к CSV", () => Clipboard.SetText(csvPath));
        });
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
