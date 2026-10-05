namespace LocalBack.Core.Storage;

public enum ChangeKind { Unchanged, Added, Modified, Deleted }

public readonly record struct SnapshotDiff(int Added, int Modified, int Deleted)
{
    public int Total => Added + Modified + Deleted;

    public static SnapshotDiff Compute(Manifest? prev, Manifest cur)
    {
        var curMap = cur.ToMap();
        if (prev == null) return new SnapshotDiff(curMap.Count, 0, 0);
        var prevMap = prev.ToMap();
        int added = 0, modified = 0, deleted = 0;
        foreach (var (key, e) in curMap)
        {
            if (!prevMap.TryGetValue(key, out var p)) added++;
            else if (p.Hash != e.Hash) modified++;
        }
        foreach (var key in prevMap.Keys)
            if (!curMap.ContainsKey(key)) deleted++;
        return new SnapshotDiff(added, modified, deleted);
    }

    public static SnapshotInfo ToInfo(string name, Manifest m, SnapshotDiff d) => new()
    {
        Name = name,
        CreatedUtc = m.CreatedUtc,
        Trigger = m.Trigger,
        Files = m.Entries.Count,
        Bytes = m.Entries.Sum(e => e.Size),
        Added = d.Added,
        Modified = d.Modified,
        Deleted = d.Deleted,
    };
}
