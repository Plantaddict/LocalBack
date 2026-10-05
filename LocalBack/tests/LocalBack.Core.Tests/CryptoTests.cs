using System.Security.Cryptography;
using LocalBack.Core.Crypto;
using LocalBack.Core.Drives;
using LocalBack.Core.Engine;
using LocalBack.Core.Model;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;

namespace LocalBack.Core.Tests;

public class ChunkedAesGcmTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(1024 * 1024 + 1)]
    [InlineData(3 * 1024 * 1024 + 12345)]
    public void Round_trips_any_size(int size)
    {
        var plain = new byte[size];
        RandomNumberGenerator.Fill(plain);
        var container = ChunkedAesGcm.Encrypt(plain, Key);
        Assert.True(ChunkedAesGcm.LooksEncrypted(container));
        Assert.Equal(plain, ChunkedAesGcm.Decrypt(container, Key));
        if (size > 64) Assert.DoesNotContain(plain.AsSpan(0, 64).ToArray(), Slices(container, 64));
    }

    private static IEnumerable<byte[]> Slices(byte[] data, int len)
    {
        for (int i = 0; i + len <= data.Length; i += 7) yield return data.AsSpan(i, len).ToArray();
    }

    [Fact]
    public void Tampering_truncation_and_wrong_key_are_detected()
    {
        var plain = new byte[2 * 1024 * 1024 + 5];
        RandomNumberGenerator.Fill(plain);
        var container = ChunkedAesGcm.Encrypt(plain, Key);

        var flipped = (byte[])container.Clone();
        flipped[container.Length / 2] ^= 1;
        Assert.Throws<CryptographicException>(() => ChunkedAesGcm.Decrypt(flipped, Key));

        var truncated = container.AsSpan(0, container.Length - 40).ToArray();
        Assert.Throws<CryptographicException>(() => ChunkedAesGcm.Decrypt(truncated, Key));

        // Dropping the whole last chunk (not just cutting it) is caught by the "last" flag.
        int firstChunkEnd = 4 + 8 + 5 + 1024 * 1024 + 16;
        Assert.Throws<CryptographicException>(() => ChunkedAesGcm.Decrypt(container.AsSpan(0, firstChunkEnd).ToArray(), Key));

        Assert.Throws<CryptographicException>(() => ChunkedAesGcm.Decrypt(container, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void Password_wraps_and_unwraps_the_data_key()
    {
        var key = PasswordKey.NewDataKey();
        var pk = PasswordKey.Wrap(key, "correct horse");
        Assert.Equal(key, pk.Unwrap("correct horse"));
        Assert.Null(pk.Unwrap("wrong"));
        Assert.Equal(key, pk.Unwrap("correct horse".Normalize(System.Text.NormalizationForm.FormD)));
    }
}

public class EncryptedDestinationTests
{
    [Fact]
    public async Task Backups_on_an_encrypted_destination_are_unreadable_without_the_password()
    {
        using var env = new TestEnv();
        var enc = Path.Combine(env.Dir, "enc"); Directory.CreateDirectory(enc);
        KeyStore.Init(Path.Combine(env.Dir, "home"));
        var secret = "the quick brown fox " + new string('z', 5000);
        env.Write("secret.txt", secret);
        var set = env.NewSet("Enc", env.Source);
        set.Drive = DriveLocator.Register(enc, "pa55word");
        await env.Backup(set: set);

        var drive = DriveStore.TryOpen(enc)!;
        Assert.True(drive.IsEncrypted);
        Assert.False(drive.IsLocked);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(enc, "LocalBack"), "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f) is "drive.json" or "set.json" or "snapshots.jsonl") continue;
            var bytes = File.ReadAllBytes(f);
            Assert.True(ChunkedAesGcm.LooksEncrypted(bytes), f);
            Assert.DoesNotContain("quick brown", System.Text.Encoding.Latin1.GetString(bytes));
            Assert.DoesNotContain("secret.txt", System.Text.Encoding.Latin1.GetString(bytes));
        }

        // Another PC: no saved key.
        KeyStore.ClearForTests();
        var locked = DriveStore.TryOpen(enc)!;
        Assert.True(locked.IsLocked);
        await Assert.ThrowsAsync<DriveLockedException>(() => env.Backup(set: set));
        Assert.Throws<CryptographicException>(() => locked.Set(set.Id).ReadManifest(locked.Set(set.Id).LatestManifestName()!));

        Assert.False(locked.Unlock("nope", remember: false));
        Assert.True(locked.Unlock("pa55word", remember: false));
        var snaps = env.Engine.ListSnapshots(set);
        var target = Path.Combine(env.Dir, "out");
        await env.Engine.RestoreSnapshotAsync(set, snaps[^1].Name, target);
        Assert.Equal(secret, File.ReadAllText(Path.Combine(target, "secret.txt")));
    }

    [Fact]
    public async Task Changing_the_password_keeps_every_file_readable()
    {
        using var env = new TestEnv();
        var enc = Path.Combine(env.Dir, "enc"); Directory.CreateDirectory(enc);
        KeyStore.Init(Path.Combine(env.Dir, "home"));
        env.Write("a.txt", "hello");
        var set = env.NewSet("Enc", env.Source);
        set.Drive = DriveLocator.Register(enc, "one");
        await env.Backup(set: set);

        var drive = DriveStore.TryOpen(enc)!;
        Assert.False(drive.ChangePassword("wrong", "two"));
        Assert.True(drive.ChangePassword("one", "two"));

        KeyStore.ClearForTests();
        var again = DriveStore.TryOpen(enc)!;
        Assert.False(again.Unlock("one", remember: false));
        Assert.True(again.Unlock("two", remember: false));
        var r = await env.Backup(set: set);
        Assert.Null(r.Snapshot);
    }

    [Fact]
    public async Task Saved_key_survives_a_restart_of_the_service()
    {
        using var env = new TestEnv();
        var enc = Path.Combine(env.Dir, "enc"); Directory.CreateDirectory(enc);
        var home = Path.Combine(env.Dir, "home");
        env.Write("a.txt", "hello");
        using (var service = new BackupService(new AppPaths(home, Path.Combine(env.Dir, "tmp"))))
        {
            var set = service.AddSet("Enc", new[] { env.Source }, enc, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true, "pw");
            await service.Engine.BackupAsync(set, SnapshotTrigger.Manual);
        }
        KeyStore.ClearForTests();
        using (var service = new BackupService(new AppPaths(home, Path.Combine(env.Dir, "tmp"))))
        {
            var set = service.Sets.Single();
            Assert.False(service.DriveFor(set)!.IsLocked);
            var r = await service.Engine.BackupAsync(set, SnapshotTrigger.Manual);
            Assert.Null(r.Snapshot);
        }
    }

    [Fact]
    public async Task Unlocking_through_the_service_runs_the_queued_backup()
    {
        using var env = new TestEnv();
        var enc = Path.Combine(env.Dir, "enc"); Directory.CreateDirectory(enc);
        var home = Path.Combine(env.Dir, "home");
        env.Write("a.txt", "hello");
        using (var service = new BackupService(new AppPaths(home, Path.Combine(env.Dir, "tmp"))))
        {
            var set = service.AddSet("Enc", new[] { env.Source }, enc, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true, "pw");
            await service.Engine.BackupAsync(set, SnapshotTrigger.Manual);
        }
        KeyStore.ClearForTests();
        File.Delete(Path.Combine(home, "keys.json")); // like a fresh PC
        using var fresh = new BackupService(new AppPaths(home, Path.Combine(env.Dir, "tmp")));
        fresh.Start(checkOnStartup: false);
        var s = fresh.Sets.Single();
        Assert.Equal(SetHealth.Locked, fresh.GetStatus(s).Health);
        env.Write("b.txt", "new");
        await fresh.BackUpNowAsync(s);
        Assert.Equal(1, fresh.GetStatus(s).Versions);

        Assert.False(fresh.UnlockDrive(fresh.DriveFor(s)!, "bad"));
        Assert.True(fresh.UnlockDrive(fresh.DriveFor(s)!, "pw"));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!(fresh.GetStatus(s).Versions >= 2 && fresh.GetStatus(s).Health == SetHealth.UpToDate) && DateTime.UtcNow < until) await Task.Delay(100);
        Assert.Equal(2, fresh.GetStatus(s).Versions);
        Assert.Equal(SetHealth.UpToDate, fresh.GetStatus(s).Health);
    }
}

