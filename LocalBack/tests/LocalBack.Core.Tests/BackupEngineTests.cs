using LocalBack.Core.Engine;
using LocalBack.Core.Indexing;
using LocalBack.Core.Storage;

namespace LocalBack.Core.Tests;

public class BackupEngineTests
{
    [Fact]
    public async Task First_backup_stores_every_file_once()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "same");
        env.Write("b.txt", "same");
        env.Write("sub/c.txt", "other");

        var r = await env.Backup();

        Assert.Equal(3, r.Added);
        Assert.NotNull(r.Snapshot);
        Assert.Equal(SnapshotTrigger.FirstBackup, r.Snapshot!.Trigger);
        Assert.Equal(2, env.Drive.Blobs.EnumerateAll().Count()); // identical content is stored once
    }

    [Fact]
    public async Task Unchanged_set_writes_no_new_snapshot()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "one");
        await env.Backup();

        var r = await env.Backup();

        Assert.Null(r.Snapshot);
        Assert.Single(env.Engine.ListSnapshots(env.Set));
    }

    [Fact]
    public async Task Detects_added_modified_and_deleted_files()
    {
        using var env = new TestEnv();
        var a = env.Write("a.txt", "one");
        var b = env.Write("b.txt", "two");
        await env.Backup();

        env.Write("a.txt", "one changed", secondsAgo: 30);
        File.Delete(b);
        env.Write("c.txt", "three");
        var r = await env.Backup();

        Assert.Equal((1, 1, 1), (r.Added, r.Modified, r.Deleted));
        var details = env.Engine.LoadSnapshot(env.Set, r.Snapshot!.Name);
        Assert.Contains(details.Files, f => f.Name == "a.txt" && f.Change == ChangeKind.Modified);
        Assert.Contains(details.Files, f => f.Name == "b.txt" && f.Change == ChangeKind.Deleted);
        Assert.Contains(details.Files, f => f.Name == "c.txt" && f.Change == ChangeKind.Added);
    }

    [Fact]
    public async Task Touching_a_file_without_changing_content_is_not_a_version()
    {
        using var env = new TestEnv();
        var a = env.Write("a.txt", "one");
        await env.Backup();

        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddSeconds(-5));
        var r = await env.Backup();

        Assert.Null(r.Snapshot);
        Assert.Equal(File.GetLastWriteTimeUtc(a).Ticks, env.Index.LoadFiles(env.Set.Id).Values.Single().MTimeTicks);
    }

    [Fact]
    public async Task Partial_backup_only_looks_at_the_given_paths_and_keeps_the_rest()
    {
        using var env = new TestEnv();
        var a = env.Write("a.txt", "one");
        var b = env.Write("b.txt", "two");
        await env.Backup();

        env.Write("a.txt", "one changed", secondsAgo: 30);
        env.Write("b.txt", "two changed", secondsAgo: 30);
        var r = await env.Backup(new[] { a });

        Assert.Equal(1, r.Modified);
        var m = env.Drive.Set(env.Set.Id).ReadManifest(r.Snapshot!.Name);
        Assert.Equal(2, m.Entries.Count);
        var full = await env.Backup();
        Assert.Equal(1, full.Modified); // b is picked up by the next full pass
    }

    [Fact]
    public async Task Partial_backup_of_a_deleted_folder_removes_everything_under_it()
    {
        using var env = new TestEnv();
        env.Write("docs/a.txt", "1");
        env.Write("docs/deep/b.txt", "2");
        env.Write("keep.txt", "3");
        await env.Backup();

        var docs = Path.Combine(env.Source, "docs");
        Directory.Delete(docs, recursive: true);
        var r = await env.Backup(new[] { docs });

        Assert.Equal(2, r.Deleted);
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public async Task Excluded_files_and_folders_are_skipped()
    {
        using var env = new TestEnv();
        env.Write("report.docx", "x");
        env.Write("~$report.docx", "lock");
        env.Write("draft.tmp", "t");
        env.Write("node_modules/lib/index.js", "js");
        env.Write("Thumbs.db", "t");
        var changed = env.Write("project/.git/HEAD", "ref");

        var r = await env.Backup();
        Assert.Equal(1, r.Files);

        // A watcher event for a path inside an excluded folder is ignored too.
        var r2 = await env.Backup(new[] { changed });
        Assert.Null(r2.Snapshot);
    }

    [Fact]
    public async Task Missing_source_folder_does_not_look_like_everything_was_deleted()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "1");
        await env.Backup();

        Directory.Move(env.Source, env.Source + "-away");
        var r = await env.Backup();

        Assert.Equal(0, r.Deleted);
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public async Task Young_empty_files_are_deferred()
    {
        using var env = new TestEnv();
        env.Engine.YoungEmptyFileAge = TimeSpan.FromMinutes(5);
        var p = Path.Combine(env.Source, "new.txt");
        File.WriteAllText(p, "");

        var r = await env.Backup();

        Assert.Contains(p, r.Deferred);
        Assert.Equal(0, r.Files);
        Assert.Contains(p, env.Index.Pending(env.Set.Id));
    }

    [Fact]
    public async Task Restore_in_place_brings_back_old_content_and_is_undoable()
    {
        using var env = new TestEnv();
        var a = env.Write("a.txt", "original");
        var first = (await env.Backup()).Snapshot!;
        env.Write("a.txt", "edited", secondsAgo: 30);
        File.WriteAllText(Path.Combine(env.Source, "extra.txt"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(env.Source, "extra.txt"), DateTime.UtcNow.AddMinutes(-1));

        var result = await env.Engine.RestoreSnapshotAsync(env.Set, first.Name, null);

        Assert.Equal("original", File.ReadAllText(a));
        Assert.True(File.Exists(Path.Combine(env.Source, "extra.txt"))); // files not in the snapshot are left alone
        Assert.NotNull(result.UndoSnapshot);
        var undo = env.Drive.Set(env.Set.Id).ReadManifest(result.UndoSnapshot!.Name).ToMap();
        var hash = undo.Single(kv => kv.Key.Path == "a.txt").Value.Hash;
        using var s = env.Drive.Blobs.OpenRead(hash);
        Assert.Equal("edited", new StreamReader(s).ReadToEnd());
        var snaps = env.Engine.ListSnapshots(env.Set);
        Assert.Equal(SnapshotTrigger.Restore, snaps[^1].Trigger);
    }

    [Fact]
    public async Task Restore_skips_files_that_are_already_current()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "x");
        var first = (await env.Backup()).Snapshot!;

        var r = await env.Engine.RestoreSnapshotAsync(env.Set, first.Name, null);

        Assert.Equal(0, r.Restored);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public async Task Restore_to_folder_keeps_structure_and_times()
    {
        using var env = new TestEnv();
        var c = env.Write("sub/c.txt", "deep");
        var mtime = File.GetLastWriteTimeUtc(c);
        var first = (await env.Backup()).Snapshot!;
        var target = Path.Combine(env.Dir, "out");

        await env.Engine.RestoreSnapshotAsync(env.Set, first.Name, target);

        var restored = Path.Combine(target, "sub", "c.txt");
        Assert.Equal("deep", File.ReadAllText(restored));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(restored));
    }

    [Fact]
    public async Task Multi_folder_set_restores_each_folder_into_its_own_subfolder()
    {
        using var env = new TestEnv();
        var other = Path.Combine(env.Dir, "Invoices");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "inv.pdf"), "pdf");
        env.Write("a.txt", "a");
        var set = env.NewSet("Mixed", env.Source, other);
        var first = (await env.Backup(set: set)).Snapshot!;
        var target = Path.Combine(env.Dir, "out");

        await env.Engine.RestoreSnapshotAsync(set, first.Name, target);

        Assert.True(File.Exists(Path.Combine(target, "src", "a.txt")));
        Assert.True(File.Exists(Path.Combine(target, "Invoices", "inv.pdf")));
    }

    [Fact]
    public async Task Open_extracts_a_read_only_copy()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "view me");
        var first = (await env.Backup()).Snapshot!;
        var entry = env.Drive.Set(env.Set.Id).ReadManifest(first.Name).Entries.Single();

        var path = env.Engine.ExtractForViewing(env.Set, entry, "Today 14:32");

        Assert.Equal("view me", File.ReadAllText(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        env.Engine.CleanTemp();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Index_is_rebuilt_from_the_drive_when_missing()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "1");
        env.Write("b.txt", "2");
        await env.Backup();

        // A fresh PC (or a deleted index) must not re-copy or re-version anything.
        using var freshIndex = new LocalIndex(Path.Combine(env.Dir, "fresh.db"));
        var engine = new BackupEngine(freshIndex, Path.Combine(env.Dir, "t2"));
        var r = await engine.BackupAsync(env.Set, SnapshotTrigger.Manual);

        Assert.Null(r.Snapshot);
        Assert.Equal(0, r.BytesCopied);
        Assert.Equal(2, freshIndex.LoadFiles(env.Set.Id).Count);
    }

    [Fact]
    public async Task File_versions_are_tracked()
    {
        using var env = new TestEnv();
        var a = env.Write("a.txt", "v1", secondsAgo: 90);
        await env.Backup();
        env.Write("a.txt", "v2", secondsAgo: 60);
        await env.Backup();
        env.Write("a.txt", "v3", secondsAgo: 30);
        await env.Backup();

        var found = BackupEngine.Locate(new[] { env.Set }, a)!.Value;
        var versions = env.Engine.FileVersions(env.Set, found.Key);

        Assert.Equal(3, versions.Count);
        Assert.Null(versions[0].Superseded);
        Assert.All(versions.Skip(1), v => Assert.NotNull(v.Superseded));
    }

    [Fact]
    public async Task Missing_drive_throws_a_clear_error()
    {
        using var env = new TestEnv();
        env.Set.Drive.Id = "nope";
        env.Set.Drive.LastRoot = Path.Combine(env.Dir, "gone");
        env.Set.Drive.VolumeSerial = null;
        await Assert.ThrowsAsync<DriveNotAvailableException>(() => env.Backup());
    }

    [Fact]
    public async Task Snapshot_list_is_rebuilt_when_the_catalog_is_lost()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "1");
        await env.Backup();
        env.Write("a.txt", "22", secondsAgo: 10);
        await env.Backup();
        File.Delete(Path.Combine(env.Drive.Set(env.Set.Id).Dir, "snapshots.jsonl"));

        var snaps = env.Engine.ListSnapshots(env.Set);

        Assert.Equal(2, snaps.Count);
        Assert.Equal(1, snaps[1].Modified);
    }
}

