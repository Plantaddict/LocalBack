# LocalBack architecture

## Storage layout on the backup drive

```
E:\LocalBack\
  objects\ab\cdef0123...      one blob per unique file content, named by hash
  sets\Desktop\
    manifests\2026-10-05T14-32-11Z.json
    manifests\2026-10-05T14-19-02Z.json
    set.json                  name, source folders, exclusions, schedule
```

A manifest lists every file in the set at that moment: relative path, hash, size, mtime, attributes. A snapshot is therefore just a manifest; unchanged files cost no extra space. Works on FAT32/exFAT because nothing relies on hard links or NTFS features.

Hashing: SHA-256 (or BLAKE3 if Rust). Hash only when size or mtime differs from the index.

## Local index (SQLite, on the PC)

- `files(set, path, size, mtime, hash)` current state, for fast change detection.
- `versions(set, path, hash, first_seen, last_seen)` for per-file history queries.
- `pending(set, path)` paths queued while the drive is unplugged.

## Change detection

1. `FileSystemWatcher` per source folder. Events go into a per-path queue; a path is processed only after 2–5 s of quiet. Duplicate events collapse.
2. Exclusion patterns (`*.tmp`, `~$*`, `*.crdownload`, `*.part`, `Thumbs.db`, `desktop.ini`, `node_modules/`, `.git/`, hidden+system attrs) are applied before any I/O.
3. Open with `FileShare.Read`; a sharing violation defers the file to the next pass. Zero-byte files younger than a few seconds are skipped.
4. Full rescan daily and on drive plug-in: metadata compare against the index, content read only on mismatch.
5. All reads/writes on one low-priority thread, `FileOptions.SequentialScan`, large buffers. Optional throttle on battery.

## Drives

Identify by volume serial number, not letter. Listen for `WM_DEVICECHANGE`; on arrival of a known drive, flush `pending` and run the full check.

## Restore

- **Restore everything:** read the chosen manifest, copy blobs back to the source folders. Before overwriting a file, snapshot the current version so the restore is undoable.
- **Restore one file:** same, one entry.
- **Open old version:** copy the blob to `%TEMP%\LocalBack\<snapshot>\<name>`, set read-only, `ShellExecute`. Cleaned up on exit.
- Explorer: registry context-menu entry that launches the exe with the path. No shell extension DLL.

## Retention and space

When free space drops below a threshold (or below the next run's estimate), show the Free up space dialog with three plans and the exact amount each frees:

1. Keep last N versions per file.
2. Keep one per day for 30 days, then one per week.
3. Drop versions older than X days.

Pruning edits manifests (removes the dropped entries), then a GC pass deletes blobs no manifest references. Safe to interrupt. The newest version of every file is never removed. The preview is the same walk without the delete step.

## Locked files (later)

v1 skips and retries. v2 can add Volume Shadow Copy via AlphaVSS for open Outlook PSTs and similar.

## Packaging

Single-file publish, Run-key autostart, MSIX or Inno Setup. No admin required.
