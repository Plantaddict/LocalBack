using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LocalBack.Core.Util;

/// <summary>Free and total space for any folder, including network shares (DriveInfo only knows drive letters).</summary>
public static class DiskSpace
{
    public static (long Free, long Total) Get(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows(path);
            var di = new DriveInfo(Path.GetFullPath(path));
            return di.IsReady ? (di.AvailableFreeSpace, di.TotalSize) : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    [SupportedOSPlatform("windows")]
    private static (long, long) Windows(string path)
    {
        var dir = Path.GetFullPath(path);
        if (!dir.EndsWith('\\')) dir += "\\";
        return GetDiskFreeSpaceExW(dir, out var free, out var total, out _) ? ((long)free, (long)total) : (0, 0);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeToCaller, out ulong total, out ulong freeTotal);
}
