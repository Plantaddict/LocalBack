using LocalBack.Core.Model;
using LocalBack.Core.Scanning;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;
using LocalBack.Core.Watching;

namespace LocalBack.Core.Tests;

public class ExclusionFilterTests
{
    private static readonly ExclusionFilter Defaults = new(ExclusionRules.DefaultEnabled, Array.Empty<string>());

    [Theory]
    [InlineData("draft.tmp", true)]
    [InlineData("notes.txt~", true)]
    [InlineData("~$Quarterly report.xlsx", true)]
    [InlineData(".~lock.budget.ods#", true)]
    [InlineData("setup.exe.crdownload", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("sub/desktop.ini", true)]
    [InlineData("app.log", true)]
    [InlineData("Quarterly report.xlsx", false)]
    [InlineData("movie.mkv", false)]
    public void File_rules(string path, bool excluded) => Assert.Equal(excluded, Defaults.ExcludeFile(path, 10));

    [Theory]
    [InlineData("node_modules", true)]
    [InlineData("web/node_modules", true)]
    [InlineData("repo/.git", true)]
    [InlineData("$RECYCLE.BIN", true)]
    [InlineData("Photos", false)]
    public void Folder_rules(string path, bool excluded) => Assert.Equal(excluded, Defaults.ExcludeDirectory(path));

    [Fact]
    public void Parent_folders_are_checked_for_single_paths()
    {
        Assert.True(Defaults.ExcludeAnyParent("web/node_modules/react/index.js"));
        Assert.False(Defaults.ExcludeAnyParent("web/src/index.js"));
    }

    [Fact]
    public void Size_rule()
    {
        var f = new ExclusionFilter(new[] { "large" }, Array.Empty<string>());
        Assert.True(f.ExcludeFile("big.bin", ExclusionRules.TwoGB + 1));
        Assert.False(f.ExcludeFile("big.bin", ExclusionRules.TwoGB));
    }

    [Fact]
    public void Custom_patterns()
    {
        var f = new ExclusionFilter(Array.Empty<string>(), new[] { "*.iso", "Renders/", "Projects/Old/", "cache\\*.bin" });
        Assert.True(f.ExcludeFile("disk.ISO", 1));
        Assert.True(f.ExcludeDirectory("work/Renders"));
        Assert.False(f.ExcludeFile("Renders", 1));
        Assert.True(f.ExcludeDirectory("a/Projects/Old"));
        Assert.False(f.ExcludeDirectory("Old"));
        Assert.True(f.ExcludeFile("x/cache/a.bin", 1));
        Assert.Null(ExclusionFilter.Validate("*.iso"));
        Assert.NotNull(ExclusionFilter.Validate("*"));
        Assert.NotNull(ExclusionFilter.Validate("  "));
    }

    [Fact]
    public void Hidden_system_files_are_always_skipped()
    {
        Assert.True(ExclusionFilter.IsExcludedByAttributes(FileAttributes.Hidden | FileAttributes.System));
        Assert.False(ExclusionFilter.IsExcludedByAttributes(FileAttributes.Hidden));
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(500, "1 KB")]
    [InlineData(4 * 1024, "4 KB")]
    [InlineData(340 * 1024, "340 KB")]
    [InlineData(1258291, "1.2 MB")]
    [InlineData(2254857830, "2.1 GB")]
    [InlineData(28991029248, "27.0 GB")]
    [InlineData(388694833152, "362 GB")]
    [InlineData(1099511627776, "1.0 TB")]
    public void Sizes(long bytes, string text) => Assert.Equal(text, Format.Size(bytes));

    [Fact]
    public void Times()
    {
        var now = new DateTimeOffset(2026, 10, 5, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5)));
        Assert.Equal("14:32 today", Format.When(now.AddMinutes(-28), now));
        Assert.Equal("Yesterday 18:00", Format.SnapshotLabel(now.AddHours(-21), now));
        Assert.Equal("Sat 3 Oct, 18:00", Format.SnapshotLabel(now.AddDays(-2).AddHours(3), now));
    }
}

public class DebouncerTests
{
    [Fact]
    public void Many_events_for_one_file_become_one_batch_after_quiet()
    {
        var t = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var batches = new List<IReadOnlyList<string>>();
        using var d = new Debouncer(TimeSpan.FromSeconds(3), b => batches.Add(b), () => t);

        d.Add("C:\\a.txt");
        t = t.AddSeconds(1); d.Add("C:\\a.txt");
        t = t.AddSeconds(1); d.Add("C:\\a.txt");
        d.Add("C:\\b.txt");
        t = t.AddSeconds(2); d.Tick();
        Assert.Empty(batches);

        t = t.AddSeconds(1.5); d.Tick();
        Assert.Single(batches);
        Assert.Equal(2, batches[0].Count);
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void A_file_written_continuously_is_still_flushed_after_max_delay()
    {
        var t = DateTime.UtcNow;
        var batches = new List<IReadOnlyList<string>>();
        using var d = new Debouncer(TimeSpan.FromSeconds(3), b => batches.Add(b), () => t) { MaxDelay = TimeSpan.FromSeconds(10) };
        for (int i = 0; i < 12; i++)
        {
            d.Add("log.txt");
            t = t.AddSeconds(1);
            d.Tick();
        }
        Assert.Single(batches);
    }
}

public class StoreTests
{
    [Fact]
    public void Manifest_names_sort_by_time_and_never_collide()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lb-names-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var s = new SetStore(dir, "x");
            var t = DateTimeOffset.UtcNow;
            var m = new Manifest { CreatedUtc = t };
            var a = s.AddSnapshot(m, default).Name;
            var b = s.AddSnapshot(m, default).Name; // same timestamp
            Assert.NotEqual(a, b);
            Assert.True(string.CompareOrdinal(a, b) < 0);
            Assert.Equal(new[] { a, b }, s.ManifestNames());
            Assert.True(SetStore.TimeFromName(b) > SetStore.TimeFromName(a));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Blob_store_dedupes_and_reports_hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lb-blobs-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var store = new BlobStore(dir);
            var h1 = store.Put(new MemoryStream("abc"u8.ToArray()));
            var h2 = store.Put(new MemoryStream("abc"u8.ToArray()));
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h1);
            Assert.Equal(h1, h2);
            Assert.Single(store.EnumerateAll());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Slugs_are_safe_folder_names()
    {
        Assert.Equal("Desktop", PathUtil.Slug("Desktop"));
        Assert.Equal("My-work", PathUtil.Slug("My/work"));
        Assert.Equal("set", PathUtil.Slug("..."));
    }
}
