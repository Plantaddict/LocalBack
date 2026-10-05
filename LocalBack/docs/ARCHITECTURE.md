# LocalBack architecture

## Storage layout on the backup drive

```
E:\LocalBack\
  drive.json                  id of this drive (matched with the volume serial, never the letter)
  objects\ab\cdef0123...      one blob per unique file content, named by SHA-256
  objects\tmp\                copies in progress; cleared at the start of every run
  sets\Desktop\
    manifests\2026-10-05T14-32-11-123Z.json.gz
    manifests\2026-10-05T14-19-02-845Z.json.gz
    snapshots.jsonl           one summary line per snapshot (cache, rebuilt if stale)
    set.json                  name, source folders, exclusions, schedule
```

Manifest names sort chronologically and never collide (a name is bumped by a millisecond if needed). Every file is written to a temp name and renamed, so an unplugged drive never leaves a half-written manifest or blob.

A manifest lists every file in the set at that moment: relative path, hash, size, mtime, attributes. A snapshot is therefore just a manifest; unchanged files cost no extra space. Works on FAT32/exFAT because nothing relies on hard links or NTFS features.

Hashing: SHA-256 (or BLAKE3 if Rust). Hash only when size or mtime differs from the index.

## Local index (SQLite, on the PC)

- `files(set, root, path, size, mtime, attr, hash)` mirror of the newest manifest, for fast change detection.
- `versions(set, root, path, hash, size, first_seen, superseded)` for per-file history queries.
- `pending(set, path)` paths queued while the drive is unplugged, the app is paused, or the set is not on a live schedule.
- `sets_state(set, last_manifest, last_run, last_full, last_error)`.

The index is a cache. Before each run its `last_manifest` is compared with the newest manifest on the drive; if they differ (new PC, deleted index, another PC used the drive) the set's rows are rebuilt from the drive.

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

## Process model

One background service (`BackupService`) owns the watchers, a schedule timer (armed for the next due time, not polling) and a single worker thread at background CPU and I/O priority. Watcher batches, plug-in events, the daily check and "Back up now" all go into one queue that coalesces work per set. Engine operations take one lock, so garbage collection never races a run that is adding blobs.

The WPF app is a thin shell over that service. Windows are created when opened and dropped on close.

## Locked files (later)

v1 skips and retries. v2 can add Volume Shadow Copy via AlphaVSS for open Outlook PSTs and similar.

## Packaging

Single-file, self-contained, ReadyToRun publish (`LocalBack.exe`, plus `localback-cli.exe`). Per-user Inno Setup installer in `installer/`, installing to `%LOCALAPPDATA%\Programs\LocalBack`; no admin. The app writes its own Run-key autostart and Explorer menu entries (HKCU) according to Settings; the uninstaller removes them.

## Memory

Windows are created when opened and released when closed. When no window is open and no backup is running, the app compacts the managed heap and trims its working set (`SetProcessWorkingSetSize(-1, -1)`), so idle memory in Task Manager stays low; pages come back on demand when a window opens.
