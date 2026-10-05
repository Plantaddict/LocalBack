using System.Text.Json.Serialization;

namespace LocalBack.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<RunSchedule>))]
public enum RunSchedule
{
    /// <summary>Back up within seconds of a save.</summary>
    Live,
    Hourly,
    /// <summary>Once a day at the daily check time.</summary>
    Daily,
    /// <summary>Only when the backup drive is plugged in.</summary>
    OnPlugIn,
}

/// <summary>Which drive a set is kept on. Matched by the id in LocalBack\drive.json or the volume serial, never by letter.</summary>
public sealed class DriveRef
{
    public string Id { get; set; } = "";
    public string? VolumeSerial { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Where the drive was last seen (e.g. "E:\"). Only a hint.</summary>
    public string LastRoot { get; set; } = "";
}

public sealed class BackupSet
{
    /// <summary>Folder name under LocalBack\sets on the drive.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    public DriveRef Drive { get; set; } = new();
    public RunSchedule Schedule { get; set; } = RunSchedule.Live;
    public List<string> EnabledRules { get; set; } = ExclusionRules.DefaultEnabled.ToList();
    public List<string> CustomPatterns { get; set; } = new();
    /// <summary>When false only the newest copy of each file is kept.</summary>
    public bool KeepHistory { get; set; } = true;

    public string ScheduleText => Schedule switch
    {
        RunSchedule.Live => "When files change (live)",
        RunSchedule.Hourly => "Every hour",
        RunSchedule.Daily => "Daily",
        RunSchedule.OnPlugIn => "When the drive is plugged in",
        _ => Schedule.ToString(),
    };
}
