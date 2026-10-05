using LocalBack.Core.Model;
using LocalBack.Core.Retention;
using LocalBack.Core.Storage;

namespace LocalBack.Core.Tests;

public class RetentionTests
{
    private static async Task<TestEnv> EnvWithVersions(int count)
    {
        var env = new TestEnv();
        for (int i = 0; i < count; i++)
        {
            env.Write("report.xlsx", $"version {i} " + new string('x', 1000 * (i + 1)), secondsAgo: 1000 - i * 10);
            await env.Backup();
        }
        return env;
    }

    [Fact]
    public async Task Keep_last_preview_is_exact_and_apply_frees_that_much()
    {
        using var env = await EnvWithVersions(5);
        env.Write("other.txt", "constant");
        await env.Backup();
        var blobsBefore = env.Drive.Blobs.EnumerateAll().ToList();

        var preview = RetentionPlanner.Preview(env.Drive, new RetentionPlan(RetentionKind.KeepLast, Count: 2), DateTimeOffset.Now);

        Assert.Equal(3, preview.VersionsRemoved);
        var expected = blobsBefore.Where(b => b.Size > 100).OrderBy(b => b.Size).Take(3).Sum(b => b.Size); // the three oldest report versions
        Assert.Equal(expected, preview.BytesFreed);

        await RetentionPlanner.ApplyAsync(env.Engine, env.Drive, preview);

        var after = env.Drive.Blobs.EnumerateAll().ToList();
        Assert.Equal(blobsBefore.Sum(b => b.Size) - preview.BytesFreed, after.Sum(b => b.Size));
        var latest = env.Engine.ListSnapshots(env.Set)[^1];
        var map = env.Drive.Set(env.Set.Id).ReadManifest(latest.Name).ToMap();
        Assert.Equal(2, map.Count); // newest snapshot untouched
        Assert.Contains("version 4", new StreamReader(env.Drive.Blobs.OpenRead(map.Values.First(e => e.Path == "report.xlsx").Hash)).ReadToEnd());
    }

    [Fact]
    public async Task Newest_version_of_a_deleted_file_is_kept()
    {
        using var env = await EnvWithVersions(3);
        File.Delete(Path.Combine(env.Source, "report.xlsx"));
        env.Write("new.txt", "n");
        await env.Backup();

        var preview = RetentionPlanner.Preview(env.Drive, new RetentionPlan(RetentionKind.KeepLast, Count: 1), DateTimeOffset.Now);
        await RetentionPlanner.ApplyAsync(env.Engine, env.Drive, preview);

        var snaps = env.Engine.ListSnapshots(env.Set);
        var withReport = snaps.Select(s => env.Drive.Set(env.Set.Id).ReadManifest(s.Name))
            .SelectMany(m => m.Entries).Where(e => e.Path == "report.xlsx").Select(e => e.Hash).Distinct().ToList();
        Assert.Single(withReport);
        Assert.True(env.Drive.Blobs.Exists(withReport[0]));
    }

    [Fact]
    public async Task Blobs_shared_with_another_set_are_never_deleted()
    {
        using var env = new TestEnv();
        var other = Path.Combine(env.Dir, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "copy.txt"), "shared content");
        var otherSet = env.NewSet("Other", other);
        await env.Backup(set: otherSet);

        env.Write("a.txt", "shared content", secondsAgo: 100);
        await env.Backup();
        env.Write("a.txt", "new content", secondsAgo: 50);
        await env.Backup();

        var preview = RetentionPlanner.Preview(env.Drive, new RetentionPlan(RetentionKind.KeepLast, Count: 1), DateTimeOffset.Now, new[] { env.Set.Id });
        Assert.Equal(1, preview.VersionsRemoved);
        Assert.Equal(0, preview.BytesFreed);
        await RetentionPlanner.ApplyAsync(env.Engine, env.Drive, preview);
        Assert.Equal(2, env.Drive.Blobs.EnumerateAll().Count());
    }

    [Fact]
    public async Task Pruning_drops_snapshots_that_became_empty_or_duplicates()
    {
        using var env = await EnvWithVersions(4);
        var preview = RetentionPlanner.Preview(env.Drive, new RetentionPlan(RetentionKind.KeepLast, Count: 1), DateTimeOffset.Now);
        await RetentionPlanner.ApplyAsync(env.Engine, env.Drive, preview);

        var snaps = env.Engine.ListSnapshots(env.Set);
        Assert.Single(snaps);
        Assert.Single(env.Drive.Blobs.EnumerateAll());
    }

    [Fact]
    public async Task Garbage_collection_removes_orphans()
    {
        using var env = await EnvWithVersions(1);
        using (var s = new MemoryStream("orphan"u8.ToArray())) env.Drive.Blobs.Put(s);
        Assert.Equal(2, env.Drive.Blobs.EnumerateAll().Count());

        var freed = RetentionPlanner.CollectGarbage(env.Drive);

        Assert.Equal(6, freed);
        Assert.Single(env.Drive.Blobs.EnumerateAll());
    }

    private static List<RetentionPlanner.Version> Versions(int n) =>
        Enumerable.Range(0, n).Select(i => new RetentionPlanner.Version { Hash = "h" + i, Size = 1, First = i, Last = i }).ToList();

    [Fact]
    public void Daily_then_weekly_keeps_one_per_day_recently_and_one_per_week_before()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var times = new List<DateTimeOffset>
        {
            now.AddDays(-60), now.AddDays(-59), // same ISO week? check below
            now.AddDays(-2).AddHours(-3), now.AddDays(-2).AddHours(-1), // same day
            now.AddHours(-2), now.AddHours(-1), // today
        };
        var keep = RetentionPlanner.Decide(new RetentionPlan(RetentionKind.DailyThenWeekly), Versions(times.Count), times, now);

        Assert.True(keep[^1]);
        Assert.False(keep[4]);      // earlier today
        Assert.True(keep[3]);
        Assert.False(keep[2]);      // earlier the same day
        int weekKept = (keep[0] ? 1 : 0) + (keep[1] ? 1 : 0);
        bool sameWeek = System.Globalization.ISOWeek.GetWeekOfYear(times[0].LocalDateTime) == System.Globalization.ISOWeek.GetWeekOfYear(times[1].LocalDateTime);
        Assert.Equal(sameWeek ? 1 : 2, weekKept);
        Assert.True(keep[1]);
    }

    [Fact]
    public void Older_than_drops_versions_replaced_before_the_cutoff()
    {
        var now = DateTimeOffset.Now;
        var times = new List<DateTimeOffset> { now.AddDays(-200), now.AddDays(-150), now.AddDays(-10), now.AddDays(-1) };
        var keep = RetentionPlanner.Decide(new RetentionPlan(RetentionKind.OlderThan, Days: 90), Versions(4), times, now);

        Assert.Equal(new[] { false, true, true, true }, keep); // v0 replaced 150 days ago; v1 replaced 10 days ago
    }

    [Fact]
    public void Keep_last_always_keeps_at_least_the_newest()
    {
        var times = Enumerable.Range(0, 3).Select(i => DateTimeOffset.Now.AddDays(-i)).Reverse().ToList();
        var keep = RetentionPlanner.Decide(new RetentionPlan(RetentionKind.KeepLast, Count: 0), Versions(3), times, DateTimeOffset.Now);
        Assert.Equal(new[] { false, false, true }, keep);
    }
}
