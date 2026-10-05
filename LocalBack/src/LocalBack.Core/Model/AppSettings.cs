namespace LocalBack.Core.Model;

public sealed class AppSettings
{
    public List<BackupSet> Sets { get; set; } = new();

    /// <summary>Local time of the daily full check.</summary>
    public TimeSpan DailyCheckAt { get; set; } = new(18, 0, 0);

    public DateTimeOffset? PausedUntil { get; set; }

    /// <summary>The standing "Version retention" policy used when space runs low.</summary>
    public RetentionPlan Retention { get; set; } = RetentionPlan.Default;

    /// <summary>Apply <see cref="Retention"/> automatically instead of asking when space is low.</summary>
    public bool AutoFreeSpace { get; set; }

    /// <summary>Warn when free space drops below this fraction of the drive...</summary>
    public double LowSpaceFraction { get; set; } = 0.05;
    /// <summary>...or below this many bytes, whichever is larger.</summary>
    public long LowSpaceMinBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Seconds of quiet before a changed file is backed up.</summary>
    public int DebounceSeconds { get; set; } = 3;

    /// <summary>UI language code ("en", "pl"); null follows the Windows display language.</summary>
    public string? Language { get; set; }

    public bool StartWithWindows { get; set; } = true;
    public bool ExplorerMenu { get; set; } = true;
    public bool ThrottleOnBattery { get; set; } = true;
}
