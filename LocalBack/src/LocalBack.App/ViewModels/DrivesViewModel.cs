using System.Collections.ObjectModel;
using System.Windows.Input;
using LocalBack.App.Localization;
using LocalBack.Core.Drives;
using LocalBack.Core.Model;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>"Drives": what is plugged in, which sets live where, and backups from other PCs.</summary>
public sealed class DrivesViewModel : ObservableObject
{
    private readonly App _app = App.Current;
    private readonly MainViewModel _main;

    public ObservableCollection<DriveRowViewModel> Drives { get; } = new();

    public DrivesViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ICommand RefreshCommand { get; }
    public bool IsEmpty => Drives.Count == 0;

    public void Refresh() => _ = RefreshAsync();

    private bool _refreshing;
    private DateTime _lastRefresh;

    /// <summary>Status ticks arrive throttled; the page only re-reads drives while a destination is being encrypted.</summary>
    public void OnStatusChanged()
    {
        if (_refreshing || DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(1)) return;
        if (!_app.Service.IsBusy) return;
        Refresh();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _lastRefresh = DateTime.UtcNow;
        try { await RefreshCoreAsync(); }
        finally { _refreshing = false; }
    }

    private async Task RefreshCoreAsync()
    {
        var service = _app.Service;
        var rows = await Task.Run(() =>
        {
            var list = new List<DriveRowViewModel>();
            var seen = new HashSet<string>(PathUtil.Comparer);
            foreach (var d in DriveLocator.ListDrives().Where(d => !d.IsSystem || d.HasStore))
            {
                seen.Add(PathUtil.NormalizeFolder(d.Root));
                list.Add(Row(service, d));
            }
            // Destinations that are folders or shares rather than drive roots.
            foreach (var set in service.Sets)
            {
                var root = set.Drive.LastRoot;
                if (string.IsNullOrEmpty(root) || !seen.Add(PathUtil.NormalizeFolder(root))) continue;
                var d = DriveLocator.Describe(root);
                if (d != null) list.Add(Row(service, d with { Label = "", IsRemovable = false }, isNetwork: set.Drive.IsNetwork));
            }
            return list;
        });
        Drives.Clear();
        foreach (var r in rows) Drives.Add(r);
        Raise(nameof(IsEmpty));
    }

    private DriveRowViewModel Row(BackupService service, DriveCandidate d, bool isNetwork = false)
    {
        var store = d.HasStore ? DriveStore.TryOpen(d.Root) : null;
        var mine = store == null ? new List<BackupSet>() : service.Sets.Where(s => s.Drive.Id == store.Identity.Id).ToList();
        var foreign = store == null || store.IsLocked ? new List<BackupSet>() : service.ForeignSets(store);
        return new DriveRowViewModel(this, d, store, mine, foreign, isNetwork);
    }

    internal void FreeUp(DriveStore store) => _app.ShowFreeSpace(store, lowSpace: false);

    internal void Unlock(DriveStore store)
    {
        if (_app.ShowUnlock(store)) Refresh();
    }

    internal void Protect(DriveStore store)
    {
        if (_app.ShowProtect(store)) Refresh();
    }

    internal void Import(DriveStore store, BackupSet set)
    {
        bool watch = set.Folders.All(Directory.Exists) &&
                     Ui.Confirm(Loc.T("drives.import.question", set.Name, string.Join(", ", set.Folders)), Loc.T("drives.import.title"));
        var added = _app.Service.ImportSet(store, set, watch);
        _main.ShowHistory(added, null);
    }
}

public sealed class DriveRowViewModel
{
    public DriveRowViewModel(DrivesViewModel owner, DriveCandidate d, DriveStore? store, List<BackupSet> mine, List<BackupSet> foreign, bool isNetwork)
    {
        Name = string.IsNullOrEmpty(d.Label) && d.Letter.Length > 2 ? d.Root : d.DisplayName;
        var kind = isNetwork ? Loc.T("drives.network") : d.IsRemovable ? Loc.T("drives.removable") : Loc.T("drives.fixed");
        var service = App.Current.Service;
        var encrypting = store is { IsEncrypting: true } ? service.EncryptProgress(store) : null;
        var crypto = encrypting is { } e ? (e.Total > 0 ? Loc.T("drives.encrypting", e.Done, e.Total) : Loc.T("drives.encryptingQueued"))
            : store is { IsEncrypted: true } ? Loc.T("drives.encrypted") : "";
        Details = string.Join(" · ", new[] { kind, d.Format, crypto }.Where(s => !string.IsNullOrEmpty(s)));
        UsedPercent = d.Total > 0 ? 100.0 * (d.Total - d.Free) / d.Total : 0;
        FreeText = d.Total > 0 ? Loc.T("drive.freeOf", Format.Size(d.Free), Format.Size(d.Total)) : "";
        Sets = store is { IsLocked: true } ? Loc.T("drives.locked")
            : mine.Count > 0 ? Loc.T("drives.backupFor", string.Join(", ", mine.Select(s => s.Name)))
            : store != null ? Loc.T("drives.hasBackups") : Loc.T("drives.notUsed");
        Foreign = foreign.Select(f => new ForeignSet(f, new RelayCommand(() => owner.Import(store!, f)))).ToList();
        IsBackupDrive = store != null && !store.IsLocked;
        IsLocked = store is { IsLocked: true };
        CanProtect = store is { IsEncrypted: false };
        FreeUp = new RelayCommand(() => owner.FreeUp(store!), () => store != null);
        Unlock = new RelayCommand(() => owner.Unlock(store!), () => store != null);
        Protect = new RelayCommand(() => owner.Protect(store!), () => store != null);
    }

    public string Name { get; }
    public string Details { get; }
    public double UsedPercent { get; }
    public string FreeText { get; }
    public string Sets { get; }
    public bool IsBackupDrive { get; }
    public bool IsLocked { get; }
    /// <summary>Holds backups but no password yet.</summary>
    public bool CanProtect { get; }
    public List<ForeignSet> Foreign { get; }
    public bool HasForeign => Foreign.Count > 0;
    public ICommand FreeUp { get; }
    public ICommand Unlock { get; }
    public ICommand Protect { get; }
}

public sealed record ForeignSet(BackupSet Set, ICommand Add)
{
    public string Text => Loc.T("drives.from", Set.Name, string.Join(", ", Set.Folders));
}
