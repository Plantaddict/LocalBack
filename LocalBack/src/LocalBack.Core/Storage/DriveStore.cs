using System.Text.Json;
using System.Text.Json.Serialization;
using LocalBack.Core.Util;

namespace LocalBack.Core.Storage;

/// <summary>
/// The LocalBack folder on a backup drive:
/// <code>
/// E:\LocalBack\
///   drive.json            identity of this drive
///   objects\ab\cdef…      one blob per unique file content
///   sets\&lt;id&gt;\           set.json, snapshots.jsonl, manifests\*.json.gz
/// </code>
/// </summary>
public sealed class DriveStore
{
    public const string FolderName = "LocalBack";

    public string DriveRoot { get; }
    public string Root { get; }
    public BlobStore Blobs { get; }
    public string SetsDir => Path.Combine(Root, "sets");
    private string IdentityFile => Path.Combine(Root, "drive.json");

    private DriveStore(string driveRoot)
    {
        DriveRoot = driveRoot;
        Root = Path.Combine(driveRoot, FolderName);
        Blobs = new BlobStore(Path.Combine(Root, "objects"));
    }

    /// <summary>Opens the store on a drive if it has one.</summary>
    public static DriveStore? TryOpen(string driveRoot)
    {
        var store = new DriveStore(driveRoot);
        return File.Exists(store.IdentityFile) ? store : null;
    }

    /// <summary>Opens the store, creating the folder layout and identity on first use.</summary>
    public static DriveStore OpenOrCreate(string driveRoot)
    {
        var store = new DriveStore(driveRoot);
        Directory.CreateDirectory(store.SetsDir);
        Directory.CreateDirectory(store.Blobs.Root);
        if (!File.Exists(store.IdentityFile))
        {
            var id = new DriveIdentity { Id = Guid.NewGuid().ToString("N"), Created = DateTimeOffset.UtcNow };
            AtomicFile.WriteAllText(store.IdentityFile, JsonSerializer.Serialize(id));
        }
        return store;
    }

    public DriveIdentity Identity
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<DriveIdentity>(File.ReadAllText(IdentityFile)) ?? new DriveIdentity();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new DriveIdentity();
            }
        }
    }

    public SetStore Set(string id) => new(SetsDir, id);

    public IEnumerable<SetStore> Sets()
    {
        if (!Directory.Exists(SetsDir)) yield break;
        foreach (var d in Directory.EnumerateDirectories(SetsDir))
            yield return new SetStore(SetsDir, Path.GetFileName(d));
    }

    public (long Free, long Total) Space()
    {
        try
        {
            var di = new DriveInfo(DriveRoot);
            if (di.IsReady) return (di.AvailableFreeSpace, di.TotalSize);
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        return (0, 0);
    }
}

public sealed class DriveIdentity
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
}