public class ProtectLaterTests
{
    private static IEnumerable<string> DataFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "LocalBack"), "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is not ("drive.json" or "set.json" or "snapshots.jsonl") && Path.GetFileName(Path.GetDirectoryName(f)) != "tmp");

    [Fact]
    public async Task A_password_can_be_added_to_a_destination_already_holding_plain_backups()
    {
        using var env = new TestEnv();
        KeyStore.Init(Path.Combine(env.Dir, "home"));
        var secret = "plain before, encrypted after " + new string('q', 3000);
        env.Write("a.txt", secret);
        env.Write("sub/b.txt", "second file");
        await env.Backup();
        env.Write("a.txt", secret + " v2");
        await env.Backup();
        Assert.All(DataFiles(env.Usb), f => Assert.False(ChunkedAesGcm.LooksEncrypted(File.ReadAllBytes(f)), f));

        var drive = env.Drive;
        drive.Protect("later-pw");
        Assert.True(drive.IsEncrypted);
        Assert.True(drive.IsEncrypting);
        Assert.False(drive.IsLocked);

        // Half way: old files are still plain, new writes are encrypted, and everything stays readable.
        env.Write("c.txt", "written after the password");
        var r = await env.Backup();
        Assert.NotNull(r.Snapshot);
        var snaps = env.Engine.ListSnapshots(env.Set);
        var mid = Path.Combine(env.Dir, "mid");
        await env.Engine.RestoreSnapshotAsync(env.Set, snaps[^1].Name, mid);
        Assert.Equal(secret + " v2", File.ReadAllText(Path.Combine(mid, "a.txt")));
        Assert.Equal("written after the password", File.ReadAllText(Path.Combine(mid, "c.txt")));
        Assert.Contains(DataFiles(env.Usb), f => !ChunkedAesGcm.LooksEncrypted(File.ReadAllBytes(f)));
        Assert.Contains(DataFiles(env.Usb), f => ChunkedAesGcm.LooksEncrypted(File.ReadAllBytes(f)));

        // Interrupted: a second call picks up where the first stopped (every file ends up encrypted exactly once).
        var reports = new List<(int Done, int Total)>();
        drive.EncryptPending(new SyncProgress(reports.Add));
        Assert.False(DriveStore.TryOpen(env.Usb)!.IsEncrypting);
        Assert.Equal(reports[^1].Total, reports[^1].Done);
        Assert.All(DataFiles(env.Usb), f => Assert.True(ChunkedAesGcm.LooksEncrypted(File.ReadAllBytes(f)), f));
        foreach (var f in DataFiles(env.Usb))
            Assert.DoesNotContain("plain before", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(f)));

        // Old snapshots still restore; nothing was double-encrypted.
        var old = Path.Combine(env.Dir, "old");
        await env.Engine.RestoreSnapshotAsync(env.Set, snaps[0].Name, old);
        Assert.Equal(secret, File.ReadAllText(Path.Combine(old, "a.txt")));
        Assert.Equal("second file", File.ReadAllText(Path.Combine(old, "sub", "b.txt")));

        // Another PC needs the password now.
        KeyStore.ClearForTests();
        var locked = DriveStore.TryOpen(env.Usb)!;
        Assert.True(locked.IsLocked);
        Assert.Throws<CryptographicException>(() => locked.Set(env.Set.Id).ReadManifest(locked.Set(env.Set.Id).LatestManifestName()!));
        Assert.True(locked.Unlock("later-pw", remember: false));
        Assert.Throws<InvalidOperationException>(() => locked.Protect("again"));
    }

    [Fact]
    public async Task The_service_encrypts_existing_backups_in_the_background_and_resumes_after_a_restart()
    {
        using var env = new TestEnv();
        var home = Path.Combine(env.Dir, "home");
        env.Write("a.txt", new string('a', 20000));
        env.Write("b.txt", "bee");
        using (var service = new BackupService(new AppPaths(home, Path.Combine(env.Dir, "tmp"))))
        {
            service.Start(checkOnStartup: false);
            var set = service.AddSet("Docs", new[] { env.Source }, env.Usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
            await service.BackUpNowAsync(set);
            Assert.Equal(SetHealth.UpToDate, service.GetStatus(set).Health);

            service.ProtectDrive(service.DriveFor(set)!, "svc-pw");
            var until = DateTime.UtcNow.AddSeconds(20);
            while (service.IsBusy && DateTime.UtcNow < until) await Task.Delay(50);
            Assert.False(service.IsBusy);
            Assert.False(service.DriveFor(set)!.IsEncrypting);
            Assert.Equal(SetHealth.UpToDate, service.GetStatus(set).Health);
            Assert.All(DataFiles(env.Usb), f => Assert.True(ChunkedAesGcm.LooksEncrypted(File.ReadAllBytes(f)), f));
        }

        // Simulate an interrupted run: mark the drive as still encrypting and drop one blob back to plaintext.
        var drive = DriveStore.TryOpen(env.Usb)!;
        var hash = drive.Blobs.EnumerateAll().First().Hash;
        var plain = new MemoryStream();
        using (var src = drive.Blobs.OpenRead(hash)) src.CopyTo(plain);
        File.WriteAllBytes(drive.Blobs.PathFor(hash), plain.ToArray());
        var identityFile = Path.Combine(env.Usb, "LocalBack", "drive.json");
        File.WriteAllText(identityFile, File.ReadAllText(identityFile).Replace("\"encrypting\":false", "\"encrypting\":true"));
        Assert.True(DriveStore.TryOpen(env.Usb)!.IsEncrypting);

        using (var service = new BackupService(new AppPaths(home, Path.Combine(env.Dir, "tmp"))))
        {
            service.Start(checkOnStartup: false);
            var set = service.Sets.Single();
            var until = DateTime.UtcNow.AddSeconds(20);
            while ((service.IsBusy || service.DriveFor(set)!.IsEncrypting) && DateTime.UtcNow < until)
            {
                await Task.Delay(50);
                service.RefreshAll();
            }
            Assert.False(DriveStore.TryOpen(env.Usb)!.IsEncrypting);
            Assert.True(ChunkedAesGcm.LooksEncrypted(File.ReadAllBytes(drive.Blobs.PathFor(hash))));
            var target = Path.Combine(env.Dir, "out");
            await service.Engine.RestoreSnapshotAsync(set, service.Engine.ListSnapshots(set)[^1].Name, target);
            Assert.Equal(new string('a', 20000), File.ReadAllText(Path.Combine(target, "a.txt")));
        }
    }

    private sealed class SyncProgress : IProgress<(int Done, int Total)>
    {
        private readonly Action<(int Done, int Total)> _report;
        public SyncProgress(Action<(int Done, int Total)> report) => _report = report;
        public void Report((int Done, int Total) value) => _report(value);
    }
}

