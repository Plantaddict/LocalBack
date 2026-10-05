using System.Collections.ObjectModel;
using System.Windows.Input;
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

    private async Task RefreshAsync()
    {
        var service = _app.Service;
        var rows = await Task.Run(() =>
        {
            var list = new List<DriveRowViewModel>();
            foreach (var d in DriveLocator.ListDrives().Where(d => !d.IsSystem || d.HasStore))
            {
                var store = d.HasStore ? DriveStore.TryOpen(d.Root) : null;
                var mine = store == null ? new List<BackupSet>()
                    : service.Sets.Where(s => s.Drive.Id == store.Identity.Id).ToList();
                var foreign = store == null ? new List<BackupSet>() : service.ForeignSets(store);
                list.Add(new DriveRowViewModel(this, d, store, mine, foreign));
            }
            return list;
        });
        Drives.Clear();
        foreach (var r in rows) Drives.Add(r);
        Raise(nameof(IsEmpty));
    }

    internal void FreeUp(DriveStore store) => _app.ShowFreeSpace(store, lowSpace: false);

    internal void Import(DriveStore store, BackupSet set)
    {
        bool watch = set.Folders.All(Directory.Exists) &&
                     Ui.Confirm($"\"{set.Name}\" was backed up from {string.Join(", ", set.Folders)}.\n\n" +
                                "Those folders exist on this PC. Keep backing them up from here?\n" +
                                "Choose Cancel to only browse and restore its history.", "Add backup set");
        var added = _app.Service.ImportSet(store, set, watch);
        _main.ShowHistory(added, null);
    }
}

public sealed class DriveRowViewModel
{
    public DriveRowViewModel(DrivesViewModel owner, DriveCandidate d, DriveStore? store, List<BackupSet> mine, List<BackupSet> foreign)
    {
        Name = d.DisplayName;
        Details = string.Join(" · ", new[] { d.IsRemovable ? "Removable" : "Fixed", d.Format }.Where(s => !string.IsNullOrEmpty(s)));
        UsedPercent = d.Total > 0 ? 100.0 * (d.Total - d.Free) / d.Total : 0;
        FreeText = d.Total > 0 ? $"{Format.Size(d.Free)} free of {Format.Size(d.Total)}" : "";
        Sets = mine.Count > 0 ? "Backup drive for " + string.Join(", ", mine.Select(s => s.Name)) : store != null ? "Has LocalBack backups" : "Not used for backups";
        Foreign = foreign.Select(f => new ForeignSet(f, new RelayCommand(() => owner.Import(store!, f)))).ToList();
        IsBackupDrive = store != null;
        FreeUp = new RelayCommand(() => owner.FreeUp(store!), () => store != null);
    }

    public string Name { get; }
    public string Details { get; }
    public double UsedPercent { get; }
    public string FreeText { get; }
    public string Sets { get; }
    public bool IsBackupDrive { get; }
    public List<ForeignSet> Foreign { get; }
    public bool HasForeign => Foreign.Count > 0;
    public ICommand FreeUp { get; }
}

public sealed record ForeignSet(BackupSet Set, ICommand Add)
{
    public string Text => $"{Set.Name} — from {string.Join(", ", Set.Folders)}";
}
