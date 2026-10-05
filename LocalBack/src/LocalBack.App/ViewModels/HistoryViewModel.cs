using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LocalBack.App.Localization;
using LocalBack.App.Services;
using LocalBack.Core.Engine;
using LocalBack.Core.Model;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>"Version history": snapshots of a set on the left, files of the chosen snapshot on the right.</summary>
public sealed class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly App _app = App.Current;
    private readonly MainViewModel _main;
    private BackupSet? _set;
    private SnapshotItem? _snapshot;
    private SnapshotDetails? _details;
    private string _search = "";
    private bool _onlyChanged = true;
    private string _message = "";
    private string _status = "";
    private bool _loading;
    private CancellationTokenSource? _loadCts;
    private string? _pendingFocusPath;
    private int _snapshotsLoad;
    private bool _showDeleted;
    private List<SnapshotFile>? _deleted;

    public ObservableCollection<BackupSet> Sets { get; } = new();
    public ObservableCollection<SnapshotItem> Snapshots { get; } = new();
    private ObservableCollection<FileRowViewModel> _files = new();
    public ObservableCollection<FileRowViewModel> Files { get => _files; private set => Set(ref _files, value); }

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        Back = new RelayCommand(() => _main.Navigate(Page.Sets));
        RestoreEverything = new AsyncCommand(RestoreEverythingAsync, () => _details != null && !_loading);
        RestoreToFolder = new AsyncCommand(RestoreToFolderAsync, () => _details != null && !_loading);
        UnlockCommand = new RelayCommand(() =>
        {
            if (_set != null && _app.Service.DriveFor(_set) is { } d && _app.ShowUnlock(d)) _ = LoadSnapshotsAsync();
        });
        RefreshSets();
    }

    public ICommand Back { get; }
    public ICommand RestoreEverything { get; }
    public ICommand RestoreToFolder { get; }
    public ICommand UnlockCommand { get; }
    private bool _showUnlock;
    public bool ShowUnlock { get => _showUnlock; private set => Set(ref _showUnlock, value); }

    public BackupSet? SelectedSet
    {
        get => _set;
        set
        {
            if (value == null || ReferenceEquals(value, _set)) return;
            _set = value;
            Raise();
            _ = LoadSnapshotsAsync();
        }
    }

    public SnapshotItem? SelectedSnapshot
    {
        get => _snapshot;
        set
        {
            if (ReferenceEquals(value, _snapshot)) return;
            if (_snapshot != null) _snapshot.IsSelected = false;
            _snapshot = value;
            if (_snapshot != null) _snapshot.IsSelected = true;
            Raise();
            RaiseHeader();
            _ = LoadDetailsAsync();
        }
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    public bool OnlyChanged
    {
        get => _onlyChanged;
        set { if (Set(ref _onlyChanged, value)) ApplyFilter(); }
    }

    /// <summary>Lists every file that is gone from the folder with its last copy, instead of one snapshot's files.</summary>
    public bool ShowDeleted
    {
        get => _showDeleted;
        set
        {
            if (!Set(ref _showDeleted, value)) return;
            RaiseHeader();
            if (value) _ = LoadDeletedAsync();
            else { _deleted = null; ApplyFilter(); }
        }
    }

    /// <summary>Shown instead of the lists when there is nothing to show (no drive, no snapshots).</summary>
    public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => Message.Length > 0;

    /// <summary>Result of the last action, in the footer.</summary>
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(FooterText)); } }

    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    public string Header => _set == null ? Loc.T("history.title")
        : ShowDeleted ? Loc.T("history.deletedHeader", _set.Name)
        : _snapshot == null ? _set.Name : $"{_set.Name} — {_snapshot.Label}";
    public string SubHeader => ShowDeleted ? Loc.T("history.deletedSub")
        : _snapshot == null || _details == null ? (_snapshot?.Meta ?? "")
        : $"{_snapshot.Meta} · {Format.Plural(_details.Info.Files, "file", "files")} · {Format.Size(_details.Info.Bytes)}";

    public string FooterText => Status.Length > 0 ? Status : Loc.T("history.footer");

    public string SourceText => _set != null && _app.Service.DriveFor(_set) is { } d
        ? Loc.T("history.source", System.IO.Path.Combine(d.Root, "sets", _set.Id)) : "";

    public string EmptyFilesText => ShowDeleted ? (_deleted == null ? "" : Search.Length > 0 ? Loc.T("history.emptySearch") : Loc.T("history.emptyDeleted"))
        : _details == null ? ""
        : Search.Length > 0 ? Loc.T("history.emptySearch")
        : OnlyChanged ? Loc.T("history.emptyChanged")
        : Loc.T("history.emptyNone");
    public bool FilesEmpty => (ShowDeleted ? _deleted != null : _details != null) && Files.Count == 0;

    private void RaiseHeader()
    {
        Raise(nameof(Header));
        Raise(nameof(SubHeader));
        Raise(nameof(SourceText));
    }

    public void Activate()
    {
        RefreshSets();
        if (_set == null && Sets.Count > 0) SelectedSet = Sets[0];
        else OnStatusChanged();
    }

    /// <summary>Called when a backup finished: picks up a new snapshot without losing the user's place.</summary>
    public void OnStatusChanged()
    {
        if (_set == null || _loading) return;
        var status = _app.Service.GetStatus(_set);
        var newest = status.Latest?.Name;
        var shown = Snapshots.FirstOrDefault()?.Info.Name;
        bool driveCameBack = HasMessage && status.Health is not (SetHealth.DriveMissing or SetHealth.Locked) && Snapshots.Count == 0 && newest != null;
        if (newest != shown || driveCameBack) _ = LoadSnapshotsAsync(keepSelection: true);
    }

    public void RefreshSets()
    {
        var sets = _app.Service.Sets;
        if (Sets.Select(s => s.Id).SequenceEqual(sets.Select(s => s.Id))) return;
        Sets.Clear();
        foreach (var s in sets) Sets.Add(s);
        if (_set != null) _set = Sets.FirstOrDefault(s => s.Id == _set.Id);
        Raise(nameof(SelectedSet));
    }

    /// <summary>Opens a set (and optionally a file from the Explorer menu).</summary>
    public void Show(BackupSet? set, string? path)
    {
        RefreshSets();
        if (path != null)
        {
            var found = BackupEngine.Locate(_app.Service.Sets, path);
            if (found == null)
            {
                Message = Loc.T("history.notInSet", path);
                return;
            }
            set = found.Value.Set;
            _pendingFocusPath = found.Value.Key.Path;
        }
        set = set == null ? Sets.FirstOrDefault() : Sets.FirstOrDefault(s => s.Id == set.Id);
        if (set == null)
        {
            Message = Loc.T("history.noSets");
            return;
        }
        if (ReferenceEquals(set, _set)) _ = LoadSnapshotsAsync();
        else SelectedSet = set;
    }

    private async Task LoadSnapshotsAsync(bool keepSelection = false)
    {
        var set = _set;
        if (set == null) return;
        // Only the newest request may fill the list; an older one finishing late would add duplicates.
        int load = ++_snapshotsLoad;
        var previous = keepSelection ? _snapshot?.Info.Name : null;
        Message = "";
        Status = "";
        ShowUnlock = false;
        Snapshots.Clear();
        if (!keepSelection)
        {
            Files = new ObservableCollection<FileRowViewModel>();
            _details = null;
            _snapshot = null;
            RaiseHeader();
        }
        Loading = true;
        try
        {
            var list = await Task.Run(() => _app.Service.Engine.ListSnapshots(set));
            if (load != _snapshotsLoad || !ReferenceEquals(set, _set)) return;
            foreach (var s in Enumerable.Reverse(list)) Snapshots.Add(new SnapshotItem(s, this));
            if (Snapshots.Count == 0)
            {
                Message = Loc.T("history.noSnapshots");
                return;
            }
            if (ShowDeleted)
            {
                _snapshot = Snapshots.FirstOrDefault(s => s.Info.Name == previous) ?? Snapshots[0];
                _snapshot.IsSelected = true;
                await LoadDeletedAsync();
                return;
            }
            var keep = previous == null ? null : Snapshots.FirstOrDefault(s => s.Info.Name == previous);
            if (keep != null)
            {
                _snapshot = null; // force the setter to reload the (possibly changed) details
                SelectedSnapshot = keep;
            }
            else SelectedSnapshot = Snapshots[0];
        }
        catch (DriveNotAvailableException)
        {
            Message = Loc.T("history.plugIn", _app.DriveDisplayName(set), set.Name);
        }
        catch (DriveLockedException)
        {
            Message = Loc.T("history.locked", _app.DriveDisplayName(set), set.Name);
            ShowUnlock = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Message = Loc.T("history.cantRead", ex.Message);
        }
        finally
        {
            if (load == _snapshotsLoad) Loading = false;
        }
    }

    private async Task LoadDetailsAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var set = _set;
        var snap = _snapshot;
        Files = new ObservableCollection<FileRowViewModel>();
        _details = null;
        if (set == null || snap == null) return;
        Loading = true;
        try
        {
            var details = await Task.Run(() => _app.Service.Engine.LoadSnapshot(set, snap.Info.Name), cts.Token);
            if (cts.IsCancellationRequested) return;
            _details = details;
            if (_pendingFocusPath != null)
            {
                // Coming from Explorer: show that file, whether or not it changed in this snapshot.
                _onlyChanged = false;
                _search = _pendingFocusPath;
                _pendingFocusPath = null;
                Raise(nameof(OnlyChanged));
                Raise(nameof(Search));
            }
            ApplyFilter();
            RaiseHeader();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotAvailableException or DriveLockedException or System.Security.Cryptography.CryptographicException)
        {
            Message = ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) Loading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task LoadDeletedAsync()
    {
        var set = _set;
        if (set == null) return;
        Loading = true;
        try
        {
            var list = await Task.Run(() => _app.Service.Engine.DeletedFiles(set));
            if (!ReferenceEquals(set, _set) || !ShowDeleted) return;
            _deleted = list;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            // Anything here would otherwise vanish with the discarded task; show it instead.
            Log.Error("Listing deleted files failed", ex);
            Message = ex.Message;
        }
        finally
        {
            Loading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyFilter()
    {
        var source = ShowDeleted ? _deleted : _details?.Files;
        if (source == null)
        {
            Files = new ObservableCollection<FileRowViewModel>();
            Raise(nameof(FilesEmpty));
            return;
        }
        var q = Search.Trim().Replace('\\', '/');
        var rows = new List<FileRowViewModel>();
        bool multiRoot = _details?.Roots.Count > 1 || (ShowDeleted && _set!.Folders.Count > 1);
        foreach (var f in source)
        {
            if (!ShowDeleted && OnlyChanged && f.Change == ChangeKind.Unchanged) continue;
            if (q.Length > 0 && f.Key.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(new FileRowViewModel(this, f, multiRoot));
            // The list virtualises, but building millions of rows still costs; cap and say so.
            if (rows.Count >= 5000) break;
        }
        // One collection swap instead of a change notification per row.
        Files = new ObservableCollection<FileRowViewModel>(rows);
        Raise(nameof(FilesEmpty));
        Raise(nameof(EmptyFilesText));
    }

    // ---- actions ----

    private async Task RestoreEverythingAsync()
    {
        if (_set == null || _snapshot == null) return;
        if (!Ui.Confirm(Loc.T("history.confirmAll", _set.Name, _snapshot.Label), Loc.T("history.confirmAllTitle"))) return;
        Status = Loc.T("history.restoring");
        var set = _set;
        var name = _snapshot.Info.Name;
        var r = await Task.Run(() => _app.Service.Engine.RestoreSnapshotAsync(set, name, null));
        Status = Summary(r, null);
        await LoadSnapshotsAsync();
        Status = Summary(r, null);
    }

    private async Task RestoreToFolderAsync()
    {
        if (_set == null || _snapshot == null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("history.restoreTo", _set.Name, _snapshot.Label) };
        if (dlg.ShowDialog() != true) return;
        Status = Loc.T("history.restoring");
        var set = _set;
        var name = _snapshot.Info.Name;
        var r = await Task.Run(() => _app.Service.Engine.RestoreSnapshotAsync(set, name, dlg.FolderName));
        Status = Summary(r, dlg.FolderName);
        if (r.Restored > 0) WindowsIntegration.OpenWithShell(dlg.FolderName);
    }

    internal async Task RestoreFileAsync(SnapshotFile file)
    {
        if (_set == null) return;
        Status = Loc.T("history.restoringFile", file.Name);
        var set = _set;
        var r = await Task.Run(() => _app.Service.Engine.RestoreAsync(set, new[] { (file.Key, file.Entry) }, null));
        Status = r.Failed.Count > 0 ? Loc.T("history.restoreFailedFile", file.Name)
            : r.Restored == 0 ? Loc.T("history.alreadyVersion", file.Name)
            : Loc.T("history.restoredFile", file.Name);
        var keep = Status;
        await LoadSnapshotsAsync(keepSelection: true);
        Status = keep;
    }

    internal async Task OpenFileAsync(SnapshotFile file)
    {
        if (_set == null) return;
        var set = _set;
        var label = file.DeletedAt is { } d ? Format.SnapshotLabel(d) : _snapshot?.Label ?? "version";
        var path = await Task.Run(() => _app.Service.Engine.ExtractForViewing(set, file.Entry, label));
        WindowsIntegration.OpenWithShell(path);
    }

    private static string Summary(RestoreResult r, string? folder)
    {
        var files = Format.Plural(r.Restored, "file", "files");
        var s = folder != null ? Loc.T("history.summary.restoredTo", files, folder) : Loc.T("history.summary.restored", files);
        if (r.Skipped > 0) s += Loc.T("history.summary.matched", r.Skipped);
        if (r.Failed.Count > 0) s += Loc.T("history.summary.failed", r.Failed.Count);
        return s;
    }

    public void Dispose() => _loadCts?.Cancel();
}

public sealed class SnapshotItem : ObservableObject
{
    private readonly HistoryViewModel _owner;
    private bool _selected;

    public SnapshotItem(SnapshotInfo info, HistoryViewModel owner)
    {
        Info = info;
        _owner = owner;
        Pick = new RelayCommand(() => _owner.SelectedSnapshot = this);
    }

    public SnapshotInfo Info { get; }
    public ICommand Pick { get; }
    public string Label => Format.SnapshotLabel(Info.CreatedUtc);
    public string Meta => Loc.T("history.meta", Ui.TriggerText(Info.Trigger), Format.Plural(Info.Changed, "changed", "changed"));
    public string Size => Format.Size(Info.Bytes);

    public bool IsSelected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Raise(nameof(Background)); Raise(nameof(Weight)); } }
    }

    public Brush Background => IsSelected ? Ui.Brush("AccentTint") : Brushes.Transparent;
    public System.Windows.FontWeight Weight => IsSelected ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
}

