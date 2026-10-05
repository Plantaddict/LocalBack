using LocalBack.Core.Storage;

namespace LocalBack.Core.Engine;

public sealed class DriveNotAvailableException : Exception
{
    public DriveNotAvailableException(string setName, string driveLabel)
        : base($"The backup drive for \"{setName}\" ({driveLabel}) is not plugged in.") { }
}

public sealed record BackupProgress(string Phase, int FilesDone, int FilesTotal, long BytesCopied, string? CurrentFile);

public sealed class BackupResult
{
    public SnapshotInfo? Snapshot { get; init; }
    public int Added { get; init; }
    public int Modified { get; init; }
    public int Deleted { get; init; }
    public int Files { get; init; }
    public long TotalBytes { get; init; }
    public long BytesCopied { get; init; }
    /// <summary>Files that were open elsewhere or still being written; retried on the next pass.</summary>
    public IReadOnlyList<string> Deferred { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Failed { get; init; } = Array.Empty<string>();
    public bool FullScan { get; init; }
    public int Changed => Added + Modified + Deleted;
}

public sealed class RestoreResult
{
    public int Restored { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> Failed { get; init; } = Array.Empty<string>();
    /// <summary>The snapshot holding the files as they were just before the restore (undo point).</summary>
    public SnapshotInfo? UndoSnapshot { get; init; }
}

/// <summary>A file as shown in the History view for one snapshot.</summary>
public sealed record SnapshotFile(FileKey Key, ManifestEntry Entry, ChangeKind Change)
{
    public string Name => Util.PathUtil.FileName(Key.Path);
    public string DisplayPath => Key.Path.Replace('/', Path.DirectorySeparatorChar);
}

public sealed record SnapshotDetails(SnapshotInfo Info, IReadOnlyList<SnapshotFile> Files, IReadOnlyList<string> Roots);
