using LocalBack.Core.Engine;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.Core.Tests;

public class SnapshotTreeTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "Users", "me", "Desktop");
    private static readonly string Docs = Path.Combine(Path.GetTempPath(), "Users", "me", "Documents");

    private static SnapshotFile File(string root, string path, long size = 100, ChangeKind change = ChangeKind.Unchanged, int minutesAgo = 60) =>
        new(new FileKey(root, path), new ManifestEntry(0, path, "h" + path, size, DateTime.UtcNow.AddMinutes(-minutesAgo).Ticks, 0), change);

    [Fact]
    public void One_source_folder_starts_at_that_folder()
    {
        var tree = SnapshotTree.Build(new[]
        {
            File(Desktop, "notes.txt", 10, ChangeKind.Modified),
            File(Desktop, "Invoices/2026/Invoice 10.pdf", 300, ChangeKind.Added),
            File(Desktop, "Invoices/2026/Invoice 9.pdf", 200),
            File(Desktop, "Invoices/readme.txt", 5),
            File(Desktop, "gone.docx", 999, ChangeKind.Deleted),
        }, new[] { Desktop });

        Assert.Same(tree.Top.Folders[0], tree.Home);
        Assert.Equal("Desktop", tree.Home.Name);
        Assert.Equal(Desktop, tree.Home.FullPath);
        Assert.Equal(4, tree.FileCount);           // the deleted file is not in this snapshot
        Assert.Equal(515, tree.Bytes);
        Assert.Equal(2, tree.Home.ChangedCount);

        Assert.Equal(new[] { "Invoices" }, tree.Home.Folders.Select(f => f.Name));
        Assert.Equal(new[] { "notes.txt" }, tree.Home.Files.Select(f => f.Name));

        var invoices = tree.Find("Desktop/Invoices")!;
        Assert.Equal(3, invoices.FileCount);
        Assert.Equal(505, invoices.Bytes);
        Assert.Equal(1, invoices.ChangedCount);
        Assert.Equal(Path.Combine(Desktop, "Invoices"), invoices.FullPath);
        Assert.Equal(new[] { "Desktop", "Invoices" }, invoices.Ancestry.Select(a => a.Name));

        var y2026 = tree.Find(@"Desktop\Invoices\2026")!;
        Assert.Equal(new[] { "Invoice 9.pdf", "Invoice 10.pdf" }, y2026.Files.Select(f => f.Name)); // natural order
        Assert.Same(y2026, tree.FolderOf(new FileKey(Desktop, "Invoices/2026/Invoice 9.pdf")));
        Assert.Same(tree.Home, tree.FolderOf(new FileKey(Desktop, "notes.txt")));
        Assert.Null(tree.FolderOf(new FileKey(Docs, "x.txt")));
        Assert.Null(tree.Find("Desktop/Nope"));
        Assert.Equal(3, invoices.AllFiles().Count());
    }

    [Fact]
    public void Several_source_folders_start_at_the_top()
    {
        var tree = SnapshotTree.Build(new[]
        {
            File(Desktop, "a.txt"),
            File(Docs, "b.txt"),
        }, new[] { Desktop, Docs });

        Assert.Same(tree.Top, tree.Home);
        Assert.Equal(new[] { "Desktop", "Documents" }, tree.Top.Folders.Select(f => f.Name));
        Assert.Equal(Docs, tree.Top.Folders[1].SourceRoot);
        Assert.Null(tree.Top.FullPath);
        Assert.Empty(tree.Top.Ancestry);
    }

    [Fact]
    public void Source_folders_with_the_same_name_keep_their_full_path()
    {
        var a = Path.Combine(Path.GetTempPath(), "one", "Photos");
        var b = Path.Combine(Path.GetTempPath(), "two", "Photos");
        var tree = SnapshotTree.Build(new[] { File(a, "x.jpg"), File(b, "y.jpg") }, new[] { a, b });
        Assert.Equal("Photos", tree.Top.Folders[0].Name);
        Assert.Equal(b, tree.Top.Folders[1].Name);
        Assert.Same(tree.Top.Folders[1], tree.FolderOf(new FileKey(b, "y.jpg")));
        Assert.Same(tree.Top.Folders[1], tree.Find(tree.Top.Folders[1].Path));
    }

    [Fact]
    public void Empty_source_folder_is_still_shown()
    {
        var tree = SnapshotTree.Build(Array.Empty<SnapshotFile>(), new[] { Desktop });
        Assert.Equal("Desktop", tree.Home.Name);
        Assert.Equal(0, tree.FileCount);
        Assert.Null(tree.Home.ModifiedUtc);
    }

    [Theory]
    [InlineData("file2.txt", "file10.txt", -1)]
    [InlineData("File10.txt", "file2.txt", 1)]
    [InlineData("a.txt", "A.txt", 0)]
    [InlineData("Invoice 2026-117.pdf", "Invoice 2026-118.pdf", -1)]
    [InlineData("abc", "abc1", -1)]
    [InlineData("ab1", "abc", -1)]
    [InlineData("01", "1", 1)]
    [InlineData("photo", "Photos", -1)]
    public void Natural_order(string x, string y, int expected) =>
        Assert.Equal(expected, Math.Sign(NaturalComparer.Instance.Compare(x, y)));
}
