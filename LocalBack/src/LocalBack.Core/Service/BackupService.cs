using System.Runtime.InteropServices;
using LocalBack.Core.Drives;
using LocalBack.Core.Engine;
using LocalBack.Core.Indexing;
using LocalBack.Core.Model;
using LocalBack.Core.Retention;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;
using LocalBack.Core.Watching;

namespace LocalBack.Core.Service;

/// <summary>
/// Runs LocalBack in the background: watchers, schedule, drive arrival, the single low-priority
/// worker thread that does all copying, and the cached status the UI reads.
/// </summary>
public sealed class BackupService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly Dictionary<string, SetWatcher> _watchers = new();
    private readonly Dictionary<string, WorkItem> _work = new();
    private readonly Dictionary<string, Cached> _cache = new();
    private readonly Dictionary<string, DateTimeOffset> _lowSpaceWarned = new();
    private readonly Dictionary<string, long> _lastCopied = new();
    private readonly Dictionary<string, int> _deferredRetries = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private readonly Timer _scheduleTimer;
    private volatile bool _stopping;
    private string? _runningSetId;
    private BackupProgress? _progress;

    public AppSettings Settings { get; private set; }
    public BackupEngine Engine { get; }
    public LocalIndex Index { get; }

    /// <summary>Set by the app: true when on battery power.</summary>
    public Func<bool> IsOnBattery { get; set; } = () => false;

    /// <summary>Minimum gap between live runs while throttled on battery.</summary>
    public TimeSpan BatteryInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Delay before files that were in use are tried again; doubles on each failed retry up to <see cref="DeferredRetryMax"/>.</summary>
    public TimeSpan DeferredRetry { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan DeferredRetryMax { get; set; } = TimeSpan.FromMinutes(30);

    public event Action? StatusChanged;
    public event Action<LowSpaceInfo>? LowSpace;
    /// <summary>Messages worth a tray balloon: (title, text, isError).</summary>
    public event Action<string, string, bool>? Notify;

    private sealed class WorkItem
    {
        public bool Full;
        public SnapshotTrigger Trigger;
        public readonly HashSet<string> Paths = new(PathUtil.Comparer);
        public readonly List<TaskCompletionSource<BackupResult?>> Waiters = new();
        public DateTimeOffset NotBefore;
    }

    private sealed record Cached(DriveStore? Drive, SnapshotInfo? Latest, int Versions, DateTimeOffset Refreshed);

    public BackupService(AppPaths paths)
    {
        _paths = paths;
        Log.Init(paths.DataDir);
        Settings = SettingsStore.Load(paths.SettingsFile);
        Index = new LocalIndex(paths.IndexFile);
        Engine = new BackupEngine(Index, paths.TempDir);
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "LocalBack worker", Priority = ThreadPriority.BelowNormal };
        _scheduleTimer = new Timer(_ => OnScheduleTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start(bool checkOnStartup = true)
    {
        Log.Info($"LocalBack starting with {Settings.Sets.Count} sets");
        _worker.Start();
        foreach (var set in Sets) StartWatching(set);
        RefreshAll();
        if (checkOnStartup)
        {
            // Changes made while LocalBack was not running are only found by looking.
            foreach (var set in Sets)
                if (GetCached(set).Drive != null) Enqueue(set, full: true, SnapshotTrigger.Startup);
        }
        ArmSchedule();
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        _scheduleTimer.Dispose();
        lock (_gate)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
        if (_worker.IsAlive) _worker.Join(TimeSpan.FromSeconds(5));
        Index.Dispose();
    }

    public IReadOnlyList<BackupSet> Sets
    {
        get { lock (_gate) return Settings.Sets.ToList(); }
    }

    public BackupSet? FindSet(string id)
    {
        lock (_gate) return Settings.Sets.FirstOrDefault(s => s.Id == id);
    }

    public void SaveSettings(Action<AppSettings>? change = null)
    {
        lock (_gate)
        {
            change?.Invoke(Settings);
            SettingsStore.Save(_paths.SettingsFile, Settings);
        }
        ArmSchedule();
        RaiseStatus();
    }

    // ------------------------------------------------------------------ sets

    /// <summary>Creates a set on a drive and queues its first backup.</summary>
    public BackupSet AddSet(string name, IEnumerable<string> folders, string driveRoot, RunSchedule schedule,
        IEnumerable<string> enabledRules, IEnumerable<string> customPatterns, bool keepHistory)
    {
        var roots = NormalizeFolders(folders);
        var driveRef = DriveLocator.Register(driveRoot);
        var store = DriveStore.TryOpen(driveRoot) ?? DriveStore.OpenOrCreate(driveRoot);
        BackupSet set;
        lock (_gate)
        {
            var baseId = PathUtil.Slug(name);
            var id = baseId;
            for (int i = 2; store.Set(id).Exists || Settings.Sets.Any(s => s.Id == id); i++) id = $"{baseId}-{i}";
            set = new BackupSet
            {
                Id = id,
                Name = name.Trim(),
                Folders = roots,
                Drive = driveRef,
                Schedule = schedule,
                EnabledRules = enabledRules.ToList(),
                CustomPatterns = customPatterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList(),
                KeepHistory = keepHistory,
            };
            store.Set(id).SaveDefinition(set);
            Settings.Sets.Add(set);
            SettingsStore.Save(_paths.SettingsFile, Settings);
        }
        Log.Info($"Added set {set.Name} ({set.Id}) on {driveRoot}");
        StartWatching(set);
        Refresh(set);
        Enqueue(set, full: true, SnapshotTrigger.FirstBackup);
        return set;
    }

    /// <summary>Normalises source folders and rejects one inside another (files under it would be stored twice).</summary>
    public static List<string> NormalizeFolders(IEnumerable<string> folders)
    {
        var roots = folders.Select(PathUtil.NormalizeFolder).Distinct(PathUtil.Comparer).ToList();
        foreach (var a in roots)
        {
            var parent = roots.FirstOrDefault(b => !PathUtil.Comparer.Equals(a, b) && PathUtil.IsUnder(a, b));
            if (parent != null) throw new ArgumentException($"{a} is inside {parent}; add just {parent}.");
        }
        return roots;
    }

    /// <summary>Saves changes to a set's folders, schedule or exclusions.</summary>
    public void UpdateSet(BackupSet updated)
    {
        updated.Folders = NormalizeFolders(updated.Folders);
        lock (_gate)
        {
            int i = Settings.Sets.FindIndex(s => s.Id == updated.Id);
            if (i < 0) return;
            Settings.Sets[i] = updated;
            SettingsStore.Save(_paths.SettingsFile, Settings);
        }
        StopWatching(updated.Id);
        StartWatching(updated);
        if (updated.Enabled) Enqueue(updated, full: true, SnapshotTrigger.Manual);
        RaiseStatus();
    }

    /// <summary>Turns backing up on or off for a set; off keeps its history available for browsing and restore.</summary>
    public void SetEnabled(BackupSet set, bool enabled)
    {
        BackupSet? live;
        lock (_gate)
        {
            live = Settings.Sets.FirstOrDefault(s => s.Id == set.Id);
            if (live == null) return;
            live.Enabled = enabled;
            SettingsStore.Save(_paths.SettingsFile, Settings);
        }
        if (enabled)
        {
            StartWatching(live);
            Enqueue(live, full: true, SnapshotTrigger.Manual);
        }
        else
        {
            StopWatching(live.Id);
            lock (_gate) _work.Remove(live.Id);
        }
        Log.Info($"{(enabled ? "Enabled" : "Disabled")} backing up {live.Name}");
        RaiseStatus();
    }

    /// <summary>Stops backing up a set. With <paramref name="deleteBackups"/> its snapshots are removed from the drive too.</summary>
    public async Task RemoveSetAsync(BackupSet set, bool deleteBackups)
    {
        StopWatching(set.Id);
        lock (_gate)
        {
            Settings.Sets.RemoveAll(s => s.Id == set.Id);
            _work.Remove(set.Id);
            _cache.Remove(set.Id);
            SettingsStore.Save(_paths.SettingsFile, Settings);
        }
        Index.RemoveSet(set.Id);
        if (deleteBackups && DriveLocator.Find(set.Drive) is { } drive)
        {
            await Task.Run(async () =>
            {
                await Engine.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var dir = drive.Set(set.Id).Dir;
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    RetentionPlanner.CollectGarbage(drive);
                }
                finally { Engine.Gate.Release(); }
            }).ConfigureAwait(false);
        }
        Log.Info($"Removed set {set.Name}{(deleteBackups ? " and its backups" : "")}");
        RaiseStatus();
    }

    /// <summary>Sets on a drive that this PC does not know about (e.g. made on a PC that died).</summary>
    public List<BackupSet> ForeignSets(DriveStore drive)
    {
        var known = Sets.Select(s => s.Id).ToHashSet();
        var list = new List<BackupSet>();
        foreach (var ss in drive.Sets())
        {
            if (known.Contains(ss.Id)) continue;
            if (ss.LoadDefinition() is { } def) list.Add(def);
        }
        return list;
    }

    /// <summary>Adds a set found on a drive, so its history can be browsed and restored here.</summary>
    public BackupSet ImportSet(DriveStore drive, BackupSet definition, bool watch)
    {
        var set = SettingsStore.Clone(definition);
        set.Drive = DriveLocator.Register(drive.DriveRoot);
        set.Enabled = watch;
        lock (_gate)
        {
            Settings.Sets.Add(set);
            SettingsStore.Save(_paths.SettingsFile, Settings);
        }
        if (watch) StartWatching(set);
        Refresh(set);
        RaiseStatus();
        return set;
    }

    // ------------------------------------------------------------------ triggers

    /// <summary>"Back up now" for one set or all of them. Completes when the runs finish.</summary>
    public Task BackUpNowAsync(BackupSet? only = null)
    {
        var tasks = new List<Task>();
        foreach (var set in only != null ? new[] { only } : Sets.ToArray())
            if (set.Enabled) tasks.Add(Enqueue(set, full: true, SnapshotTrigger.Manual, force: true));
        return Task.WhenAll(tasks);
    }

    public bool IsPaused => Settings.PausedUntil is { } t && t > DateTimeOffset.Now;

    public void Pause(TimeSpan duration)
    {
        SaveSettings(s => s.PausedUntil = DateTimeOffset.Now + duration);
        Log.Info($"Paused until {Settings.PausedUntil:HH:mm}");
    }

    public void Resume()
    {
        SaveSettings(s => s.PausedUntil = null);
        FlushPending(SnapshotTrigger.Live);
    }

    /// <summary>Call when Windows reports a new volume (WM_DEVICECHANGE).</summary>
    public void OnDriveArrived(string? root = null)
    {
        foreach (var set in Sets)
        {
            var before = GetCached(set).Drive != null;
            Refresh(set);
            var now = GetCached(set).Drive;
            if (now != null && !before && (root == null || PathUtil.Comparer.Equals(PathUtil.NormalizeFolder(now.DriveRoot), PathUtil.NormalizeFolder(root))))
            {
                Log.Info($"Drive for {set.Name} arrived at {now.DriveRoot}");
                Enqueue(set, full: true, SnapshotTrigger.DrivePlugIn);
            }
        }
        RaiseStatus();
    }

    public void OnDriveRemoved()
    {
        RefreshAll();
        RaiseStatus();
    }

    private void OnWatcherBatch(BackupSet set, IReadOnlyList<string> paths)
    {
        var live = FindSet(set.Id);
        if (live == null) return;
        bool throttled = Settings.ThrottleOnBattery && IsOnBattery();
        if (IsPaused || live.Schedule != RunSchedule.Live || throttled || GetCached(live).Drive == null)
        {
            Index.AddPending(live.Id, paths, DateTimeOffset.UtcNow);
            RaiseStatus();
            ArmSchedule();
            return;
        }
        Enqueue(live, full: false, SnapshotTrigger.Live, paths: paths);
    }

    private void OnWatcherOverflow(SetWatcher w)
    {
        // Too many events at once (e.g. a big unzip): fall back to a metadata scan.
        if (FindSet(w.Set.Id) is { } set) Enqueue(set, full: true, SnapshotTrigger.Live);
    }

    private void FlushPending(SnapshotTrigger trigger)
    {
        foreach (var set in Sets)
        {
            var pending = Index.Pending(set.Id);
            if (pending.Count > 0 && GetCached(set).Drive != null)
                Enqueue(set, full: false, trigger, paths: pending);
        }
    }

    // ------------------------------------------------------------------ queue + worker

    private Task<BackupResult?> Enqueue(BackupSet set, bool full, SnapshotTrigger trigger, IEnumerable<string>? paths = null,
        bool force = false, DateTimeOffset? notBefore = null)
    {
        if (!set.Enabled) return Task.FromResult<BackupResult?>(null);
        if (IsPaused && !force)
        {
            if (paths != null) Index.AddPending(set.Id, paths, DateTimeOffset.UtcNow);
            return Task.FromResult<BackupResult?>(null);
        }
        var tcs = new TaskCompletionSource<BackupResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_work.TryGetValue(set.Id, out var item))
            {
                item = new WorkItem { Trigger = trigger, NotBefore = notBefore ?? DateTimeOffset.MinValue };
                _work[set.Id] = item;
            }
            else if (notBefore == null)
            {
                item.NotBefore = DateTimeOffset.MinValue;
            }
            if (full && !item.Full)
            {
                item.Full = true;
                item.Trigger = trigger;
            }
            if (paths != null) item.Paths.UnionWith(paths);
            item.Waiters.Add(tcs);
        }
        _signal.Set();
        RaiseStatus();
        return tcs.Task;
    }

    private void WorkerLoop()
    {
        if (OperatingSystem.IsWindows()) Native.EnterBackgroundMode();
        while (!_stopping)
        {
            (BackupSet Set, WorkItem Item)? next = null;
            TimeSpan wait = Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var (id, item) in _work)
                {
                    if (item.NotBefore > now)
                    {
                        var w = item.NotBefore - now;
                        if (wait == Timeout.InfiniteTimeSpan || w < wait) wait = w;
                        continue;
                    }
                    var set = Settings.Sets.FirstOrDefault(s => s.Id == id);
                    if (set == null || !set.Enabled)
                    {
                        // The set was removed or disabled while queued: drop the item and release anyone waiting on it.
                        _work.Remove(id);
                        foreach (var w in item.Waiters) w.TrySetResult(null);
                        break;
                    }
                    next = (set, item);
                    _work.Remove(id);
                    _runningSetId = id;
                    break;
                }
            }
            if (next == null)
            {
                _signal.WaitOne(wait);
                continue;
            }
            RaiseStatus();
            Run(next.Value.Set, next.Value.Item);
            lock (_gate) _runningSetId = null;
            _progress = null;
            RaiseStatus();
        }
    }

    private void Run(BackupSet set, WorkItem item)
    {
        BackupResult? result = null;
        try
        {
            var progress = new Progress(p => { _progress = p; });
            var drive = DriveLocator.Find(set.Drive);
            if (drive == null)
            {
                if (item.Paths.Count > 0) Index.AddPending(set.Id, item.Paths, DateTimeOffset.UtcNow);
                return;
            }
            var paths = item.Full ? null : item.Paths.ToList();
            result = Engine.BackupAsync(set, item.Trigger, paths, progress).GetAwaiter().GetResult();
            lock (_gate) _lastCopied[set.Drive.Id] = Math.Max(result.BytesCopied, _lastCopied.GetValueOrDefault(set.Drive.Id) / 2);

            if (result.Deferred.Count > 0)
            {
                // A file that stays open (a PST, a database) must not cost a run every 30 seconds: back off.
                int tries;
                lock (_gate) tries = _deferredRetries[set.Id] = _deferredRetries.GetValueOrDefault(set.Id) + 1;
                var delay = DeferredRetry * Math.Pow(2, Math.Min(tries - 1, 10));
                if (delay > DeferredRetryMax) delay = DeferredRetryMax;
                Enqueue(set, full: false, SnapshotTrigger.Live, paths: result.Deferred, notBefore: DateTimeOffset.UtcNow + delay);
            }
            else
            {
                lock (_gate) _deferredRetries.Remove(set.Id);
            }

            if (!set.KeepHistory && result.Snapshot != null && result.Modified + result.Deleted > 0)
                RetentionPlanner.KeepOnlyLatestAsync(Engine, drive, set.Id).GetAwaiter().GetResult();
            Refresh(set);
            CheckSpace(drive);
        }
        catch (DriveNotAvailableException)
        {
            if (item.Paths.Count > 0) Index.AddPending(set.Id, item.Paths, DateTimeOffset.UtcNow);
            Refresh(set);
        }
        catch (Exception ex)
        {
            Log.Error($"Backup of {set.Name} failed", ex);
            Index.RecordRun(set.Id, DateTimeOffset.UtcNow, false, ex.Message);
            if (item.Paths.Count > 0) Index.AddPending(set.Id, item.Paths, DateTimeOffset.UtcNow);
            Notify?.Invoke($"Backup of {set.Name} failed", ex.Message, true);
            Refresh(set);
        }
        finally
        {
            foreach (var w in item.Waiters) w.TrySetResult(result);
        }
    }

    private sealed class Progress : IProgress<BackupProgress>
    {
        private readonly Action<BackupProgress> _report;
        public Progress(Action<BackupProgress> report) => _report = report;
        public void Report(BackupProgress value) => _report(value);
    }

    // ------------------------------------------------------------------ space

    private void CheckSpace(DriveStore drive)
    {
        var (free, total) = drive.Space();
        if (total <= 0) return;
        long estimate;
        lock (_gate) estimate = _lastCopied.GetValueOrDefault(drive.Identity.Id);
        var threshold = Math.Max((long)(total * Settings.LowSpaceFraction), Settings.LowSpaceMinBytes);
        // Warn below the threshold, or when a run like the last one would not fit.
        if (free >= threshold && free >= estimate + threshold / 2) return;

        var name = DriveName(drive);
        if (Settings.AutoFreeSpace)
        {
            var preview = RetentionPlanner.Preview(drive, Settings.Retention, DateTimeOffset.Now);
            if (preview.BytesFreed > 0)
            {
                var freed = RetentionPlanner.ApplyAsync(Engine, drive, preview).GetAwaiter().GetResult();
                Notify?.Invoke($"{name} was almost full", $"Removed old versions ({Settings.Retention.Title.ToLowerInvariant()}). {Format.Size(freed)} freed.", false);
                RefreshAll();
                return;
            }
        }
        lock (_gate)
        {
            if (_lowSpaceWarned.TryGetValue(drive.Identity.Id, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(12)) return;
            _lowSpaceWarned[drive.Identity.Id] = DateTimeOffset.UtcNow;
        }
        LowSpace?.Invoke(new LowSpaceInfo(drive, name, free, total, estimate));
    }

    public LowSpaceInfo? SpaceInfo(DriveStore drive)
    {
        var (free, total) = drive.Space();
        long estimate;
        lock (_gate) estimate = _lastCopied.GetValueOrDefault(drive.Identity.Id);
        return new LowSpaceInfo(drive, DriveName(drive), free, total, estimate);
    }

    public Task<PrunePreview> PreviewAsync(DriveStore drive, RetentionPlan plan, CancellationToken ct = default) =>
        Task.Run(() => RetentionPlanner.Preview(drive, plan, DateTimeOffset.Now, null, ct), ct);

    public Task<List<PrunePreview>> PreviewAllAsync(DriveStore drive, IReadOnlyList<RetentionPlan> plans, CancellationToken ct = default) =>
        Task.Run(() => RetentionPlanner.PreviewAll(drive, plans, DateTimeOffset.Now, null, ct), ct);

    public async Task<long> FreeUpAsync(DriveStore drive, PrunePreview preview, CancellationToken ct = default)
    {
        var freed = await Task.Run(() => RetentionPlanner.ApplyAsync(Engine, drive, preview, ct), ct).ConfigureAwait(false);
        RefreshAll();
        RaiseStatus();
        return freed;
    }

    public static string DriveName(DriveStore drive)
    {
        var d = DriveLocator.Describe(drive.DriveRoot);
        return d?.DisplayName ?? drive.DriveRoot;
    }

    // ------------------------------------------------------------------ schedule

    private void ArmSchedule()
    {
        if (_stopping) return;
        var next = NextDue(DateTimeOffset.Now) - DateTimeOffset.Now;
        if (next < TimeSpan.FromSeconds(1)) next = TimeSpan.FromSeconds(1);
        if (next > TimeSpan.FromHours(1)) next = TimeSpan.FromHours(1); // re-check after sleep/clock changes
        try { _scheduleTimer.Change(next, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private DateTimeOffset NextDue(DateTimeOffset now)
    {
        var candidates = new List<DateTimeOffset> { NextDailyCheck(now) };
        if (Settings.PausedUntil is { } p && p > now) candidates.Add(p);
        foreach (var set in Sets)
        {
            var last = Index.GetState(set.Id).LastRun ?? now;
            if (set.Schedule == RunSchedule.Hourly) candidates.Add(last + TimeSpan.FromHours(1));
            if (set.Schedule == RunSchedule.Live && Index.PendingCount(set.Id) > 0) candidates.Add(last + BatteryInterval);
        }
        return candidates.Min();
    }

    private DateTimeOffset NextDailyCheck(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.Date + Settings.DailyCheckAt, now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    private void OnScheduleTick()
    {
        try
        {
            var now = DateTimeOffset.Now;
            if (Settings.PausedUntil is { } p && p <= now)
            {
                SaveSettings(s => s.PausedUntil = null);
                FlushPending(SnapshotTrigger.Live);
            }
            if (IsPaused) return;

            var todayCheck = new DateTimeOffset(now.Date + Settings.DailyCheckAt, now.Offset);
            foreach (var set in Sets)
            {
                if (!set.Enabled) continue;
                var state = Index.GetState(set.Id);
                bool drivePresent = GetCached(set).Drive != null;
                if (!drivePresent) continue;

                if (set.Schedule != RunSchedule.OnPlugIn && now >= todayCheck && (state.LastFullCheck ?? DateTimeOffset.MinValue) < todayCheck)
                {
                    Enqueue(set, full: true, SnapshotTrigger.DailyCheck);
                    continue;
                }
                var last = state.LastRun ?? DateTimeOffset.MinValue;
                if (set.Schedule == RunSchedule.Hourly && now - last >= TimeSpan.FromHours(1) - TimeSpan.FromSeconds(30))
                {
                    Enqueue(set, full: true, SnapshotTrigger.Scheduled);
                    continue;
                }
                if (set.Schedule == RunSchedule.Live && now - last >= BatteryInterval)
                {
                    var pending = Index.Pending(set.Id);
                    if (pending.Count > 0) Enqueue(set, full: false, SnapshotTrigger.Live, paths: pending);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Schedule tick failed", ex);
        }
        finally
        {
            ArmSchedule();
        }
    }

    // ------------------------------------------------------------------ watchers

    private void StartWatching(BackupSet set)
    {
        lock (_gate)
        {
            if (_watchers.ContainsKey(set.Id) || !set.Enabled) return;
            try
            {
                var w = new SetWatcher(set, TimeSpan.FromSeconds(Math.Clamp(Settings.DebounceSeconds, 1, 30)), OnWatcherBatch);
                w.Overflow += OnWatcherOverflow;
                _watchers[set.Id] = w;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                Log.Error($"Cannot watch {set.Name}", ex);
            }
        }
    }

    private void StopWatching(string setId)
    {
        lock (_gate)
        {
            if (_watchers.Remove(setId, out var w)) w.Dispose();
        }
    }

    // ------------------------------------------------------------------ status

    public BackupProgress? CurrentProgress => _progress;
    public bool IsBusy
    {
        get { lock (_gate) return _runningSetId != null || _work.Count > 0; }
    }

    public string? RunningSetName
    {
        get { lock (_gate) return _runningSetId == null ? null : Settings.Sets.FirstOrDefault(s => s.Id == _runningSetId)?.Name; }
    }

    public DriveStore? DriveFor(BackupSet set) => GetCached(set).Drive;

    private Cached GetCached(BackupSet set)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(set.Id, out var c)) return c;
        }
        return Refresh(set);
    }

    public void RefreshAll()
    {
        foreach (var set in Sets) Refresh(set);
    }

    private Cached Refresh(BackupSet set)
    {
        DriveStore? drive = null;
        SnapshotInfo? latest = null;
        int versions = 0;
        try
        {
            drive = DriveLocator.Find(set.Drive);
            if (drive != null)
            {
                var snaps = drive.Set(set.Id).Snapshots();
                latest = snaps.LastOrDefault();
                versions = snaps.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read drive for {set.Name}: {ex.Message}");
        }
        var c = new Cached(drive, latest, versions, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            if (Settings.Sets.Any(s => s.Id == set.Id)) _cache[set.Id] = c;
        }
        return c;
    }

    public SetStatus GetStatus(BackupSet set)
    {
        var c = GetCached(set);
        var state = Index.GetState(set.Id);
        int pending = Index.PendingCount(set.Id);
        bool queued;
        bool running;
        lock (_gate)
        {
            running = _runningSetId == set.Id;
            queued = _watchers.TryGetValue(set.Id, out var w) && w.Queued > 0;
        }
        SetHealth health =
            running ? SetHealth.Running
            : !set.Enabled ? SetHealth.Disabled
            : c.Drive == null ? SetHealth.DriveMissing
            : IsPaused ? SetHealth.Paused
            : state.LastError != null ? SetHealth.Error
            : pending > 0 || queued ? SetHealth.Pending
            : c.Latest == null ? SetHealth.NeverRun
            : SetHealth.UpToDate;
        var driveName = c.Drive != null ? DriveName(c.Drive) : string.IsNullOrEmpty(set.Drive.Label) ? set.Drive.LastRoot : set.Drive.Label;
        return new SetStatus(set, health, state.LastRun, pending, state.LastError, c.Latest, c.Versions, driveName);
    }

    public List<SetStatus> GetStatuses() => Sets.Select(GetStatus).ToList();

    private void RaiseStatus()
    {
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("Status handler failed", ex); }
    }

    private static class Native
    {
        private const int ThreadModeBackgroundBegin = 0x00010000;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        /// <summary>Low CPU and low I/O priority for the copying thread, so saving in other apps stays snappy.</summary>
        public static void EnterBackgroundMode()
        {
            try { SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin); }
            catch (EntryPointNotFoundException) { }
        }
    }
}
