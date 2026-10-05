# LocalBack — project brief

**Date:** 5 October 2026
**Status:** Milestones 1–5 implemented; installer and perf pass open
**Design:** five screens on the LocalBack canvas (Main, Version history, Tray flyout, Add backup set, Free up space)

## One line

A lightweight Windows tray app that keeps a versioned backup of chosen folders on an external HDD or pendrive, and brings any file or folder back with one click.

## Problem

People keep important work on the Desktop and in Documents, own a USB drive, and still lose files. Existing tools are either cloud-first, heavy, or need setup nobody finishes. LocalBack should be the thing you install once, point at a drive, and forget.

## Goals

1. **Set and forget.** Pick folders, pick a drive, done. Backs up within seconds of a save and whenever the drive is plugged in.
2. **Version history.** Every save becomes a version. Open any old version read-only, or restore it in place. Restores are themselves undoable.
3. **One-click restore.** "Restore everything" puts a whole set back as it was at a chosen moment.
4. **Lightweight.** Idle in the tray at roughly 25 MB RAM and no CPU. No polling, no constant disk reads.
5. **Works on any drive.** NTFS, exFAT, FAT32 pendrives alike. No hard links, no NTFS-only features.
6. **Never fills the drive silently.** Warns before space runs out and offers to thin old copies of the same file, always keeping the newest version.

## Non-goals (v1)

- Cloud or network destinations
- Whole-disk or system-image backup
- Encryption at rest (consider for v2)
- Backing up open/locked files via Volume Shadow Copy (v2, AlphaVSS)
- macOS or Linux

## Screens

| Screen | Purpose | Key actions |
| --- | --- | --- |
| Main window | Overview of backup sets, drive usage, live status | Back up all now, Add set, per-set Back up / Restore |
| Version history | Snapshots for a set, files in a snapshot | Restore everything, Restore to folder…, per-file Open / Restore |
| Tray flyout | Status at a glance without opening the app | Back up now, Restore…, Pause 1 h, Open app |
| Add backup set | Name, folders, drive, schedule, exclusions | Toggle exclusion tiles, add custom pattern, Create and back up |
| Free up space | Appears when the drive is nearly full | Choose a retention plan, see exact space freed, apply automatically |

Look: Windows-native. Segoe UI, flat panels, one blue accent (#0F5FBF), green/amber/red for up to date / pending / deleted. One shared button system (primary, secondary, outline, ghost) with hover, press and focus states.

## How it works

**Storage on the drive.** Content-addressed: each unique file content is stored once as a blob named by its hash; each backup run writes a small JSON manifest (path → hash, size, mtime). A snapshot is just a manifest, so unchanged files cost nothing and version history is close to free.

**Change detection.** `FileSystemWatcher` per folder, debounced 2–5 s per path so one save produces one version. Exclusion rules (temp files, Office lock files, in-progress downloads, system files, dev folders, caches, size limits) are applied before any I/O. Files that can't be opened are deferred to the next pass. A daily metadata-only rescan, and a rescan on drive plug-in, catch anything the watcher missed.

**Drives.** Identified by volume serial, not letter. `WM_DEVICECHANGE` triggers a run on arrival. Pending changes queue while the drive is away.

**Restore.** Read the manifest, copy blobs back. The current file is snapshotted before being overwritten. "Open" extracts a read-only copy to `%TEMP%` and launches it with the default app. Explorer gets a registry context-menu entry, not a shell extension.

**Retention.** Three plans: keep last N versions per file; one per day for 30 days then one per week; drop versions older than X days. Pruning edits manifests, then garbage-collects unreferenced blobs. Safe to interrupt. The preview number is exact.

## Stack

- **.NET 8 / C#.** WPF windows created lazily and disposed on close; WinForms `NotifyIcon` for the tray. Self-contained, ReadyToRun, trimmed single-file publish.
- **SQLite** index on the PC for fast change detection and per-file history queries.
- **SHA-256** hashing, only when size or mtime differ.
- **Installer:** MSIX or Inno Setup; Run-key autostart; no admin.
- **Alternative** if footprint matters more than ship date: Rust with `tray-icon`, `notify`, `rusqlite`, `blake3`, egui or Tauri 2.

## Milestones

1. **Engine core** — store, manifests, index, full scan, restore. Command line only.
2. **Live watching** — debounce, exclusions, deferred files, drive detection.
3. **Tray + main window** — sets list, status, Back up / Restore.
4. **Version history** — snapshot list, file list, Open, per-file restore.
5. **Retention + Free up space** — plans, preview, GC, auto mode.
6. **Polish and installer** — autostart, Explorer menu, trimmed build, perf pass against the 25 MB target.

## Open questions

- Should the auto-prune checkbox apply the chosen plan once, or become the standing retention policy in Settings? (Leaning: standing policy, shown in Settings as "Version retention".)
- Default schedule on laptops: live, or hourly while on battery?
- Should a backup set be allowed to span two drives (e.g. mirror to both when present)?
- Encryption of the blob store for drives that leave the house.
