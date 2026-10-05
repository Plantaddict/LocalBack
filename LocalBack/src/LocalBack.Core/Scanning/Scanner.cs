using LocalBack.Core.Util;

namespace LocalBack.Core.Scanning;

/// <summary>Metadata of a file on the PC. No content is read.</summary>
public readonly record struct FileState(int Root, string Path, long Size, long MTimeTicks, int Attributes);

/// <summary>Metadata-only walk of the source folders, with exclusions applied before any I/O on the files.</summary>
public static class Scanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static IEnumerable<FileState> Scan(int rootIndex, string root, ExclusionFilter filter, CancellationToken ct = default)
        => ScanFrom(rootIndex, root, root, filter, ct);

    /// <summary>Walks <paramref name="start"/> (a folder inside <paramref name="root"/>).</summary>
    public static IEnumerable<FileState> ScanFrom(int rootIndex, string root, string start, ExclusionFilter filter, CancellationToken ct = default)
    {
        if (!Directory.Exists(start)) yield break;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(start));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try
            {
                items = dir.EnumerateFileSystemInfos("*", Options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn($"Cannot read folder {dir.FullName}: {ex.Message}");
                continue;
            }

            foreach (var item in items)
            {
                FileAttributes attrs;
                try { attrs = item.Attributes; }
                catch (IOException) { continue; }
                if (ExclusionFilter.IsExcludedByAttributes(attrs)) continue;

                var rel = PathUtil.ToManifestPath(root, item.FullName);
                if (item is DirectoryInfo sub)
                {
                    if (!filter.ExcludeDirectory(rel)) stack.Push(sub);
                }
                else if (item is FileInfo file)
                {
                    long size;
                    DateTime mtime;
                    try
                    {
                        size = file.Length;
                        mtime = file.LastWriteTimeUtc;
                    }
                    catch (IOException) { continue; }
                    if (filter.ExcludeFile(rel, size)) continue;
                    yield return new FileState(rootIndex, rel, size, mtime.Ticks, (int)attrs);
                }
            }
        }
    }

    /// <summary>Stats a single file; null if it is gone, excluded or not a file.</summary>
    public static FileState? Stat(int rootIndex, string root, string fullPath, ExclusionFilter filter)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            var attrs = fi.Attributes;
            if ((attrs & FileAttributes.Directory) != 0 || ExclusionFilter.IsExcludedByAttributes(attrs)) return null;
            var rel = PathUtil.ToManifestPath(root, fi.FullName);
            if (filter.ExcludeAnyParent(rel) || filter.ExcludeFile(rel, fi.Length)) return null;
            return new FileState(rootIndex, rel, fi.Length, fi.LastWriteTimeUtc.Ticks, (int)attrs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
