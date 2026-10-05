using System.Globalization;
using LocalBack.Core.Engine;
using LocalBack.Core.Model;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.Core.Retention;

public sealed record Offender(string SetId, string Path, int Versions, long Bytes)
{
    public string Name => PathUtil.FileName(Path);
}

/// <summary>What a retention plan would do on one drive. The byte count is exact: it is the same walk as pruning, without the deletes.</summary>
public sealed class PrunePreview
{
    public RetentionPlan Plan { get; init; } = RetentionPlan.Default;
    public long BytesFreed { get; init; }
    public int VersionsRemoved { get; init; }
    /// <summary>Space used by the newest snapshot of every set.</summary>
    public long CurrentBytes { get; init; }
    /// <summary>Space used only by older versions.</summary>
    public long OlderBytes { get; init; }
    public IReadOnlyList<Offender> Offenders { get; init; } = Array.Empty<Offender>();
    internal Dictionary<string, List<Drop>> Drops { get; init; } = new();
}

/// <summary>
/// Thins old versions. A "version" is a run of consecutive snapshots in which a file had the same content.
/// Pruning removes dropped versions from the manifests, then deletes blobs no manifest references.
/// The newest version of every file is always kept. Safe to interrupt: manifests are rewritten atomically
/// and blobs are only deleted after every manifest has been re-read.
/// </summary>
internal sealed record Drop(FileKey Key, string Hash, string FirstManifest, string LastManifest);

public static class RetentionPlanner
{
    internal sealed class Version
    {
        public required string Hash;
        public required long Size;
        public required int First;
        public int Last;
    }

    public static PrunePreview Preview(DriveStore drive, RetentionPlan plan, DateTimeOffset now, IReadOnlyCollection<string>? onlySets = null, CancellationToken ct = default)
        => PreviewAll(drive, new[] { plan }, now, onlySets, ct)[0];

    /// <summary>Previews several plans with one walk of the manifests and blobs (the Free up space dialog shows three).</summary>
    public static List<PrunePreview> PreviewAll(DriveStore drive, IReadOnlyList<RetentionPlan> plans, DateTimeOffset now, IReadOnlyCollection<string>? onlySets = null, CancellationToken ct = default)
    {
        int n = plans.Count;
        var drops = new Dictionary<string, List<Drop>>[n];
        var keptHashes = new HashSet<string>[n];
        var removed = new int[n];
        for (int p = 0; p < n; p++)
        {
            drops[p] = new Dictionary<string, List<Drop>>();
            keptHashes[p] = new HashSet<string>(StringComparer.Ordinal);
        }
        var currentHashes = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<Offender>();

        foreach (var setStore in drive.Sets())
        {
            ct.ThrowIfCancellationRequested();
            var names = setStore.ManifestNames();
            if (names.Count == 0) continue;
            var times = new List<DateTimeOffset>(names.Count);
            var read = new List<string>(names.Count);
            var history = new Dictionary<FileKey, List<Version>>(FileKey.Comparer);
            int idx = 0;
            foreach (var (name, m) in BackupEngine.ReadAll(setStore))
            {
                times.Add(m.CreatedUtc);
                read.Add(name);
                foreach (var (key, e) in m.ToMap())
                {
                    if (!history.TryGetValue(key, out var list)) history[key] = list = new List<Version>();
                    var last = list.Count > 0 ? list[^1] : null;
                    if (last != null && last.Hash == e.Hash && last.Last == idx - 1) last.Last = idx;
                    else list.Add(new Version { Hash = e.Hash, Size = e.Size, First = idx, Last = idx });
                }
                idx++;
            }
            int lastIdx = idx - 1;
            bool applies = onlySets == null || onlySets.Contains(setStore.Id);
            var setDrops = new List<Drop>[n];
            for (int p = 0; p < n; p++) setDrops[p] = new List<Drop>();

            foreach (var (key, versions) in history)
            {
                long bytes = 0;
                var distinct = new HashSet<string>();
                foreach (var v in versions)
                {
                    if (distinct.Add(v.Hash)) bytes += v.Size;
                    if (v.Last == lastIdx) currentHashes.Add(v.Hash);
                }
                if (versions.Count > 1) offenders.Add(new Offender(setStore.Id, key.Path, versions.Count, bytes));

                for (int p = 0; p < n; p++)
                {
                    var keep = applies ? Decide(plans[p], versions, times, now) : null;
                    for (int i = 0; i < versions.Count; i++)
                    {
                        var v = versions[i];
                        if (keep == null || keep[i]) keptHashes[p].Add(v.Hash);
                        else { setDrops[p].Add(new Drop(key, v.Hash, read[v.First], read[v.Last])); removed[p]++; }
                    }
                }
            }
            for (int p = 0; p < n; p++)
                if (setDrops[p].Count > 0) drops[p][setStore.Id] = setDrops[p];
        }

        var freed = new long[n];
        long current = 0, older = 0;
        foreach (var (hash, size) in drive.Blobs.EnumerateAll())
        {
            for (int p = 0; p < n; p++)
                if (!keptHashes[p].Contains(hash)) freed[p] += size;
            if (currentHashes.Contains(hash)) current += size;
            else older += size;
        }

        var top = offenders.OrderByDescending(o => o.Bytes).Take(5).ToList();
        var result = new List<PrunePreview>(n);
        for (int p = 0; p < n; p++)
        {
            result.Add(new PrunePreview
            {
                Plan = plans[p],
                BytesFreed = freed[p],
                VersionsRemoved = removed[p],
                CurrentBytes = current,
                OlderBytes = older,
                Offenders = top,
                Drops = drops[p],
            });
        }
        return result;
    }

