using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace LocalBack.App.Services;

/// <summary>
/// Gives memory back while LocalBack sits idle in the tray: a compacting GC, then the process working set is
/// trimmed so unused pages leave RAM. Pages come back on demand when a window opens again.
/// </summary>
public static class MemoryTrim
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        try
        {
            using var p = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, -1, -1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
}
