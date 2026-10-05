using System.Text.Json.Serialization;

namespace LocalBack.Core.Storage;

/// <summary>Summary line for one snapshot, kept in snapshots.jsonl so listing history never opens every manifest.</summary>
public sealed record SnapshotInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("created")] public DateTimeOffset CreatedUtc { get; init; }
    [JsonPropertyName("trigger")] public SnapshotTrigger Trigger { get; init; }
    [JsonPropertyName("files")] public int Files { get; init; }
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    [JsonPropertyName("added")] public int Added { get; init; }
    [JsonPropertyName("modified")] public int Modified { get; init; }
    [JsonPropertyName("deleted")] public int Deleted { get; init; }

    [JsonIgnore] public int Changed => Added + Modified + Deleted;

    [JsonIgnore]
    public string TriggerText => Trigger switch
    {
        SnapshotTrigger.Live => "Live",
        SnapshotTrigger.Manual => "Backed up now",
        SnapshotTrigger.DrivePlugIn => "Drive plugged in",
        SnapshotTrigger.DailyCheck => "Daily check",
        SnapshotTrigger.Scheduled => "Scheduled",
        SnapshotTrigger.BeforeRestore => "Before restore",
        SnapshotTrigger.Restore => "Restore",
        SnapshotTrigger.FirstBackup => "First backup",
        SnapshotTrigger.Startup => "Check at startup",
        _ => Trigger.ToString(),
    };
}