public sealed class FileRowViewModel
{
    private readonly SnapshotFile _file;

    public FileRowViewModel(HistoryViewModel owner, SnapshotFile file, bool showRoot)
    {
        _file = file;
        Open = new AsyncCommand(() => owner.OpenFileAsync(file));
        Restore = new AsyncCommand(() => owner.RestoreFileAsync(file));
        ShowRoot = showRoot;
    }

    private bool ShowRoot { get; }
    public ICommand Open { get; }
    public ICommand Restore { get; }
    public string Name => _file.Name;
    public string Folder
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(_file.DisplayPath) ?? "";
            if (ShowRoot) dir = System.IO.Path.Combine(System.IO.Path.GetFileName(_file.Key.Root), dir);
            return dir;
        }
    }
    public bool HasFolder => Folder.Length > 0;
    public string FullPath => _file.Key.FullPath;
    public string Size => Format.Size(_file.Entry.Size);
    public string Change => _file.DeletedAt is { } d ? Loc.T("history.deletedAt", Format.When(d)) : Loc.T("change." + _file.Change);
    public Brush TagBackground => _file.Change switch
    {
        ChangeKind.Added => Ui.Brush("GreenTint"),
        ChangeKind.Modified => Ui.Brush("AccentTint"),
        ChangeKind.Deleted => Ui.Brush("RedTint"),
        _ => Ui.Brush("Panel2"),
    };
    public Brush TagForeground => _file.Change switch
    {
        ChangeKind.Added => Ui.Brush("Green"),
        ChangeKind.Modified => Ui.Brush("AccentDark"),
        ChangeKind.Deleted => Ui.Brush("Red"),
        _ => Ui.Brush("TextSecondary"),
    };
    /// <summary>A deleted file's last version is restored from the snapshot before.</summary>
    public string RestoreText => Loc.T(_file.Change == ChangeKind.Deleted ? "history.bringBack" : "history.restore");
}
