using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalBack.Core.Model;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.Core.Drives;

public sealed record DriveCandidate(string Root, string Label, string Format, long Free, long Total, bool IsRemovable, bool IsSystem, bool HasStore)
{
    public string Letter => Root.TrimEnd('\\', '/');

    /// <summary>"E: Samsung T7".</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Letter} {Label}";
}

/// <summary>Finds backup drives by identity, not by letter.</summary>
public static class DriveLocator
{
    private static readonly HashSet<string> PseudoFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "overlay", "squashfs", "mqueue",
        "debugfs", "tracefs", "securityfs", "pstore", "bpf", "autofs", "fusectl", "configfs", "hugetlbfs", "binfmt_misc", "nsfs", "rpc_pipefs",
    };

    /// <summary>Drives that could hold backups.</summary>
    public static List<DriveCandidate> ListDrives()
    {
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
        var list = new List<DriveCandidate>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is DriveType.CDRom or DriveType.Ram or DriveType.NoRootDirectory or DriveType.Unknown) continue;
                if (!OperatingSystem.IsWindows() && PseudoFormats.Contains(d.DriveFormat)) continue;
                var isSystem = OperatingSystem.IsWindows()
                    ? string.Equals(d.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase)
                    : d.RootDirectory.FullName == "/";
                list.Add(new DriveCandidate(d.RootDirectory.FullName, SafeLabel(d), d.DriveFormat, d.AvailableFreeSpace, d.TotalSize,
                    d.DriveType == DriveType.Removable, isSystem, DriveStore.TryOpen(d.RootDirectory.FullName) != null));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    public static DriveCandidate? Describe(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            if (d.IsReady && string.Equals(PathUtil.NormalizeFolder(d.RootDirectory.FullName), PathUtil.NormalizeFolder(root), PathUtil.Comparison))
                return new DriveCandidate(d.RootDirectory.FullName, SafeLabel(d), d.DriveFormat, d.AvailableFreeSpace, d.TotalSize,
                    d.DriveType == DriveType.Removable, false, DriveStore.TryOpen(root) != null);
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        // A plain folder used as a destination (tests, CLI).
        if (Directory.Exists(root))
            return new DriveCandidate(root, "", "", 0, 0, false, false, DriveStore.TryOpen(root) != null);
        return null;
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Builds the reference saved in a set for a drive, creating the store on it.</summary>
    public static DriveRef Register(string driveRoot)
    {
        var store = DriveStore.OpenOrCreate(driveRoot);
        var desc = Describe(driveRoot);
        return new DriveRef
        {
            Id = store.Identity.Id,
            VolumeSerial = VolumeSerial(driveRoot),
            Label = desc?.Label ?? "",
            LastRoot = driveRoot,
        };
    }

    /// <summary>Finds the drive a set lives on, wherever it is mounted now. Null when it is not plugged in.</summary>
    public static DriveStore? Find(DriveRef drive)
    {
        if (!string.IsNullOrEmpty(drive.LastRoot) && Matches(drive, drive.LastRoot, out var hinted))
            return hinted;
        foreach (var d in ListDrives())
        {
            if (Matches(drive, d.Root, out var store))
            {
                drive.LastRoot = d.Root;
                return store;
            }
        }
        return null;
    }

    public static bool IsPresent(DriveRef drive) => Find(drive) != null;

    private static bool Matches(DriveRef drive, string root, out DriveStore? store)
    {
        store = null;
        try
        {
            var s = DriveStore.TryOpen(root);
            if (s == null) return false;
            if (!string.IsNullOrEmpty(drive.Id) && s.Identity.Id == drive.Id)
            {
                store = s;
                return true;
            }
            if (!string.IsNullOrEmpty(drive.VolumeSerial) && VolumeSerial(root) == drive.VolumeSerial)
            {
                store = s;
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>Volume serial as "1A2B-3C4D" on Windows; null elsewhere.</summary>
    public static string? VolumeSerial(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return WindowsSerial(root);
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsSerial(string root)
    {
        var path = Path.GetPathRoot(Path.GetFullPath(root));
        if (string.IsNullOrEmpty(path)) return null;
        if (!path.EndsWith('\\')) path += "\\";
        if (!GetVolumeInformationW(path, null, 0, out var serial, out _, out _, null, 0))
            return null;
        return $"{serial >> 16:X4}-{serial & 0xFFFF:X4}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, char[]? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char[]? fileSystemNameBuffer, int nFileSystemNameSize);
}
