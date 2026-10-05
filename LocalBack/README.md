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

## Stack

- **.NET 8, C#.** WPF for the windows, created lazily and disposed on close; WinForms `NotifyIcon` for the tray. Publish self-contained, ReadyToRun, trimmed. Target ~25 MB RAM idle.
- **Engine:** content-addressed blob store on the backup drive + JSON manifests per snapshot + SQLite index on the PC. See `docs/ARCHITECTURE.md` and `docs/BRIEF.md`.
- Alternative if size matters more than ship date: Rust (`tray-icon`, `notify`, `rusqlite`, `blake3`) with egui or Tauri 2.

## Status

Milestones 1–5 are implemented: engine, live watching, tray and main window, version history, retention and Free up space.
Milestone 6 is partly done (autostart, Explorer menu, single-file publish); the installer and a measured perf pass against the 25 MB target are still open.

## Code

```
LocalBack.sln
src/
  LocalBack.Core/     engine, cross-platform (net8.0)
    Storage/          blob store, manifests, snapshot list, drive layout
    Indexing/         SQLite index on the PC
    Scanning/         exclusion rules, metadata-only folder walk
    Engine/           backup, restore, open old version
    Retention/        retention plans, exact preview, garbage collection
    Watching/         FileSystemWatcher + debounce
    Drives/           find drives by identity, not letter
    Service/          background service: queue, schedule, pause, low space, status
  LocalBack.App/      WPF tray app (net8.0-windows)
  LocalBack.Cli/      `localback` command line, same settings and index as the app
tests/
  LocalBack.Core.Tests/   xUnit tests for the engine and service
tools/make_icon.py        regenerates Assets/LocalBack.ico
```

## Build and run

Needs the .NET 8 SDK. The app builds on Windows only; the engine, CLI and tests build anywhere.

```
dotnet test tests/LocalBack.Core.Tests          # engine tests
dotnet run --project src/LocalBack.App           # tray app (Windows)
dotnet publish src/LocalBack.App -c Release -r win-x64 --self-contained -o publish
```

`.github/workflows/build.yml` runs the tests on Windows and Linux, builds the app and uploads a `LocalBack-win-x64` artifact.

App command line: `LocalBack.exe --tray` (start hidden, used by autostart), `LocalBack.exe --history <path>` (used by the Explorer menu).

## Command line

```
localback drives
localback add --name Desktop --drive E:\ --folder %USERPROFILE%\Desktop
localback backup [SET]
localback snapshots SET
localback files SET [SNAPSHOT] [--changed]
localback restore SET SNAPSHOT [--file PATH]... [--to FOLDER]
localback versions PATH
localback prune SET --plan last:3|daily|older:90 [--apply]
localback watch
```

Settings and the index live in `%LOCALAPPDATA%\LocalBack` (override with `LOCALBACK_HOME`).

## Decisions made while building

- **Manifests are gzip-compressed** (`*.json.gz`). Every snapshot lists every file, so this keeps live snapshots of large sets small on the drive.
- **`snapshots.jsonl`** next to the manifests caches one summary line per snapshot, so the history list does not open every manifest. It is rebuilt from the manifests if it is missing or stale.
- **`drive.json`** gives each backup drive an id. Drives are matched by that id or by volume serial, never by letter.
- **Snapshots are only written when content changed.** Touching a file without changing it updates the index, not the history.
- **Restore everything leaves files that were added later alone**, and snapshots what it overwrites first, so it can be undone.
- **Auto-prune is a standing policy** (Settings → Version retention), answering the first open question in the brief.
- **On battery**, live changes are batched to at most one run every 15 minutes (Settings, on by default).
- **Trimming is off**: WPF does not support it. The build is self-contained, single-file, ReadyToRun and compressed instead.
