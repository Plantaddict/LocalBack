# LocalBack

A small Windows tray app that keeps a versioned backup of chosen folders on an external HDD or pendrive, and brings any file or folder back with one click.

## What it does

- Watches folders live (Desktop, Documents, project dirs) and backs up changed files within seconds of saving.
- Keeps a version history of every file; open any old version read-only, or restore it in place.
- Backs up to any external drive, including FAT32/exFAT pendrives. Runs automatically when the drive is plugged in.
- Stays out of the way: debounced change detection, temp-file exclusions, metadata-only rescans, low-priority I/O.
- Warns when the drive is almost full and offers to thin out old copies of the same file.

## Design

`design/` holds the UI mockups, made with the Claude Design canvas. Each screen is one self-contained `.dc.html` file; `canvas.json` lays them out.

| Screen | File |
| --- | --- |
| Main window: backup sets | `design/screens/Main.dc.html` |
| Version history and restore | `design/screens/History.dc.html` |
| Tray flyout | `design/screens/Tray.dc.html` |
| Add backup set dialog | `design/screens/AddSet.dc.html` |
| Free up space dialog | `design/screens/FreeSpace.dc.html` |

Look: Windows-native (Segoe UI, flat panels, single blue accent `#0F5FBF`). Status colours: green `#1F7A4D` up to date, amber `#B85C00` pending/warning, red `#A12A2A` deleted.

## Proposed stack

- **.NET 8, C#.** WPF for the windows, created lazily and disposed on close; WinForms `NotifyIcon` for the tray. Publish self-contained, ReadyToRun, trimmed. Target ~25 MB RAM idle.
- **Engine:** content-addressed blob store on the backup drive + JSON manifests per snapshot + SQLite index on the PC. See `docs/ARCHITECTURE.md` and `docs/BRIEF.md`.
- Alternative if size matters more than ship date: Rust (`tray-icon`, `notify`, `rusqlite`, `blake3`) with egui or Tauri 2.

## Status

Design and architecture only. No code yet.
