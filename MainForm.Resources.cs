using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MBrowser;

internal static class NativeGpuDiagnostics
{
    [DllImport("MBrowser.GpuDiagnostics.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "MB_QueryLocalVideoMemory")]
    private static extern int Query(out ulong used, out ulong budget, out ulong availableForReservation);

    [DllImport("MBrowser.GpuDiagnostics.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "MB_QuerySystemDedicatedVideoMemory")]
    private static extern int QuerySystemDedicated(out ulong bytes);

    internal static (ulong Used, ulong Budget)? Read()
    {
        try { return Query(out var used, out var budget, out _) != 0 ? (used, budget) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
    }

    internal static ulong? SystemDedicatedUsage()
    {
        try { return QuerySystemDedicated(out var bytes) != 0 ? bytes : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
    }
}

internal sealed partial class MainForm
{
    private readonly HashSet<Guid> _hibernatedIds = [];
    private int _hibernateCount;
    private int _restoreCount;
    private double _restoreTotalMilliseconds;
    internal int HibernationCountForTest => _hibernateCount;
    internal int RestorationCountForTest => _restoreCount;

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("N0") + " МБ";

    private void SaveResourcePreferences()
    {
        try { _preferences.Save(DataPath); SetMemoryTargets(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex); }
    }

    private void ShowResourceSettings()
    {
        CloseOverlay();
        var panel = new GlassPanel { Size = new Size(435, 530) };
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(12)
        };
        Label TextRow(string text, int height = 29)
        {
            var label = new Label { Text = text, Width = 375, Height = height, ForeColor = Palette.Text,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            content.Controls.Add(label);
            return label;
        }

        var heading = TextRow("Память и GPU", 45);
        heading.Font = new Font("Segoe UI", 15, FontStyle.Bold);
        TextRow("Режим работы вкладок:");
        var modes = new ComboBox
        {
            Width = 375, Height = 36, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Palette.Raised, ForeColor = Palette.Text, AccessibleName = "Режим экономии памяти"
        };
        modes.Items.AddRange(["Performance — быстродействие", "Balanced — баланс",
            "Low Memory — экономия RAM", "Extreme — минимум RAM"]);
        modes.SelectedIndex = (int)_preferences.MemoryMode;
        modes.SelectedIndexChanged += (_, _) =>
        {
            _preferences.MemoryMode = (MemoryMode)modes.SelectedIndex;
            SaveResourcePreferences();
        };
        content.Controls.Add(modes);
        TextRow("Время простоя (минуты):");
        var minutes = new NumericUpDown
        {
            Minimum = 1, Maximum = 120, Value = _preferences.UnloadAfterMinutes, Width = 130,
            BackColor = Palette.Raised, ForeColor = Palette.Text, AccessibleName = "Время до экономии памяти"
        };
        minutes.ValueChanged += (_, _) => { _preferences.UnloadAfterMinutes = (int)minutes.Value; SaveResourcePreferences(); };
        content.Controls.Add(minutes);

        CheckBox Toggle(string text, bool value, Action<bool> onChanged)
        {
            var control = new CheckBox
            {
                Text = text, Checked = value, Width = 375, Height = 39,
                BackColor = Palette.Surface, ForeColor = Palette.Text, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8), AccessibleName = text
            };
            control.CheckedChanged += (_, _) => onChanged(control.Checked);
            content.Controls.Add(control);
            return control;
        }
        Toggle("Автоматически выгружать неактивные вкладки", _preferences.AutomaticHibernate,
            value => { _preferences.AutomaticHibernate = value; SaveResourcePreferences(); });
        Toggle("GPU-ускорение Chromium (использовать VRAM)", _preferences.HardwareAcceleration,
            value => { _preferences.HardwareAcceleration = value; SaveResourcePreferences(); });
        if (Uri.TryCreate(ActiveTab.CurrentUrl, UriKind.Absolute, out var currentUri) &&
            currentUri.Scheme is "https" or "http")
        {
            string host = currentUri.Host.ToLowerInvariant();
            Toggle("Не выгружать автоматически: " + host,
                _preferences.NeverHibernateHosts.Contains(host, StringComparer.OrdinalIgnoreCase), enabled =>
                {
                    _preferences.NeverHibernateHosts.RemoveAll(item => item.Equals(host, StringComparison.OrdinalIgnoreCase));
                    if (enabled) _preferences.NeverHibernateHosts.Add(host);
                    SaveResourcePreferences();
                });
        }
        var restart = TextRow("После изменения GPU-ускорения перезапусти браузер.", 42);
        restart.ForeColor = Palette.Muted;
        var limitations = TextRow("VRAM используется для графики; память JS/DOM в неё не переносится.", 57);
        limitations.ForeColor = Palette.Muted;
        var diagnostics = Palette.Button("Диагностика RAM / VRAM ›", "Открыть диагностику ресурсов", 370);
        diagnostics.Height = 42;
        diagnostics.Click += (_, _) => ShowResourceDiagnostics();
        content.Controls.Add(diagnostics);
        panel.Controls.Add(content);
        ShowOverlay(panel, new Point(_viewport.Width - panel.Width - 12, 10));
        _menu = panel;
    }

    private void ShowResourceDiagnostics()
    {
        var memory = ReadMemory();
        long appWs = 0, appPrivate = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            appWs = process.WorkingSet64;
            appPrivate = process.PrivateMemorySize64;
        }
        catch (InvalidOperationException) { }
        var gpu = NativeGpuDiagnostics.Read();
        var systemGpuUsage = NativeGpuDiagnostics.SystemDedicatedUsage();
        long available = AdaptiveResourceManager.AvailableRamBytes();
        int unloaded = _session.Tabs.Count(tab => tab.CurrentUrl is not null and not "about:blank" &&
                                                     !_cefRuntimes.ContainsKey(tab.Id));
        long sessionBytes = 0;
        try { sessionBytes = new FileInfo(Path.Combine(DataPath, "session.json")).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        ShowMenuPage("Диагностика ресурсов", rows =>
        {
            rows("Режим: " + _preferences.MemoryMode, ShowResourceSettings);
            rows("Приложение RAM (Working Set): " + Mb(appWs), ShowResourceDiagnostics);
            rows("Приложение Private Bytes: " + Mb(appPrivate), ShowResourceDiagnostics);
            rows("Все процессы Working Set*: " + Mb(memory.WorkingSetBytes), ShowResourceDiagnostics);
            rows("Все процессы Private Bytes: " + Mb(memory.PrivateBytes), ShowResourceDiagnostics);
            rows("CEF процессы Working Set*: " + Mb(Math.Max(0, memory.WorkingSetBytes-appWs)), ShowResourceDiagnostics);
            rows("Состояние вкладок на SSD: " + (sessionBytes / 1024.0).ToString("N1") + " КБ", ShowResourceDiagnostics);
            rows("Процессов: " + memory.ProcessCount, ShowResourceDiagnostics);
            rows("Доступно RAM: " + (available >= 0 ? Mb(available) : "нет данных"), ShowResourceDiagnostics);
            rows("Активных CEF: " + _cefRuntimes.Count, ShowResourceDiagnostics);
            rows("Выгружено вкладок: " + unloaded, ShowResourceDiagnostics);
            rows("Выгрузок / восстановлений: " + _hibernateCount + " / " + _restoreCount, ShowResourceDiagnostics);
            rows("Среднее восстановление: " + (_restoreCount == 0 ? "—" :
                (_restoreTotalMilliseconds / _restoreCount).ToString("N0") + " мс"), ShowResourceDiagnostics);
            rows("VRAM системы (PDH)*: " + (systemGpuUsage is { } usage ? Mb((long)usage) : "нет данных"), ShowResourceDiagnostics);
            rows("Бюджет GPU для приложения (DXGI): " + (gpu is { } v ? Mb((long)v.Budget) : "нет данных"), ShowResourceDiagnostics);
            rows("* Сумма WS может учитывать общие страницы", ShowResourceDiagnostics);
            rows("* PDH — видеопамять по счётчикам GPU", ShowResourceDiagnostics);
            rows("DXGI — бюджет процесса, не всех процессов CEF", ShowResourceDiagnostics);
            rows("Обновить показатели", ShowResourceDiagnostics);
            rows("Настройки памяти и GPU", ShowResourceSettings);
        });
    }
}