    /// <summary>
    /// For a set without version history: drops every snapshot but the newest, then collects garbage.
    /// One pass over the drive's manifests (for the GC) instead of a full preview plus apply.
    /// </summary>
    public static async Task KeepOnlyLatestAsync(BackupEngine engine, DriveStore drive, string setId, CancellationToken ct = default)
    {
        await engine.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var setStore = drive.Set(setId);
            var names = setStore.ManifestNames();
            if (names.Count <= 1) return;
            foreach (var name in names.Take(names.Count - 1)) setStore.DeleteManifest(name);
            setStore.RebuildCatalog();
            engine.Index.Rebuild(setId, BackupEngine.ReadAll(setStore));
            CollectGarbage(drive, ct);
        }
        finally
        {
            engine.Gate.Release();
        }
    }

    /// <summary>Which versions to keep, oldest first. The last one (the newest) is always kept.</summary>
    internal static bool[] Decide(RetentionPlan plan, List<Version> versions, List<DateTimeOffset> times, DateTimeOffset now)
    {
        var keep = new bool[versions.Count];
        keep[^1] = true;
        switch (plan.Kind)
        {
            case RetentionKind.KeepLast:
                for (int i = 0; i < versions.Count; i++)
                    if (i >= versions.Count - Math.Max(1, plan.Count)) keep[i] = true;
                break;

            case RetentionKind.DailyThenWeekly:
            {
                // Newest version in each bucket survives: a day for the last 30 days, a week before that.
                var seen = new HashSet<string>();
                for (int i = versions.Count - 1; i >= 0; i--)
                {
                    var t = times[versions[i].First].ToLocalTime();
                    string bucket = now - t <= TimeSpan.FromDays(30)
                        ? "d" + t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : $"w{ISOWeek.GetYear(t.DateTime)}-{ISOWeek.GetWeekOfYear(t.DateTime)}";
                    if (seen.Add(bucket)) keep[i] = true;
                }
                break;
            }

            case RetentionKind.OlderThan:
            {
                var cutoff = now - TimeSpan.FromDays(Math.Max(1, plan.Days));
                for (int i = 0; i < versions.Count - 1; i++)
                {
                    // A version stopped being current when the next snapshot no longer had it.
                    int after = versions[i].Last + 1;
                    var replaced = after < times.Count ? times[after] : now;
                    keep[i] = replaced >= cutoff;
                }
                break;
            }
        }
        return keep;
    }

    /// <summary>Applies a preview: rewrites manifests, drops snapshots that became empty duplicates, then collects garbage.</summary>
    public static async Task<long> ApplyAsync(BackupEngine engine, DriveStore drive, PrunePreview preview, CancellationToken ct = default)
    {
        await engine.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = drive.Space().Free;
            foreach (var (setId, drops) in preview.Drops)
            {
                ct.ThrowIfCancellationRequested();
                PruneSet(drive.Set(setId), drops, ct);
                // Index rebuild needs every manifest again; cheap compared to the copy work.
                engine.Index.Rebuild(setId, BackupEngine.ReadAll(drive.Set(setId)));
            }
            var freed = CollectGarbage(drive, ct);
            Log.Info($"Pruned {preview.VersionsRemoved} versions with \"{preview.Plan.Title}\"; {Format.Size(freed)} freed");
            var after = drive.Space().Free;
            return after > before ? after - before : freed;
        }
        finally
        {
            engine.Gate.Release();
        }
    }

    private static void PruneSet(SetStore setStore, List<Drop> drops, CancellationToken ct)
    {
        var names = setStore.ManifestNames();
        var byKey = drops.GroupBy(d => d.Key, FileKey.Comparer).ToDictionary(g => g.Key, g => g.ToList(), FileKey.Comparer);
        var keptPrev = (Dictionary<FileKey, string>?)null;
        for (int i = 0; i < names.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = names[i];
            var m = setStore.ReadManifest(name);
            int before = m.Entries.Count;
            m.Entries = m.Entries.Where(e => !IsDropped(e)).ToList();

            bool IsDropped(ManifestEntry e)
            {
                if (e.Root < 0 || e.Root >= m.Roots.Count) return false;
                if (!byKey.TryGetValue(new FileKey(m.Roots[e.Root], e.Path), out var list)) return false;
                foreach (var d in list)
                {
                    if (d.Hash == e.Hash && string.CompareOrdinal(name, d.FirstManifest) >= 0 && string.CompareOrdinal(name, d.LastManifest) <= 0)
                        return true;
                }
                return false;
            }

            var map = m.Entries.Where(e => e.Root >= 0 && e.Root < m.Roots.Count)
                .ToDictionary(e => new FileKey(m.Roots[e.Root], e.Path), e => e.Hash, FileKey.Comparer);
            bool isLatest = i == names.Count - 1;
            // A snapshot that lost everything, or now equals the one before it, tells nothing: drop it.
            if (!isLatest && (map.Count == 0 || keptPrev != null && SameContent(keptPrev, map)))
            {
                setStore.DeleteManifest(names[i]);
                continue;
            }
            if (m.Entries.Count != before) setStore.ReplaceManifest(names[i], m);
            keptPrev = map;
        }
        setStore.RebuildCatalog();
    }

    private static bool SameContent(Dictionary<FileKey, string> a, Dictionary<FileKey, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, h) in a)
            if (!b.TryGetValue(k, out var h2) || h2 != h) return false;
        return true;
    }

    /// <summary>Deletes blobs that no manifest of any set on the drive refers to. Returns bytes deleted.</summary>
    public static long CollectGarbage(DriveStore drive, CancellationToken ct = default)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setStore in drive.Sets())
        {
            foreach (var name in setStore.ManifestNames())
            {
                ct.ThrowIfCancellationRequested();
                // An unreadable manifest means we cannot know what is safe to delete.
                var m = setStore.ReadManifest(name);
                foreach (var e in m.Entries) referenced.Add(e.Hash);
            }
        }
        long freed = 0;
        foreach (var (hash, size) in drive.Blobs.EnumerateAll().ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (referenced.Contains(hash)) continue;
            drive.Blobs.Delete(hash);
            freed += size;
        }
        drive.Blobs.CleanTemp();
        return freed;
    }
}
