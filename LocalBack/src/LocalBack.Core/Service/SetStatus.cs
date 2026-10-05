using LocalBack.Core.Model;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.Core.Service;

public enum SetHealth { UpToDate, Pending, Running, DriveMissing, Paused, Error, NeverRun }

public sealed record SetStatus(
    BackupSet Set,
    SetHealth Health,
    DateTimeOffset? LastRun,
    int PendingCount,
    string? Error,
    SnapshotInfo? Latest,
    int Versions,
    string DriveName)
{
    public string StatusText => Health switch
    {
        SetHealth.UpToDate => "Up to date",
        SetHealth.Pending => "Changes pending",
        SetHealth.Running => "Backing up…",
        SetHealth.DriveMissing => "Drive not connected",
        SetHealth.Paused => "Paused",
        SetHealth.Error => "Last backup failed",
        _ => "Not backed up yet",
    };

    /// <summary>Second line under the status: "14:32 today", "3 files changed".</summary>
    public string WhenText => Health switch
    {
        SetHealth.Pending or SetHealth.DriveMissing or SetHealth.Paused when PendingCount > 0 => Format.Plural(PendingCount, "file changed", "files changed"),
        SetHealth.Error => Error ?? "",
        _ => LastRun is { } t ? Format.When(t) : "Never",
    };

    /// <summary>Short form for the tray list: "14:32", "3 pending".</summary>
    public string ShortWhen => Health switch
    {
        SetHealth.Running => "Running",
        SetHealth.Error => "Failed",
        _ when PendingCount > 0 => $"{PendingCount} pending",
        SetHealth.DriveMissing => "No drive",
        _ => LastRun is { } t ? (t.ToLocalTime().Date == DateTime.Today ? t.ToLocalTime().ToString("HH:mm") : Format.When(t)) : "Never",
    };

    public string SizeText => Latest is { } l ? Format.Size(l.Bytes) : "—";
    public string VersionsText => Format.Plural(Versions, "version", "versions");
    public string FoldersText => string.Join("; ", Set.Folders);
}

public sealed record LowSpaceInfo(DriveStore Drive, string DriveName, long Free, long Total, long NextRunEstimate);
