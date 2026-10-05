using LocalBack.Core.Model;
using LocalBack.Core.Scanning;
using LocalBack.Core.Util;

namespace LocalBack.Core.Watching;

/// <summary>
/// Watches the source folders of one set. Excluded names are dropped before they reach the queue,
/// so temp and lock files never cause any I/O.
/// </summary>
public sealed class SetWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Debouncer _debouncer;
    private readonly ExclusionFilter _filter;
    private readonly List<string> _roots;

    public BackupSet Set { get; }

    /// <summary>Raised when the OS event buffer overflowed and a full rescan is needed.</summary>
    public event Action<SetWatcher>? Overflow;

    public SetWatcher(BackupSet set, TimeSpan quiet, Action<BackupSet, IReadOnlyList<string>> changed)
    {
        Set = set;
        _filter = ExclusionFilter.For(set);
        _roots = set.Folders.Select(PathUtil.NormalizeFolder).ToList();
        _debouncer = new Debouncer(quiet, paths => changed(set, paths));
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                Log.Warn($"Not watching missing folder {root}");
                continue;
            }
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Changed += (_, e) => OnEvent(root, e.FullPath);
            w.Created += (_, e) => OnEvent(root, e.FullPath);
            w.Deleted += (_, e) => OnEvent(root, e.FullPath);
            w.Renamed += (_, e) =>
            {
                OnEvent(root, e.OldFullPath);
                OnEvent(root, e.FullPath);
            };
            w.Error += (_, e) =>
            {
                Log.Warn($"Watcher error on {root}: {e.GetException().Message}");
                Overflow?.Invoke(this);
            };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    public int Queued => _debouncer.Count;

    public IReadOnlyList<string> Drain() => _debouncer.Drain();

    private void OnEvent(string root, string fullPath)
    {
        var rel = PathUtil.ToManifestPath(root, fullPath);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return;
        var name = PathUtil.FileName(rel);
        // Name-only checks: no I/O for temp files, lock files and the like.
        if (_filter.ExcludeAnyParent(rel)) return;
        if (_filter.ExcludeFile(rel, 0)) return;
        if (_filter.ExcludeDirectory(rel) && Directory.Exists(fullPath)) return;
        if (name.StartsWith(".localback-", StringComparison.Ordinal)) return;
        _debouncer.Add(fullPath);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        _debouncer.Dispose();
    }
}
