using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalBack.Core.Model;
using LocalBack.Core.Util;

namespace LocalBack.Core.Storage;

/// <summary>One backup set on the drive: sets\&lt;id&gt;\set.json, manifests\, snapshots.jsonl.</summary>
public sealed class SetStore
{
    public const string ManifestExtension = ".json.gz";
    private const string NameFormat = "yyyy-MM-dd'T'HH-mm-ss-fff'Z'";

    public string Id { get; }
    public string Dir { get; }
    public string ManifestsDir => Path.Combine(Dir, "manifests");
    private string DefinitionFile => Path.Combine(Dir, "set.json");
    private string CatalogFile => Path.Combine(Dir, "snapshots.jsonl");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false };

    private readonly byte[]? _key;

    public SetStore(string setsDir, string id, byte[]? key = null)
    {
        Id = id;
        Dir = Path.Combine(setsDir, id);
        _key = key;
    }

    public bool Exists => Directory.Exists(Dir);

    /// <summary>Writes set.json, unless it already says the same (every run calls this; the file rarely changes).</summary>
    public void SaveDefinition(BackupSet set)
    {
        Directory.CreateDirectory(ManifestsDir);
        var json = JsonSerializer.Serialize(set, JsonOptions);
        try
        {
            if (File.Exists(DefinitionFile) && File.ReadAllText(DefinitionFile) == json) return;
        }
        catch (IOException) { }
        AtomicFile.WriteAllText(DefinitionFile, json);
    }

    public BackupSet? LoadDefinition()
    {
        try
        {
            return File.Exists(DefinitionFile) ? JsonSerializer.Deserialize<BackupSet>(File.ReadAllText(DefinitionFile)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Manifest names, oldest first. Names sort chronologically.</summary>
    public List<string> ManifestNames()
    {
        if (!Directory.Exists(ManifestsDir)) return new List<string>();
        var names = Directory.EnumerateFiles(ManifestsDir, "*" + ManifestExtension)
            .Select(Path.GetFileName)
            .Where(n => n != null && IsManifestName(n))
            .Select(n => n!)
            .ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public string? LatestManifestName() => ManifestNames().LastOrDefault();

    public Manifest ReadManifest(string name) => Manifest.Read(Path.Combine(ManifestsDir, name), _key);

    public static bool IsManifestName(string name) =>
        name.EndsWith(ManifestExtension, StringComparison.Ordinal) &&
        DateTimeOffset.TryParseExact(name[..^ManifestExtension.Length], NameFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _);

    public static DateTimeOffset TimeFromName(string name) =>
        DateTimeOffset.ParseExact(name[..^ManifestExtension.Length], NameFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>A name later than every existing one, so ordering by name is ordering by time.</summary>
    public string NewManifestName(DateTimeOffset utc)
    {
        var latest = LatestManifestName();
        var t = utc.ToUniversalTime();
        t = new DateTimeOffset(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        if (latest != null)
        {
            var last = TimeFromName(latest);
            if (t <= last) t = last.AddMilliseconds(1);
        }
        return t.ToString(NameFormat, CultureInfo.InvariantCulture) + ManifestExtension;
    }

    /// <summary>Writes a new snapshot. The manifest is written first; the catalog line is only a cache.</summary>
    public SnapshotInfo AddSnapshot(Manifest manifest, SnapshotDiff diff)
    {
        Directory.CreateDirectory(ManifestsDir);
        var name = NewManifestName(manifest.CreatedUtc);
        Manifest.Write(Path.Combine(ManifestsDir, name), manifest, _key);
        var info = SnapshotDiff.ToInfo(name, manifest, diff);
        try
        {
            File.AppendAllText(CatalogFile, JsonSerializer.Serialize(info, LineOptions) + "\n", Encoding.UTF8);
        }
        catch (IOException ex)
        {
            Log.Warn($"Could not update snapshot list for {Id}: {ex.Message}");
        }
        return info;
    }

    public void ReplaceManifest(string name, Manifest manifest) => Manifest.Write(Path.Combine(ManifestsDir, name), manifest, _key);

    public void DeleteManifest(string name) => AtomicFile.TryDelete(Path.Combine(ManifestsDir, name));

    /// <summary>Snapshot summaries, oldest first. Rebuilt from the manifests if the cache is stale.</summary>
    public List<SnapshotInfo> Snapshots()
    {
        var names = ManifestNames();
        var cached = ReadCatalog();
        if (cached != null && cached.Count == names.Count && cached.Select(c => c.Name).SequenceEqual(names, StringComparer.Ordinal))
            return cached;
        return RebuildCatalog(names);
    }

    public List<SnapshotInfo> RebuildCatalog() => RebuildCatalog(ManifestNames());

    private List<SnapshotInfo> RebuildCatalog(List<string> names)
    {
        var list = new List<SnapshotInfo>(names.Count);
        Manifest? prev = null;
        foreach (var name in names)
        {
            Manifest cur;
            try
            {
                cur = ReadManifest(name);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                Log.Warn($"Skipping unreadable manifest {Id}/{name}: {ex.Message}");
                continue;
            }
            list.Add(SnapshotDiff.ToInfo(name, cur, SnapshotDiff.Compute(prev, cur)));
            prev = cur;
        }
        WriteCatalog(list);
        return list;
    }

    private void WriteCatalog(List<SnapshotInfo> list)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var i in list) sb.Append(JsonSerializer.Serialize(i, LineOptions)).Append('\n');
            Directory.CreateDirectory(Dir);
            AtomicFile.WriteAllText(CatalogFile, sb.ToString());
        }
        catch (IOException ex)
        {
            Log.Warn($"Could not write snapshot list for {Id}: {ex.Message}");
        }
    }

    private List<SnapshotInfo>? ReadCatalog()
    {
        if (!File.Exists(CatalogFile)) return null;
        try
        {
            var list = new List<SnapshotInfo>();
            foreach (var line in File.ReadLines(CatalogFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var info = JsonSerializer.Deserialize<SnapshotInfo>(line, LineOptions);
                if (info != null) list.Add(info);
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
