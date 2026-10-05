using System.Diagnostics;
using System.Text.Json;
using LocalBack.Core.Drives;
using LocalBack.Core.Indexing;
using LocalBack.Core.Model;
using LocalBack.Core.Scanning;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.Core.Engine;

/// <summary>
/// Backup, restore and history for backup sets. One operation at a time per process:
/// runs, restores and pruning all go through <see cref="Gate"/> so the garbage collector never
/// races a backup that is adding blobs.
/// </summary>
public sealed class BackupEngine
{
    private readonly LocalIndex _index;
    private readonly string _tempRoot;
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Files newer than this with zero bytes are probably still being created.</summary>
    public TimeSpan YoungEmptyFileAge { get; set; } = TimeSpan.FromSeconds(5);

    public BackupEngine(LocalIndex index, string tempRoot)
    {
        _index = index;
        _tempRoot = tempRoot;
    }

    public LocalIndex Index => _index;

    public DriveStore RequireDrive(BackupSet set) =>
        DriveLocator.Find(set.Drive) ?? throw new DriveNotAvailableException(set.Name, string.IsNullOrEmpty(set.Drive.Label) ? set.Drive.LastRoot : set.Drive.Label);

    // ------------------------------------------------------------------ backup

    /// <summary>
    /// Backs up a set. With <paramref name="changedPaths"/> only those paths (files or folders) are looked at;
    /// otherwise every source folder is walked (metadata only; content is read only for files whose size or time changed).
    /// </summary>
    public async Task<BackupResult> BackupAsync(BackupSet set, SnapshotTrigger trigger, IReadOnlyCollection<string>? changedPaths = null,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var drive = RequireDrive(set);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Backup(drive, set, trigger, changedPaths, progress, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private BackupResult Backup(DriveStore drive, BackupSet set, SnapshotTrigger trigger, IReadOnlyCollection<string>? changedPaths,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var setStore = drive.Set(set.Id);
        setStore.SaveDefinition(set);
        drive.Blobs.CleanTemp();
        SyncIndex(set.Id, setStore);

        var roots = set.Folders.Select(PathUtil.NormalizeFolder).Distinct(PathUtil.Comparer).ToList();
        var filter = ExclusionFilter.For(set);
        var current = _index.LoadFiles(set.Id);
        bool isFirst = setStore.LatestManifestName() == null;
        bool full = changedPaths == null || isFirst;

        // 1. Work out what the set looks like now (metadata only).
        progress?.Report(new BackupProgress("Checking for changes", 0, 0, 0, null));
        var now = new Dictionary<FileKey, FileState>(FileKey.Comparer);
        var rootSet = new HashSet<string>(roots, PathUtil.Comparer);
        var unavailable = new List<string>();
        if (full)
        {
            for (int i = 0; i < roots.Count; i++)
            {
                if (!Directory.Exists(roots[i]))
                {
                    // A source folder that is temporarily missing (unmounted drive, network) must not look like
                    // "everything deleted": keep what we had.
                    foreach (var (k, v) in current.Where(c => PathUtil.Comparer.Equals(c.Key.Root, roots[i])))
                        now[k] = new FileState(i, k.Path, v.Size, v.MTimeTicks, v.Attributes);
                    Log.Warn($"Source folder missing, keeping previous copy: {roots[i]}");
                    continue;
                }
                foreach (var f in Scanner.Scan(i, roots[i], filter, ct))
                    now[new FileKey(roots[i], f.Path)] = f;
            }
        }
        else
        {
            foreach (var (k, v) in current)
            {
                if (rootSet.Contains(k.Root))
                    now[k] = new FileState(roots.FindIndex(r => PathUtil.Comparer.Equals(r, k.Root)), k.Path, v.Size, v.MTimeTicks, v.Attributes);
            }
            foreach (var raw in changedPaths!.Distinct(PathUtil.Comparer))
            {
                var path = Path.GetFullPath(raw);
                int ri = roots.FindIndex(r => PathUtil.IsUnder(path, r));
                if (ri < 0) continue;
                var root = roots[ri];
                if (!Directory.Exists(root))
                {
                    // Same rule as the full scan: a source folder that is away is not "everything deleted".
                    unavailable.Add(path);
                    continue;
                }
                var rel = PathUtil.ToManifestPath(root, path);
                bool isRoot = rel == ".";

                // Drop what we knew at or under this path, then look again.
                if (!isRoot)
                {
                    var prefix = rel + "/";
                    foreach (var k in now.Keys.Where(k => PathUtil.Comparer.Equals(k.Root, root) &&
                                 (PathUtil.Comparer.Equals(k.Path, rel) || k.Path.StartsWith(prefix, PathUtil.Comparison))).ToList())
                        now.Remove(k);
                }
                else
                {
                    foreach (var k in now.Keys.Where(k => PathUtil.Comparer.Equals(k.Root, root)).ToList()) now.Remove(k);
                }

                if (Directory.Exists(path))
                {
                    if (!isRoot && filter.ExcludeAnyParent(rel + "/x")) continue;
                    foreach (var f in Scanner.ScanFrom(ri, root, path, filter, ct))
                        now[new FileKey(root, f.Path)] = f;
                }
                else if (Scanner.Stat(ri, root, path, filter) is { } st)
                {
                    now[new FileKey(root, st.Path)] = st;
                }
            }
        }

        if (unavailable.Count > 0) Log.Warn($"Source folder missing, keeping previous copy of {unavailable.Count} changed paths");

        // 2. Copy new content to the drive.
        var toStore = now.Where(kv => !current.TryGetValue(kv.Key, out var old) || old.Size != kv.Value.Size || old.MTimeTicks != kv.Value.MTimeTicks).ToList();
        var hashes = new Dictionary<FileKey, string>(FileKey.Comparer);
        var deferred = new List<string>();
        var failed = new List<string>();
        long copied = 0;
        int done = 0;
        var youngCutoff = DateTime.UtcNow - YoungEmptyFileAge;
        foreach (var (key, st) in toStore)
        {
            ct.ThrowIfCancellationRequested();
            var full_ = key.FullPath;
            progress?.Report(new BackupProgress("Copying", done++, toStore.Count, copied, full_));
            if (st.Size == 0 && new DateTime(st.MTimeTicks, DateTimeKind.Utc) > youngCutoff)
            {
                Defer(key, full_);
                continue;
            }
            try
            {
                using var src = new FileStream(full_, FileMode.Open, FileAccess.Read, FileShare.Read, Hashing.BufferSize, FileOptions.SequentialScan);
                // While we hold the handle nobody can write, so hashing then copying sees one consistent content.
                var hash = Hashing.Sha256(src);
                if (!drive.Blobs.Exists(hash))
                {
                    src.Position = 0;
                    hash = drive.Blobs.Put(src, ct);
                    copied += st.Size;
                }
                hashes[key] = hash;
                // Size/time may have moved between the scan and opening; use what we actually stored.
                var fi = new FileInfo(full_);
                if (fi.Length != st.Size || fi.LastWriteTimeUtc.Ticks != st.MTimeTicks)
                    now[key] = st with { Size = fi.Length, MTimeTicks = fi.LastWriteTimeUtc.Ticks };
            }
            catch (FileNotFoundException) { now.Remove(key); }
            catch (DirectoryNotFoundException) { now.Remove(key); }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                Defer(key, full_);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive problems are fatal for the run; a source file problem is not.
                if (!Directory.Exists(drive.Root)) throw;
                Log.Warn($"Could not back up {full_}: {ex.Message}");
                failed.Add(full_);
                KeepOld(key);
            }
        }

        void Defer(FileKey key, string path)
        {
            deferred.Add(path);
            KeepOld(key);
        }

        void KeepOld(FileKey key)
        {
            if (current.TryGetValue(key, out var old))
                now[key] = new FileState(now[key].Root, key.Path, old.Size, old.MTimeTicks, old.Attributes);
            else
                now.Remove(key);
        }

        // 3. Diff against the previous snapshot.
        var changes = new List<LocalIndex.Change>();
        int added = 0, modified = 0, deleted = 0;
        foreach (var (key, st) in now)
        {
            current.TryGetValue(key, out var old);
            var hash = hashes.TryGetValue(key, out var h) ? h : old!.Hash;
            var entry = new IndexedFile(st.Size, st.MTimeTicks, st.Attributes, hash);
            if (old == null) { added++; changes.Add(new(key, null, entry)); }
            else if (old.Hash != hash) { modified++; changes.Add(new(key, old, entry)); }
            else if (old != entry) changes.Add(new(key, old, entry));
        }
        foreach (var (key, old) in current)
        {
            if (!now.ContainsKey(key))
            {
                deleted++;
                changes.Add(new(key, old, null));
            }
        }

        // 4. Write the snapshot (only if content changed, or this is the first one).
        SnapshotInfo? info = null;
        bool contentChanged = added + modified + deleted > 0 || isFirst;
        if (contentChanged)
        {
            var manifest = new Manifest
            {
                SetId = set.Id,
                CreatedUtc = started,
                Trigger = isFirst ? SnapshotTrigger.FirstBackup : trigger,
                Roots = roots,
                Entries = now
                    .OrderBy(kv => kv.Value.Root).ThenBy(kv => kv.Key.Path, StringComparer.Ordinal)
                    .Select(kv =>
                    {
                        var h = hashes.TryGetValue(kv.Key, out var x) ? x : current[kv.Key].Hash;
                        return new ManifestEntry(kv.Value.Root, kv.Key.Path, h, kv.Value.Size, kv.Value.MTimeTicks, kv.Value.Attributes);
                    })
                    .ToList(),
            };
            info = setStore.AddSnapshot(manifest, new SnapshotDiff(added, modified, deleted));
        }
        _index.Commit(set.Id, info?.Name, started, changes);
        _index.RecordRun(set.Id, started, full, null);

        // Changes queued while the drive was away are covered by a full pass, or by the paths we just handled.
        if (full) _index.ClearPending(set.Id);
        else _index.ClearPending(set.Id, changedPaths!.Select(Path.GetFullPath).Except(deferred.Concat(unavailable), PathUtil.Comparer));

        if (deferred.Count > 0)
            _index.AddPending(set.Id, deferred, started);
        if (unavailable.Count > 0)
            _index.AddPending(set.Id, unavailable, started);

        progress?.Report(new BackupProgress("Done", toStore.Count, toStore.Count, copied, null));
        Log.Info($"Backup {set.Name}: +{added} ~{modified} -{deleted}, {Format.Size(copied)} copied, {deferred.Count} deferred ({trigger}{(full ? ", full" : "")})");

        return new BackupResult
        {
            Snapshot = info,
            Added = added,
            Modified = modified,
            Deleted = deleted,
            Files = now.Count,
            TotalBytes = now.Values.Sum(v => v.Size),
            BytesCopied = copied,
            Deferred = deferred,
            Failed = failed,
            FullScan = full,
        };
    }

    private static bool IsSharingViolation(IOException ex)
    {
        // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33)
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    /// <summary>Makes sure the local index describes the newest snapshot on the drive.</summary>
    private void SyncIndex(string setId, SetStore setStore)
    {
        var latest = setStore.LatestManifestName();
        var state = _index.GetState(setId);
        if (state.LastManifest == latest) return;
        Log.Info($"Index for {setId} is out of date ({state.LastManifest ?? "none"} vs {latest ?? "none"}); rebuilding from drive");
        _index.Rebuild(setId, ReadAll(setStore));
    }

    internal static IEnumerable<(string, Manifest)> ReadAll(SetStore setStore)
    {
        foreach (var name in setStore.ManifestNames())
        {
            Manifest m;
            try { m = setStore.ReadManifest(name); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                Log.Warn($"Skipping unreadable manifest {setStore.Id}/{name}: {ex.Message}");
                continue;
            }
            yield return (name, m);
        }
    }

    // ------------------------------------------------------------------ history

    public List<SnapshotInfo> ListSnapshots(BackupSet set)
    {
        var drive = RequireDrive(set);
        return drive.Set(set.Id).Snapshots();
    }

    /// <summary>Files in a snapshot, with what changed compared to the snapshot before it.</summary>
    public SnapshotDetails LoadSnapshot(BackupSet set, string snapshotName)
    {
        var setStore = RequireDrive(set).Set(set.Id);
        var names = setStore.ManifestNames();
        int i = names.IndexOf(snapshotName);
        if (i < 0) throw new FileNotFoundException($"Snapshot {snapshotName} no longer exists.");
        var cur = setStore.ReadManifest(snapshotName);
        var prev = i > 0 ? setStore.ReadManifest(names[i - 1]) : null;
        var curMap = cur.ToMap();
        var prevMap = prev?.ToMap();
        var files = new List<SnapshotFile>(curMap.Count);
        foreach (var (key, e) in curMap)
        {
            var change = prevMap == null || !prevMap.TryGetValue(key, out var p) ? ChangeKind.Added
                : p.Hash != e.Hash ? ChangeKind.Modified : ChangeKind.Unchanged;
            files.Add(new SnapshotFile(key, e, change));
        }
        if (prevMap != null)
        {
            foreach (var (key, e) in prevMap)
                if (!curMap.ContainsKey(key))
                    files.Add(new SnapshotFile(key, e, ChangeKind.Deleted));
        }
        files.Sort((a, b) =>
        {
            int c = Rank(a.Change).CompareTo(Rank(b.Change));
            return c != 0 ? c : string.Compare(a.Key.Path, b.Key.Path, StringComparison.OrdinalIgnoreCase);
        });
        var info = SnapshotDiff.ToInfo(snapshotName, cur, SnapshotDiff.Compute(prev, cur));
        return new SnapshotDetails(info, files, cur.Roots);

        static int Rank(ChangeKind k) => k == ChangeKind.Unchanged ? 1 : 0;
    }

    public List<FileVersion> FileVersions(BackupSet set, FileKey key) => _index.Versions(set.Id, key);

    /// <summary>Finds the set and key for a path on the PC (used by the Explorer menu).</summary>
    public static (BackupSet Set, FileKey Key)? Locate(IEnumerable<BackupSet> sets, string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        foreach (var set in sets)
        {
            foreach (var f in set.Folders)
            {
                var root = PathUtil.NormalizeFolder(f);
                if (PathUtil.IsUnder(fullPath, root))
                    return (set, new FileKey(root, PathUtil.ToManifestPath(root, fullPath)));
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Puts files back. With <paramref name="targetFolder"/> null they go to their original place, after the
    /// current versions are backed up so the restore can be undone; otherwise they are copied under the folder.
    /// Files that exist now but are not in the snapshot are left alone.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(BackupSet set, IReadOnlyCollection<(FileKey Key, ManifestEntry Entry)> files, string? targetFolder,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var drive = RequireDrive(set);
        SnapshotInfo? undo = null;
        if (targetFolder == null)
        {
            // Capture what is about to be overwritten.
            var touched = files.Select(f => f.Key.FullPath).Where(File.Exists).ToList();
            if (touched.Count > 0 && set.Folders.Count > 0)
            {
                var pre = await BackupAsync(set, SnapshotTrigger.BeforeRestore, touched, progress, ct).ConfigureAwait(false);
                undo = pre.Snapshot ?? drive.Set(set.Id).Snapshots().LastOrDefault();
            }
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        int restored = 0, skipped = 0;
        var failed = new List<string>();
        var restoredPaths = new List<string>();
        try
        {
            var rootNames = RootFolderNames(files.Select(f => f.Key.Root).Distinct(PathUtil.Comparer).ToList());
            int done = 0;
            foreach (var (key, entry) in files)
            {
                ct.ThrowIfCancellationRequested();
                var dest = targetFolder == null
                    ? key.FullPath
                    : PathUtil.FromManifestPath(rootNames.Count > 1 ? Path.Combine(targetFolder, rootNames[key.Root]) : targetFolder, key.Path);
                progress?.Report(new BackupProgress("Restoring", done++, files.Count, 0, dest));
                try
                {
                    if (IsAlreadyThere(set, key, entry, dest))
                    {
                        skipped++;
                        continue;
                    }
                    WriteBlobTo(drive, entry, dest, ct);
                    restored++;
                    restoredPaths.Add(dest);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (!Directory.Exists(drive.Root)) throw;
                    Log.Warn($"Could not restore {dest}: {ex.Message}");
                    failed.Add(dest);
                }
            }
        }
        finally
        {
            Gate.Release();
        }

        // Record the restored state as its own snapshot, so history shows what happened.
        if (targetFolder == null && restoredPaths.Count > 0)
        {
            try { await BackupAsync(set, SnapshotTrigger.Restore, restoredPaths, null, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or DriveNotAvailableException) { Log.Warn($"Post-restore snapshot failed: {ex.Message}"); }
        }
        Log.Info($"Restore {set.Name}: {restored} restored, {skipped} already current, {failed.Count} failed{(targetFolder != null ? " to " + targetFolder : "")}");
        return new RestoreResult { Restored = restored, Skipped = skipped, Failed = failed, UndoSnapshot = undo };
    }

    /// <summary>Restores every file of a snapshot.</summary>
    public Task<RestoreResult> RestoreSnapshotAsync(BackupSet set, string snapshotName, string? targetFolder,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var m = RequireDrive(set).Set(set.Id).ReadManifest(snapshotName);
        var files = m.ToMap().Select(kv => (kv.Key, kv.Value)).ToList();
        return RestoreAsync(set, files, targetFolder, progress, ct);
    }

    private bool IsAlreadyThere(BackupSet set, FileKey key, ManifestEntry entry, string dest)
    {
        var fi = new FileInfo(dest);
        if (!fi.Exists || fi.Length != entry.Size) return false;
        // Cheap check first: the index knows the hash for this size+time.
        var idx = _index.GetFile(set.Id, key);
        if (idx != null && PathUtil.Comparer.Equals(key.FullPath, dest) && idx.Size == fi.Length && idx.MTimeTicks == fi.LastWriteTimeUtc.Ticks)
            return idx.Hash == entry.Hash;
        try
        {
            using var s = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.Read, Hashing.BufferSize, FileOptions.SequentialScan);
            return Hashing.Sha256(s) == entry.Hash;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void WriteBlobTo(DriveStore drive, ManifestEntry entry, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = Path.Combine(Path.GetDirectoryName(dest)!, $".localback-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var src = drive.Blobs.OpenRead(entry.Hash))
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, Hashing.BufferSize))
            {
                var buffer = new byte[Hashing.BufferSize];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                }
            }
            File.SetLastWriteTimeUtc(tmp, entry.MTimeUtc);
            if (File.Exists(dest))
            {
                var attrs = File.GetAttributes(dest);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
            }
            File.Move(tmp, dest, overwrite: true);
            var keep = (FileAttributes)entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
            if (OperatingSystem.IsWindows() && keep != 0) File.SetAttributes(dest, keep);
        }
        catch
        {
            AtomicFile.TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Distinct folder names for each source folder, used when restoring a multi-folder set elsewhere.</summary>
    private static Dictionary<string, string> RootFolderNames(List<string> roots)
    {
        var map = new Dictionary<string, string>(PathUtil.Comparer);
        var used = new HashSet<string>(PathUtil.Comparer);
        foreach (var r in roots)
        {
            var name = PathUtil.Slug(Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : r);
            var candidate = name;
            for (int i = 2; !used.Add(candidate); i++) candidate = $"{name} ({i})";
            map[r] = candidate;
        }
        return map;
    }

    // ------------------------------------------------------------------ open old version

    /// <summary>Copies one version to %TEMP%\LocalBack\&lt;snapshot&gt;\&lt;name&gt;, read-only, and returns the path.</summary>
    public string ExtractForViewing(BackupSet set, ManifestEntry entry, string label)
    {
        var drive = RequireDrive(set);
        var dir = Path.Combine(_tempRoot, PathUtil.Slug(label), entry.Hash[..8]);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, PathUtil.FileName(entry.Path));
        if (!File.Exists(dest))
        {
            WriteBlobTo(drive, entry with { Attributes = 0 }, dest, CancellationToken.None);
            File.SetAttributes(dest, FileAttributes.ReadOnly);
        }
        return dest;
    }

    /// <summary>Removes the read-only copies made for viewing.</summary>
    public void CleanTemp()
    {
        if (!Directory.Exists(_tempRoot)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                AtomicFile.TryDelete(f);
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex.Message);
        }
    }
}