public class DestinationTests
{
    [Fact]
    public async Task A_folder_on_a_drive_can_be_the_destination()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "hello");
        var dest = Path.Combine(env.Usb, "Backups", "PC1");
        Directory.CreateDirectory(dest);
        var set = env.NewSet("Sub", env.Source);
        set.Drive = DriveLocator.Register(dest);

        Assert.True(Directory.Exists(Path.Combine(dest, "LocalBack")));
        Assert.NotNull(set.Drive.SubPath);
        Assert.EndsWith(Path.Combine("Backups", "PC1"), set.Drive.SubPath!);
        await env.Backup(set: set);
        Assert.NotNull(DriveLocator.Find(set.Drive));
        Assert.Single(env.Engine.ListSnapshots(set));
    }

    [Fact]
    public void A_network_path_is_marked_as_such()
    {
        var r = new DriveRef { LastRoot = @"\\nas\backups", SubPath = null };
        Assert.True(r.IsNetwork);
        Assert.True(DriveLocator.IsNetworkPath(@"\\nas\backups"));
        Assert.False(DriveLocator.IsNetworkPath(@"E:\Backups"));
    }

    [Fact]
    public void Disk_space_works_for_a_folder()
    {
        var (free, total) = LocalBack.Core.Util.DiskSpace.Get(Path.GetTempPath());
        Assert.True(total > 0);
        Assert.True(free >= 0 && free <= total);
    }
}

