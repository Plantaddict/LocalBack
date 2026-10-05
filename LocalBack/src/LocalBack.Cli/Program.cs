using LocalBack.Core.Drives;
using LocalBack.Core.Engine;
using LocalBack.Core.Model;
using LocalBack.Core.Retention;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.Cli;

/// <summary>Command-line front end to the engine. Shares settings and index with the tray app.</summary>
public static class Program
{
    private const string Usage = """
        LocalBack command line

          localback-cli drives                               List drives that can hold backups
          localback-cli add --name N --drive D --folder F [--folder F2] [--schedule live|hourly|daily|plugin]
                            [--exclude PATTERN] [--rules temp,lock,...] [--no-history] [--password PW]
                                                     D is a drive (E:\), a folder (E:\Backups) or a share (\\nas\backups).
                                                     --password encrypts a destination used for the first time.
          localback-cli sets                                 List backup sets and their status
          localback-cli backup [SET]                         Back up one set, or all
          localback-cli snapshots SET                        List snapshots, newest first
          localback-cli files SET [SNAPSHOT] [--changed]     List files in a snapshot (default: newest)
          localback-cli restore SET SNAPSHOT [--file PATH]... [--to FOLDER]
                                                             Restore a snapshot (in place unless --to)
          localback-cli versions PATH                        Versions of one file
          localback-cli prune SET --plan last:3|daily|older:90 [--apply]
                                                             Preview (or apply) thinning old versions on the set's drive
          localback-cli remove SET [--delete-backups]        Stop backing up a set
          localback-cli unlock SET [--password PW]           Enter the password of an encrypted destination
          localback-cli lock SET                             Forget the saved password on this PC
          localback-cli set-password SET                     Add a password to a destination, or change it
          localback-cli watch                                Run in the foreground: watch, schedule, back up

        SNAPSHOT is a name from "snapshots", "latest", or a number (1 = newest).
        Settings live in %LOCALAPPDATA%\LocalBack (override with LOCALBACK_HOME).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        try
        {
            var paths = new AppPaths();
            // Commands that write share the engine with the tray app; only one process may run it at a time.
            bool writes = args[0] is "add" or "backup" or "restore" or "prune" or "remove" or "watch" or "set-password";
            using var engineLock = writes ? EngineLock.TryAcquire(paths.DataDir) : null;
            if (writes && engineLock == null)
                return Fail("LocalBack is running. Use the app for this, or exit it from the tray icon first.");
            using var service = new BackupService(paths);
            var a = new Args(args.Skip(1));
            return args[0] switch
            {
                "drives" => Drives(),
                "add" => await Add(service, a),
                "sets" => Sets(service),
                "backup" => await Backup(service, a),
                "snapshots" => Snapshots(service, a),
                "files" => Files(service, a),
                "restore" => await Restore(service, a),
                "versions" => Versions(service, a),
                "prune" => await Prune(service, a),
                "remove" => await Remove(service, a),
                "unlock" => Unlock(service, a),
                "lock" => Lock(service, a),
                "set-password" => SetPassword(service, a),
                "watch" => Watch(service),
                _ => Fail($"Unknown command \"{args[0]}\". Run localback-cli --help."),
            };
        }
        catch (Exception ex) when (ex is DriveNotAvailableException or DriveLockedException)
        {
            return Fail(ex.Message + (ex is DriveLockedException ? " Run localback-cli unlock SET." : ""));
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Drives()
    {
        foreach (var d in DriveLocator.ListDrives())
        {
            var flags = string.Join(", ", new[] { d.IsRemovable ? "removable" : null, d.IsSystem ? "system" : null, d.HasStore ? "has backups" : null }.Where(f => f != null));
            Console.WriteLine($"{d.DisplayName,-30} {Format.Size(d.Free),9} free of {Format.Size(d.Total),-9} {d.Format,-6} {flags}");
        }
        return 0;
    }

    private static async Task<int> Add(BackupService service, Args a)
    {
        var name = a.Option("--name") ?? throw new ArgumentException("--name is required");
        var drive = a.Option("--drive") ?? throw new ArgumentException("--drive is required");
        var folders = a.Options("--folder");
        if (folders.Count == 0) throw new ArgumentException("At least one --folder is required");
        foreach (var f in folders)
            if (!Directory.Exists(f)) throw new ArgumentException($"Folder not found: {f}");
        if (!Directory.Exists(drive)) throw new ArgumentException($"Drive not found: {drive}");
        var schedule = (a.Option("--schedule") ?? "live") switch
        {
            "live" => RunSchedule.Live,
            "hourly" => RunSchedule.Hourly,
            "daily" => RunSchedule.Daily,
            "plugin" => RunSchedule.OnPlugIn,
            var s => throw new ArgumentException($"Unknown schedule {s}"),
        };
        var rules = a.Option("--rules") is { } r ? r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : ExclusionRules.DefaultEnabled.ToArray();
        foreach (var p in a.Options("--exclude"))
            if (LocalBack.Core.Scanning.ExclusionFilter.Validate(p) is { } err) throw new ArgumentException($"{p}: {err}");
        var password = a.Option("--password");
        var store = LocalBack.Core.Storage.DriveStore.TryOpen(PathUtil.NormalizeFolder(drive));
        if (store is null or { IsEncrypted: false } && password == null && !Console.IsInputRedirected)
        {
            Console.Write("Protect this destination with a password? Leave empty for no encryption: ");
            password = ReadPassword();
            if (password.Length > 0)
            {
                Console.Write("Repeat the password: ");
                if (ReadPassword() != password) throw new ArgumentException("The passwords do not match.");
            }
            else password = null;
        }
        if (store is { IsLocked: true } && password == null) password = AskPassword();
        var set = service.AddSet(name, folders, drive, schedule, rules, a.Options("--exclude"), !a.Flag("--no-history"), password);
        Console.WriteLine($"Added \"{set.Name}\" (id {set.Id}). Running the first backup…");
        var result = await service.Engine.BackupAsync(set, SnapshotTrigger.FirstBackup, null, ConsoleProgress());
        Print(result);
        return 0;
    }

    private static string AskPassword()
    {
        if (Console.IsInputRedirected) throw new ArgumentException("This destination is encrypted; pass --password.");
        Console.Write("Password: ");
        return ReadPassword();
    }

    private static string ReadPassword()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }

    private static int Unlock(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        var drive = LocalBack.Core.Drives.DriveLocator.Find(set.Drive) ?? throw new DriveNotAvailableException(set.Name, set.Drive.LastRoot);
        if (!drive.IsEncrypted) { Console.WriteLine("That destination is not encrypted."); return 0; }
        if (!drive.IsLocked) { Console.WriteLine("Already unlocked on this PC."); return 0; }
        var password = a.Option("--password") ?? AskPassword();
        if (!service.UnlockDrive(drive, password)) return Fail("Wrong password.");
        Console.WriteLine("Unlocked. The key is kept for this PC; use lock to forget it.");
        return 0;
    }

    private static int Lock(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        var drive = LocalBack.Core.Drives.DriveLocator.Find(set.Drive) ?? throw new DriveNotAvailableException(set.Name, set.Drive.LastRoot);
        service.LockDrive(drive);
        Console.WriteLine("The password will be asked for again.");
        return 0;
    }

    private static int SetPassword(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        var drive = LocalBack.Core.Drives.DriveLocator.Find(set.Drive) ?? throw new DriveNotAvailableException(set.Name, set.Drive.LastRoot);
        if (!drive.IsEncrypted)
        {
            // A destination used without a password so far: add one and encrypt what is already there.
            Console.Write("This destination is not encrypted. New password: ");
            var pw = ReadPassword();
            if (pw.Length < 8) return Fail("Use at least 8 characters.");
            Console.Write("Repeat the new password: ");
            if (ReadPassword() != pw) return Fail("The passwords do not match.");
            drive.Protect(pw);
            Console.WriteLine("Password set. Encrypting the backups already on it…");
            drive.EncryptPending(new Progress<(int Done, int Total)>(p =>
            {
                if (p.Total > 0 && (p.Done % 50 == 0 || p.Done == p.Total)) Console.WriteLine($"  {p.Done} of {p.Total}");
            }));
            Console.WriteLine("Done. Everything on this destination is now encrypted.");
            return 0;
        }
        Console.Write("Current password: ");
        var current = ReadPassword();
        Console.Write("New password: ");
        var next = ReadPassword();
        Console.Write("Repeat the new password: ");
        if (ReadPassword() != next) return Fail("The passwords do not match.");
        if (next.Length == 0) return Fail("The password cannot be empty.");
        if (!drive.ChangePassword(current, next)) return Fail("Wrong password.");
        Console.WriteLine("Password changed.");
        return 0;
    }

    private static int Sets(BackupService service)
    {
        var statuses = service.GetStatuses();
        if (statuses.Count == 0) Console.WriteLine("No backup sets yet. Add one with: localback-cli add --name Desktop --drive E:\\ --folder %USERPROFILE%\\Desktop");
        foreach (var s in statuses)
        {
            Console.WriteLine($"{s.Set.Name} [{s.Set.Id}]  {s.StatusText} · {s.WhenText}");
            Console.WriteLine($"    {s.FoldersText}");
            Console.WriteLine($"    {s.SizeText}, {s.VersionsText} on {s.DriveName} · {s.Set.ScheduleText}");
        }
        return 0;
    }

    private static async Task<int> Backup(BackupService service, Args a)
    {
        var sets = a.Positional(0) is { } id ? new[] { FindSet(service, id) } : service.Sets.ToArray();
        int failures = 0;
        foreach (var set in sets)
        {
            Console.WriteLine($"Backing up {set.Name}…");
            try
            {
                Print(await service.Engine.BackupAsync(set, SnapshotTrigger.Manual, null, ConsoleProgress()));
            }
            catch (DriveNotAvailableException ex)
            {
                Console.Error.WriteLine("  " + ex.Message);
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Print(BackupResult r)
    {
        Console.WriteLine(r.Snapshot != null
            ? $"  Snapshot {r.Snapshot.Name}: {r.Added} added, {r.Modified} modified, {r.Deleted} deleted; {Format.Size(r.BytesCopied)} copied"
            : "  Nothing changed.");
        Console.WriteLine($"  {Format.Plural(r.Files, "file", "files")}, {Format.Size(r.TotalBytes)} in the set.");
        foreach (var d in r.Deferred) Console.WriteLine($"  In use, will retry: {d}");
        foreach (var f in r.Failed) Console.WriteLine($"  Failed: {f}");
    }

    private static IProgress<BackupProgress>? ConsoleProgress() => Console.IsOutputRedirected ? null : new ConsoleBar();

    private sealed class ConsoleBar : IProgress<BackupProgress>
    {
        private DateTime _last;
        public void Report(BackupProgress p)
        {
            if (p.Phase == "Done") { Console.Write("\r" + new string(' ', 79) + "\r"); return; }
            if ((DateTime.UtcNow - _last).TotalMilliseconds < 200) return;
            _last = DateTime.UtcNow;
            var text = p.FilesTotal > 0 ? $"{p.Phase} {p.FilesDone}/{p.FilesTotal} {Format.Size(p.BytesCopied)}" : p.Phase;
            Console.Write("\r" + text.PadRight(79)[..79]);
        }
    }

    private static int Snapshots(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        var snaps = service.Engine.ListSnapshots(set);
        int n = 1;
        foreach (var s in Enumerable.Reverse(snaps))
            Console.WriteLine($"{n++,4}  {s.Name}  {Format.SnapshotLabel(s.CreatedUtc),-22} {s.TriggerText} · {s.Changed} changed · {Format.Plural(s.Files, "file", "files")} · {Format.Size(s.Bytes)}");
        return 0;
    }

    private static string ResolveSnapshot(BackupService service, BackupSet set, string? spec)
    {
        var snaps = service.Engine.ListSnapshots(set);
        if (snaps.Count == 0) throw new ArgumentException($"{set.Name} has no snapshots yet.");
        if (spec == null || spec == "latest") return snaps[^1].Name;
        if (int.TryParse(spec, out var n) && n >= 1 && n <= snaps.Count) return snaps[^n].Name;
        var match = snaps.FirstOrDefault(s => s.Name.StartsWith(spec, StringComparison.Ordinal));
        return match?.Name ?? throw new ArgumentException($"No snapshot {spec}");
    }

    private static int Files(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        var name = ResolveSnapshot(service, set, a.Positional(1));
        var details = service.Engine.LoadSnapshot(set, name);
        bool changedOnly = a.Flag("--changed");
        Console.WriteLine($"{set.Name} — {Format.SnapshotLabel(details.Info.CreatedUtc)} ({details.Info.Files} files, {Format.Size(details.Info.Bytes)})");
        foreach (var f in details.Files)
        {
            if (changedOnly && f.Change == ChangeKind.Unchanged) continue;
            Console.WriteLine($"  {f.Change,-9} {Format.Size(f.Entry.Size),9}  {f.Key.FullPath}");
        }
        return 0;
    }

    private static async Task<int> Restore(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        var name = ResolveSnapshot(service, set, a.Positional(1) ?? throw new ArgumentException("Which snapshot?"));
        var target = a.Option("--to");
        var only = a.Options("--file").Select(Path.GetFullPath).ToHashSet(PathUtil.Comparer);
        var details = service.Engine.LoadSnapshot(set, name);
        var files = details.Files
            .Where(f => f.Change != ChangeKind.Deleted)
            .Where(f => only.Count == 0 || only.Contains(f.Key.FullPath))
            .Select(f => (f.Key, f.Entry)).ToList();
        if (files.Count == 0) return Fail("Nothing to restore.");
        var result = await service.Engine.RestoreAsync(set, files, target, ConsoleProgress());
        Console.WriteLine($"Restored {result.Restored} files, {result.Skipped} were already current.");
        foreach (var f in result.Failed) Console.WriteLine($"  Failed: {f}");
        if (result.UndoSnapshot != null) Console.WriteLine($"To undo, restore snapshot {result.UndoSnapshot.Name}.");
        return result.Failed.Count == 0 ? 0 : 1;
    }

    private static int Versions(BackupService service, Args a)
    {
        var path = a.Positional(0) ?? throw new ArgumentException("Which file?");
        var found = BackupEngine.Locate(service.Sets, path) ?? throw new ArgumentException("That file is not in any backup set.");
        var versions = service.Engine.FileVersions(found.Set, found.Key);
        if (versions.Count == 0) Console.WriteLine("No versions backed up yet.");
        foreach (var v in versions)
            Console.WriteLine($"  {Format.When(v.FirstSeen),-22} {Format.Size(v.Size),9}  {v.Hash[..12]}{(v.Superseded == null ? "  (current)" : "")}");
        return 0;
    }

    private static async Task<int> Prune(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        var drive = service.Engine.RequireDrive(set);
        var plan = ParsePlan(a.Option("--plan") ?? "daily");
        var preview = await service.PreviewAsync(drive, plan);
        Console.WriteLine($"{plan.Title}: removes {preview.VersionsRemoved} old versions, frees {Format.Size(preview.BytesFreed)}.");
        Console.WriteLine($"Current files {Format.Size(preview.CurrentBytes)}, older versions {Format.Size(preview.OlderBytes)}.");
        foreach (var o in preview.Offenders.Take(3))
            Console.WriteLine($"  {o.Name}: {o.Versions} versions, {Format.Size(o.Bytes)}");
        if (!a.Flag("--apply"))
        {
            Console.WriteLine("Preview only. Add --apply to remove them.");
            return 0;
        }
        var freed = await service.FreeUpAsync(drive, preview);
        Console.WriteLine($"Done. {Format.Size(freed)} freed.");
        return 0;
    }

    internal static RetentionPlan ParsePlan(string spec)
    {
        var parts = spec.Split(':', 2);
        int Number(int fallback, string what)
        {
            if (parts.Length < 2) return fallback;
            if (!int.TryParse(parts[1], out var n) || n < 1 || n > 100000)
                throw new ArgumentException($"{what} must be a whole number of at least 1 (got \"{parts[1]}\").");
            return n;
        }
        return parts[0] switch
        {
            "last" => new RetentionPlan(RetentionKind.KeepLast, Count: Number(3, "Versions to keep")),
            "daily" => new RetentionPlan(RetentionKind.DailyThenWeekly),
            "older" => new RetentionPlan(RetentionKind.OlderThan, Days: Number(90, "Days")),
            _ => throw new ArgumentException($"Unknown plan {spec}. Use last:N, daily or older:DAYS."),
        };
    }

    private static async Task<int> Remove(BackupService service, Args a)
    {
        var set = FindSet(service, a.Positional(0) ?? throw new ArgumentException("Which set?"));
        await service.RemoveSetAsync(set, a.Flag("--delete-backups"));
        Console.WriteLine($"Removed {set.Name}.");
        return 0;
    }

    private static int Watch(BackupService service)
    {
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        string last = "";
        service.StatusChanged += () =>
        {
            var line = string.Join(" | ", service.GetStatuses().Select(s => $"{s.Set.Name}: {s.StatusText} ({s.WhenText})"));
            if (line != last) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            last = line;
        };
        service.Notify += (title, text, _) => Console.WriteLine($"{title}: {text}");
        service.LowSpace += i => Console.WriteLine($"{i.DriveName} is almost full: {Format.Size(i.Free)} free. Run localback-cli prune.");
        service.Start();
        Console.WriteLine("Watching. Press Ctrl+C to stop.");
        done.Wait();
        return 0;
    }

    private static BackupSet FindSet(BackupService service, string idOrName) =>
        service.Sets.FirstOrDefault(s => s.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? service.Sets.FirstOrDefault(s => s.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No backup set called {idOrName}. Run localback-cli sets.");

    private sealed class Args
    {
        private readonly List<string> _positional = new();
        private readonly List<(string Key, string? Value)> _options = new();

        public Args(IEnumerable<string> args)
        {
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].StartsWith("--", StringComparison.Ordinal))
                {
                    string? value = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal) && TakesValue(list[i]) ? list[++i] : null;
                    _options.Add((list[i - (value != null ? 1 : 0)], value));
                }
                else _positional.Add(list[i]);
            }
        }

        private static bool TakesValue(string key) => key is not ("--changed" or "--apply" or "--no-history" or "--delete-backups");

        public string? Positional(int i) => i < _positional.Count ? _positional[i] : null;
        public string? Option(string key) => _options.LastOrDefault(o => o.Key == key).Value;
        public List<string> Options(string key) => _options.Where(o => o.Key == key && o.Value != null).Select(o => o.Value!).ToList();
        public bool Flag(string key) => _options.Any(o => o.Key == key);
    }
}
