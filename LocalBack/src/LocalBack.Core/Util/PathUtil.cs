namespace LocalBack.Core.Util;

/// <summary>Path helpers that respect the platform's case rules.</summary>
public static class PathUtil
{
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Full path without a trailing separator (except for drive roots).</summary>
    public static string NormalizeFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies beneath it.</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (path.Length < folder.Length || !path.StartsWith(folder, Comparison))
            return false;
        if (path.Length == folder.Length)
            return true;
        if (folder.EndsWith(Path.DirectorySeparatorChar) || folder.EndsWith(Path.AltDirectorySeparatorChar))
            return true;
        var c = path[folder.Length];
        return c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
    }

    /// <summary>Relative path in manifest form: forward slashes, no leading slash.</summary>
    public static string ToManifestPath(string folder, string fullPath)
    {
        var rel = Path.GetRelativePath(folder, fullPath);
        return rel.Replace('\\', '/');
    }

    public static string FromManifestPath(string folder, string relative) =>
        Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>File name part of a manifest path.</summary>
    public static string FileName(string manifestPath)
    {
        var i = manifestPath.LastIndexOf('/');
        return i < 0 ? manifestPath : manifestPath[(i + 1)..];
    }

    /// <summary>Makes a string safe to use as a single folder name on FAT32/exFAT/NTFS.</summary>
    public static string Slug(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var chars = name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray();
        var s = new string(chars).Trim('.', ' ', '-');
        if (s.Length > 40) s = s[..40];
        return string.IsNullOrEmpty(s) ? "set" : s;
    }
}