public class DeletedFilesAndCopyTests
{
    [Fact]
    public async Task Deleted_files_keep_their_last_copy_and_are_listed()
    {
        using var env = new TestEnv();
        var gone = env.Write("gone.txt", "keep me", secondsAgo: 300);
        env.Write("stay.txt", "still here", secondsAgo: 300);
        await env.Backup();
        env.Write("gone.txt", "keep me v2", secondsAgo: 200);
        await env.Backup();
        File.Delete(gone);
        await env.Backup();
        env.Write("stay.txt", "changed", secondsAgo: 50);
        await env.Backup();

        var deleted = env.Engine.DeletedFiles(env.Set);
        var d = Assert.Single(deleted);
        Assert.Equal("gone.txt", d.Name);
        Assert.NotNull(d.DeletedAt);
        Assert.Equal("keep me v2", new StreamReader(env.Drive.Blobs.OpenRead(d.Entry.Hash)).ReadToEnd());

        // Retention never removes the last copy of a deleted file.
        var preview = LocalBack.Core.Retention.RetentionPlanner.Preview(env.Drive, new LocalBack.Core.Model.RetentionPlan(LocalBack.Core.Model.RetentionKind.KeepLast, Count: 1), DateTimeOffset.Now);
        await LocalBack.Core.Retention.RetentionPlanner.ApplyAsync(env.Engine, env.Drive, preview);
        Assert.True(env.Drive.Blobs.Exists(d.Entry.Hash));
        Assert.Single(env.Engine.DeletedFiles(env.Set));

        // Bring it back.
        var r = await env.Engine.RestoreAsync(env.Set, new[] { (d.Key, d.Entry) }, null);
        Assert.Equal(1, r.Restored);
        Assert.Equal("keep me v2", File.ReadAllText(gone));
        await env.Backup();
        Assert.Empty(env.Engine.DeletedFiles(env.Set));
    }

