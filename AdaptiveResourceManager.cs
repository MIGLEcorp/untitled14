using System.Runtime.InteropServices;

namespace MBrowser;

internal enum MemoryMode { Performance, Balanced, LowMemory, Extreme }

/// <summary>Pure, deterministic tab-eviction policy. A tab's page-safety checks happen separately.</summary>
internal static class AdaptiveResourceManager
{
    internal static (TimeSpan Idle, int MaxViews, long MinFreeBytes, TimeSpan Grace) Settings(
        MemoryMode mode, int configuredMinutes) => mode switch
    {
        MemoryMode.Performance => (TimeSpan.MaxValue, 100, 0L, TimeSpan.MaxValue),
        MemoryMode.LowMemory => (TimeSpan.FromMinutes(Math.Min(configuredMinutes, 4)), 5, 2L * 1024 * 1024 * 1024, TimeSpan.FromSeconds(60)),
        MemoryMode.Extreme => (TimeSpan.FromMinutes(Math.Min(configuredMinutes, 2)), 2, 3L * 1024 * 1024 * 1024, TimeSpan.FromSeconds(30)),
        _ => (TimeSpan.FromMinutes(Math.Max(configuredMinutes, 1)), 12, 1536L * 1024 * 1024, TimeSpan.FromSeconds(90))
    };

    internal static bool ShouldHibernate(MemoryMode mode, DateTimeOffset lastUsedUtc,
        DateTimeOffset now, int liveViews, long availableBytes, int configuredMinutes)
    {
        var (idle, maxViews, minFree, grace) = Settings(mode, configuredMinutes);
        var elapsed = now - lastUsedUtc;
        if (elapsed < grace) return false; // Prevent ping-pong when switching tabs.
        return elapsed >= idle || liveViews > maxViews ||
            (minFree > 0 && availableBytes > 0 && availableBytes < minFree);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    internal static long AvailableRamBytes()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue) : -1;
    }
}

/// <summary>Chromium owns VRAM. This setting only enables or disables its built-in GPU rendering.</summary>
internal static class GraphicsOptimizationManager
{
    internal static string BrowserArguments(bool hardwareAcceleration, bool smoke)
    {
        var diagnostic = smoke
            ? "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --disable-features=CalculateNativeWinOcclusion"
            : "";
        var graphics = hardwareAcceleration ? "" : "--disable-gpu";
        return string.Join(" ", new[] { diagnostic, graphics }.Where(x => x.Length > 0));
    }
}