public class ReviewFixTests
{
    [Fact]
    public async Task Partial_run_with_the_source_folder_away_keeps_files_and_requeues_them()
    {
        using var env = new TestEnv();
        var a = env.Write("a.txt", "1");
        env.Write("b.txt", "2");
        await env.Backup();

        Directory.Move(env.Source, env.Source + "-away");
        var r = await env.Backup(new[] { a });

        Assert.Equal(0, r.Deleted);
        Assert.Null(r.Snapshot);
        Assert.Contains(a, env.Index.Pending(env.Set.Id));

        Directory.Move(env.Source + "-away", env.Source);
        var again = await env.Backup(env.Index.Pending(env.Set.Id));
        Assert.Null(again.Snapshot); // nothing changed after all
        Assert.Empty(env.Index.Pending(env.Set.Id));
    }

    [Fact]
    public void Nested_source_folders_are_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            LocalBack.Core.Service.BackupService.NormalizeFolders(new[] { "/data/work", "/data/work/sub", "/data/other" }));
        Assert.Contains("inside", ex.Message);
        Assert.Equal(2, LocalBack.Core.Service.BackupService.NormalizeFolders(new[] { "/data/work", "/data/work2", "/data/work/" }).Count);
    }

    [Fact]
    public async Task Previewing_several_plans_at_once_matches_previewing_each()
    {
        using var env = new TestEnv();
        for (int i = 0; i < 4; i++)
        {
            env.Write("r.xlsx", $"v{i} " + new string('x', 500 * (i + 1)), secondsAgo: 400 - i * 10);
            await env.Backup();
        }
        var plans = LocalBack.Core.Model.RetentionPlan.Choices;
        var all = LocalBack.Core.Retention.RetentionPlanner.PreviewAll(env.Drive, plans, DateTimeOffset.Now);
        for (int i = 0; i < plans.Count; i++)
        {
            var one = LocalBack.Core.Retention.RetentionPlanner.Preview(env.Drive, plans[i], DateTimeOffset.Now);
            Assert.Equal(one.BytesFreed, all[i].BytesFreed);
            Assert.Equal(one.VersionsRemoved, all[i].VersionsRemoved);
            Assert.Equal(one.CurrentBytes, all[i].CurrentBytes);
        }
        Assert.Equal(1, all[0].VersionsRemoved); // keep last 3 of 4
    }

    [Fact]
    public async Task Keep_only_latest_leaves_one_snapshot_and_one_blob()
    {
        using var env = new TestEnv();
        for (int i = 0; i < 3; i++)
        {
            env.Write("doc.txt", $"version {i}", secondsAgo: 300 - i * 10);
            await env.Backup();
        }
        await LocalBack.Core.Retention.RetentionPlanner.KeepOnlyLatestAsync(env.Engine, env.Drive, env.Set.Id);

        Assert.Single(env.Engine.ListSnapshots(env.Set));
        Assert.Single(env.Drive.Blobs.EnumerateAll());
        Assert.Equal("version 2", new StreamReader(env.Drive.Blobs.OpenRead(env.Drive.Blobs.EnumerateAll().Single().Hash)).ReadToEnd());
        // The index agrees with the drive, so the next run copies nothing.
        var r = await env.Backup();
        Assert.Null(r.Snapshot);
    }

    [Fact]
    public void Engine_lock_is_exclusive_per_data_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lb-lock-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            using (var first = LocalBack.Core.Service.EngineLock.TryAcquire(dir))
            {
                Assert.NotNull(first);
                Assert.Null(LocalBack.Core.Service.EngineLock.TryAcquire(dir));
                Assert.Null(LocalBack.Core.Service.EngineLock.Acquire(dir, TimeSpan.FromMilliseconds(600)));
            }
            using var again = LocalBack.Core.Service.EngineLock.TryAcquire(dir);
            Assert.NotNull(again);
        }
        finally { Directory.Delete(dir, true); }
    }
}