    [Fact]
    public async Task Large_files_are_copied_in_one_pass_and_still_deduplicated()
    {
        using var env = new TestEnv();
        env.Engine.PreHashLimit = 100; // everything counts as large
        env.Write("a.bin", new string('x', 5000));
        env.Write("b.bin", new string('x', 5000)); // same content
        var r = await env.Backup();
        Assert.Equal(2, r.Added);
        Assert.Single(env.Drive.Blobs.EnumerateAll());
        Assert.Empty(Directory.Exists(Path.Combine(env.Drive.Blobs.Root, "tmp")) ? Directory.GetFiles(Path.Combine(env.Drive.Blobs.Root, "tmp")) : Array.Empty<string>());
    }
}

public class DeletedFilesSingleVersionTests
{
    [Fact]
    public async Task A_file_with_one_version_that_is_deleted_is_listed()
    {
        using var env = new TestEnv();
        var gone = env.Write("old-draft.docx", "draft", secondsAgo: 100);
        env.Write("notes.txt", "n", secondsAgo: 100);
        await env.Engine.BackupAsync(env.Set, LocalBack.Core.Storage.SnapshotTrigger.FirstBackup);
        File.Delete(gone);
        env.Write("new.txt", "x", secondsAgo: 60);
        await env.Engine.BackupAsync(env.Set, LocalBack.Core.Storage.SnapshotTrigger.Live);

        var deleted = env.Engine.DeletedFiles(env.Set);
        var d = Assert.Single(deleted);
        Assert.Equal("old-draft.docx", d.Name);
    }
}
