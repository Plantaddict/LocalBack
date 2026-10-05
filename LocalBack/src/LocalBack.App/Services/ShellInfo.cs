using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalBack.App.Localization;

namespace LocalBack.App.Services;

/// <summary>
/// File-type names and icons as Explorer shows them, from the Windows shell, looked up by extension only
/// (the files themselves are on the backup drive as blobs). Cached per extension for the life of the process.
/// </summary>
public static class ShellInfo
{
    private const string FolderKey = "\\folder";
    private static readonly Dictionary<string, (ImageSource? Icon, string Type)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Icon(string fileName, bool folder) => Lookup(fileName, folder).Icon;
    public static string TypeName(string fileName, bool folder) => Lookup(fileName, folder).Type;

    private static (ImageSource? Icon, string Type) Lookup(string fileName, bool folder)
    {
        var ext = folder ? FolderKey : Path.GetExtension(fileName);
        lock (Cache)
        {
            if (Cache.TryGetValue(ext, out var hit)) return hit;
        }
        var info = Query(ext, folder);
        lock (Cache) Cache[ext] = info;
        return info;
    }

    private static (ImageSource? Icon, string Type) Query(string ext, bool folder)
    {
        var fallback = folder ? Loc.T("browse.type.folder")
            : ext.Length > 1 ? Loc.T("browse.type.file", ext[1..].ToUpperInvariant()) : Loc.T("browse.type.plainFile");
        if (!OperatingSystem.IsWindows()) return (null, fallback);
        try
        {
            var shfi = new SHFILEINFO();
            uint attrs = folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            // A made-up name with the right extension: the shell answers from the registry, the file need not exist.
            var probe = folder ? "folder" : "file" + ext;
            var r = SHGetFileInfo(probe, attrs, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_USEFILEATTRIBUTES | SHGFI_ICON | SHGFI_LARGEICON | SHGFI_TYPENAME);
            if (r == IntPtr.Zero) return (null, fallback);
            ImageSource? icon = null;
            if (shfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    icon = bmp;
                }
                finally
                {
                    DestroyIcon(shfi.hIcon);
                }
            }
            var type = string.IsNullOrWhiteSpace(shfi.szTypeName) ? fallback : shfi.szTypeName;
            return (icon, type);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return (null, fallback);
        }
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
