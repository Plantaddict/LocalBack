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
