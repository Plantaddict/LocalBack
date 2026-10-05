using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalBack.Core.Util;

namespace LocalBack.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter<SnapshotTrigger>))]
public enum SnapshotTrigger
{
    Live,
    Manual,
    DrivePlugIn,
    DailyCheck,
    Scheduled,
    /// <summary>Taken just before a restore overwrote files, so the restore can be undone.</summary>
    BeforeRestore,
    Restore,
    FirstBackup,
    Startup,
}

/// <summary>One file in a snapshot. Short property names keep manifests small.</summary>
public sealed record ManifestEntry(
    [property: JsonPropertyName("r")] int Root,
    [property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("h")] string Hash,
    [property: JsonPropertyName("s")] long Size,
    [property: JsonPropertyName("m")] long MTimeTicks,
    [property: JsonPropertyName("a")] int Attributes)
{
    [JsonIgnore]
    public DateTime MTimeUtc => new(MTimeTicks, DateTimeKind.Utc);
}

/// <summary>A snapshot: every file in the set at one moment. Unchanged files point at blobs that are already stored.</summary>
public sealed class Manifest
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("set")] public string SetId { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset CreatedUtc { get; set; }
    [JsonPropertyName("trigger")] public SnapshotTrigger Trigger { get; set; }
    [JsonPropertyName("host")] public string Host { get; set; } = Environment.MachineName;
    /// <summary>Source folders; entries refer to them by index.</summary>
    [JsonPropertyName("roots")] public List<string> Roots { get; set; } = new();
    [JsonPropertyName("entries")] public List<ManifestEntry> Entries { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static void Write(string path, Manifest manifest) =>
        AtomicFile.Write(path, s =>
        {
            using var gz = new GZipStream(s, CompressionLevel.Fastest, leaveOpen: true);
            JsonSerializer.Serialize(gz, manifest, Options);
        });

    public static Manifest Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Manifest>(gz, Options) ?? throw new InvalidDataException($"Empty manifest {path}");
    }

    /// <summary>Entries keyed by (root folder, relative path).</summary>
    public Dictionary<FileKey, ManifestEntry> ToMap()
    {
        var map = new Dictionary<FileKey, ManifestEntry>(Entries.Count, FileKey.Comparer);
        foreach (var e in Entries)
        {
            if (e.Root < 0 || e.Root >= Roots.Count) continue;
            map[new FileKey(Roots[e.Root], e.Path)] = e;
        }
        return map;
    }
}

/// <summary>Identifies a file independent of its position in a manifest's root list.</summary>
public readonly record struct FileKey(string Root, string Path)
{
    public static readonly IEqualityComparer<FileKey> Comparer = new KeyComparer();

    public string FullPath => PathUtil.FromManifestPath(Root, Path);

    private sealed class KeyComparer : IEqualityComparer<FileKey>
    {
        public bool Equals(FileKey a, FileKey b) => PathUtil.Comparer.Equals(a.Root, b.Root) && PathUtil.Comparer.Equals(a.Path, b.Path);
        public int GetHashCode(FileKey k) => HashCode.Combine(PathUtil.Comparer.GetHashCode(k.Root), PathUtil.Comparer.GetHashCode(k.Path));
    }
}
