using LocalBack.Core.Drives;
using LocalBack.Core.Engine;
using LocalBack.Core.Indexing;
using LocalBack.Core.Model;
using LocalBack.Core.Storage;

namespace LocalBack.Core.Tests;

/// <summary>A source folder, a fake "drive" folder and an index, all in a temp dir.</summary>
public sealed class TestEnv : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "lb-test-" + Guid.NewGuid().ToString("N")[..8]);
    public string Source => Path.Combine(Dir, "src");
    public string Usb => Path.Combine(Dir, "usb");
    public LocalIndex Index { get; }
    public BackupEngine Engine { get; }
    public BackupSet Set { get; }

    public TestEnv(string name = "Desktop")
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Usb);
        Index = new LocalIndex(Path.Combine(Dir, "index.db"));
        Engine = new BackupEngine(Index, Path.Combine(Dir, "temp")) { YoungEmptyFileAge = TimeSpan.Zero };
        Set = NewSet(name, Source);
    }

    public BackupSet NewSet(string id, params string[] folders) => new()
    {
        Id = id,
        Name = id,
        Folders = folders.ToList(),
        Drive = DriveLocator.Register(Usb),
    };

    public DriveStore Drive => DriveStore.TryOpen(Usb)!;

    /// <summary>Writes a file with a distinct timestamp so size+time change detection sees it.</summary>
    public string Write(string rel, string content, int secondsAgo = 60)
    {
        var path = Path.Combine(Source, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-secondsAgo).AddMilliseconds(Random.Shared.Next(1000)));
        return path;
    }

    public Task<BackupResult> Backup(IReadOnlyCollection<string>? paths = null, BackupSet? set = null) =>
        Engine.BackupAsync(set ?? Set, SnapshotTrigger.Manual, paths);

    public void Dispose()
    {
        Index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
