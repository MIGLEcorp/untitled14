using System.Runtime.InteropServices;

namespace MBrowser;

/// <summary>
/// Enumerate Windows process-tree descendants of the CEF host. This avoids
/// counting unrelated Chrome/CEF instances and keeps the active WebView2 build
/// unchanged. Race conditions during process teardown are tolerated by callers.
/// </summary>
internal static class CefProcessMetrics
{
    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size;
        internal uint Usage;
        internal uint ProcessId;
        internal IntPtr DefaultHeapId;
        internal uint ModuleId;
        internal uint ThreadCount;
        internal uint ParentProcessId;
        internal int BasePriority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    internal static HashSet<int> GetDescendantIds(int parentId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1) || snapshot == IntPtr.Zero)
            return [];
        var relationships = new Dictionary<int, int>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    relationships[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }

        var children = new HashSet<int>();
        var remaining = relationships;
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, parent) in remaining)
            {
                if (id == parentId || children.Contains(id)) continue;
                if (parent == parentId || children.Contains(parent))
                {
                    children.Add(id);
                    changed = true;
                }
            }
        } while (changed);
        return children;
    }
}
