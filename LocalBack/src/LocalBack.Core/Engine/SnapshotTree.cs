using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.Core.Engine;

/// <summary>
/// The files of one snapshot arranged as folders, so a snapshot can be browsed like a drive in Explorer.
/// Deleted entries (files the snapshot no longer has) are left out: they were not there at that moment.
/// </summary>
public sealed class SnapshotTree
{
    private SnapshotTree(SnapshotFolder top, SnapshotFolder home, Dictionary<string, SnapshotFolder> byPath)
    {
        Top = top;
        Home = home;
        _byPath = byPath;
    }

    private readonly Dictionary<string, SnapshotFolder> _byPath;

    /// <summary>The node above the set's source folders. Its children are one folder per source folder.</summary>
    public SnapshotFolder Top { get; }

    /// <summary>Where browsing starts: the only source folder, or <see cref="Top"/> when the set has several.</summary>
    public SnapshotFolder Home { get; }

    public int FileCount => Top.FileCount;
    public long Bytes => Top.Bytes;

    /// <summary>The folder at a tree path ("" for the top, "Desktop/Invoices"), or null.</summary>
    public SnapshotFolder? Find(string path)
    {
        if (path.Length == 0) return Top;
        return _byPath.TryGetValue(path.Replace('\\', '/').Trim('/'), out var f) ? f : null;
    }

    /// <summary>The folder holding a file of this snapshot, or null when the file is not in it.</summary>
    public SnapshotFolder? FolderOf(FileKey key)
    {
        var root = Top.Folders.FirstOrDefault(r => PathUtil.Comparer.Equals(r.SourceRoot, key.Root));
        if (root == null) return null;
        int slash = key.Path.LastIndexOf('/');
        return slash < 0 ? root : Find(root.Path + "/" + key.Path[..slash]);
    }

    public static SnapshotTree Build(SnapshotDetails details) => Build(details.Files, details.Roots);

    public static SnapshotTree Build(IEnumerable<SnapshotFile> files, IReadOnlyList<string> roots)
    {
        var top = new SnapshotFolder("", "", null, null);
        var byPath = new Dictionary<string, SnapshotFolder>(PathUtil.Comparer);
        var rootNodes = new Dictionary<string, SnapshotFolder>(PathUtil.Comparer);
        foreach (var r in roots) RootNode(r);

        foreach (var f in files)
        {
            if (f.Change == ChangeKind.Deleted) continue;
            var folder = RootNode(f.Key.Root);
            var path = f.Key.Path;
            int start = 0;
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', start))
            {
                folder = folder.ChildOrAdd(path[start..slash], byPath);
                start = slash + 1;
            }
            folder.Files.Add(f);
        }
        top.Finish();
        var home = top.Folders.Count == 1 ? top.Folders[0] : top;
        return new SnapshotTree(top, home, byPath);

        SnapshotFolder RootNode(string root)
        {
            if (rootNodes.TryGetValue(root, out var node)) return node;
            var name = RootName(root);
            if (top.Folders.Any(x => PathUtil.Comparer.Equals(x.Name, name))) name = root;
            var path = name.Replace('\\', '/').Trim('/');
            node = new SnapshotFolder(name, path, top, root);
            top.Folders.Add(node);
            rootNodes[root] = node;
            byPath[path] = node;
            return node;
        }
    }

    /// <summary>"Desktop" for C:\Users\me\Desktop; the whole path for a drive root like E:\.</summary>
    private static string RootName(string root)
    {
        var trimmed = root.TrimEnd('\\', '/');
        int i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var leaf = i < 0 ? trimmed : trimmed[(i + 1)..];
        return leaf.Length == 0 || leaf.EndsWith(':') ? root : leaf;
    }
}

/// <summary>A folder in a <see cref="SnapshotTree"/>: its subfolders and files, with totals over everything beneath.</summary>
public sealed class SnapshotFolder
{
    internal SnapshotFolder(string name, string path, SnapshotFolder? parent, string? sourceRoot)
    {
        Name = name;
        Path = path;
        Parent = parent;
        SourceRoot = sourceRoot;
    }

    public string Name { get; }
    /// <summary>Tree path, "/"-separated: "" for the top, "Desktop", "Desktop/Invoices".</summary>
    public string Path { get; }
    public SnapshotFolder? Parent { get; }
    /// <summary>For a source-folder node, the folder on the PC it was backed up from.</summary>
    public string? SourceRoot { get; set; }
    public bool IsTop => Parent == null;

    public List<SnapshotFolder> Folders { get; } = new();
    public List<SnapshotFile> Files { get; } = new();

    /// <summary>Files in this folder and every folder beneath it.</summary>
    public int FileCount { get; private set; }
    public long Bytes { get; private set; }
    /// <summary>Files beneath this folder that were added or modified in this snapshot.</summary>
    public int ChangedCount { get; private set; }
    /// <summary>The newest modification time of any file beneath this folder.</summary>
    public DateTime? ModifiedUtc { get; private set; }

    /// <summary>From the top down to this folder, excluding the top itself.</summary>
    public IReadOnlyList<SnapshotFolder> Ancestry
    {
        get
        {
            var list = new List<SnapshotFolder>();
            for (var f = this; f is { IsTop: false }; f = f.Parent) list.Add(f);
            list.Reverse();
            return list;
        }
    }

    /// <summary>The folder on the PC this one corresponds to, e.g. for showing where a restore goes.</summary>
    public string? FullPath
    {
        get
        {
            var ancestry = Ancestry;
            if (ancestry.Count == 0 || ancestry[0].SourceRoot is not { } root) return null;
            var rel = string.Join('/', ancestry.Skip(1).Select(a => a.Name));
            return rel.Length == 0 ? root : PathUtil.FromManifestPath(root, rel);
        }
    }

    public SnapshotFolder? Child(string name) => Folders.FirstOrDefault(f => PathUtil.Comparer.Equals(f.Name, name));

    /// <summary>Every file beneath this folder, for restoring it whole.</summary>
    public IEnumerable<SnapshotFile> AllFiles()
    {
        foreach (var f in Files) yield return f;
        foreach (var d in Folders)
            foreach (var f in d.AllFiles()) yield return f;
    }

    internal SnapshotFolder ChildOrAdd(string name, Dictionary<string, SnapshotFolder> byPath)
    {
        var path = Path.Length == 0 ? name : Path + "/" + name;
        if (byPath.TryGetValue(path, out var existing)) return existing;
        var child = new SnapshotFolder(name, path, this, null);
        Folders.Add(child);
        byPath[path] = child;
        return child;
    }

    /// <summary>Sorts and totals this folder and everything beneath it.</summary>
    internal void Finish()
    {
        // Source folders stay in the set's order; everything beneath them is in name order.
        if (!IsTop) Folders.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        Files.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        int count = Files.Count, changed = 0;
        long bytes = 0;
        DateTime? newest = null;
        foreach (var f in Files)
        {
            bytes += f.Entry.Size;
            if (f.Change != ChangeKind.Unchanged) changed++;
            if (newest == null || f.Entry.MTimeUtc > newest) newest = f.Entry.MTimeUtc;
        }
        foreach (var d in Folders)
        {
            d.Finish();
            count += d.FileCount;
            bytes += d.Bytes;
            changed += d.ChangedCount;
            if (d.ModifiedUtc is { } m && (newest == null || m > newest)) newest = m;
        }
        FileCount = count;
        Bytes = bytes;
        ChangedCount = changed;
        ModifiedUtc = newest;
    }
}
