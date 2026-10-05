using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LocalBack.App.Services;
using LocalBack.Core.Engine;
using LocalBack.Core.Model;
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

    public ObservableCollection<BackupSet> Sets { get; } = new();
    public ObservableCollection<SnapshotItem> Snapshots { get; } = new();
    public ObservableCollection<FileRowViewModel> Files { get; } = new();

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        Back = new RelayCommand(() => _main.Navigate(Page.Sets));
        RestoreEverything = new AsyncCommand(RestoreEverythingAsync, () => _details != null && !_loading);
        RestoreToFolder = new AsyncCommand(RestoreToFolderAsync, () => _details != null && !_loading);
        RefreshSets();
    }

    public ICommand Back { get; }
    public ICommand RestoreEverything { get; }
    public ICommand RestoreToFolder { get; }

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

    /// <summary>Shown instead of the lists when there is nothing to show (no drive, no snapshots).</summary>
    public string Message { get => _message; private set { if (Set(ref _message, value)) Raise(nameof(HasMessage)); } }
    public bool HasMessage => Message.Length > 0;

    /// <summary>Result of the last action, in the footer.</summary>
    public string Status { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(FooterText)); } }

    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    public string Header => _set == null ? "Version history" : _snapshot == null ? _set.Name : $"{_set.Name} — {_snapshot.Label}";
    public string SubHeader => _snapshot == null || _details == null ? (_snapshot?.Meta ?? "")
        : $"{_snapshot.Meta} · {Format.Plural(_details.Info.Files, "file", "files")} · {Format.Size(_details.Info.Bytes)}";

    public string FooterText => Status.Length > 0 ? Status
        : "Open shows a read-only copy of that version. Restore replaces the current file; the replaced copy is kept as a new version.";

    public string SourceText => _set != null && _app.Service.DriveFor(_set) is { } d
        ? $"Source: {System.IO.Path.Combine(d.Root, "sets", _set.Id)}" : "";

    public string EmptyFilesText => _details == null ? ""
        : Search.Length > 0 ? "No files match your search."
        : OnlyChanged ? "Nothing changed in this snapshot. Untick \"Only changed\" to see every file."
        : "This snapshot has no files.";
    public bool FilesEmpty => _details != null && Files.Count == 0;

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
                Message = $"{path}\nis not in any backup set. Add its folder to a set to keep versions of it.";
                return;
            }
            set = found.Value.Set;
            _pendingFocusPath = found.Value.Key.Path;
        }
        set = set == null ? Sets.FirstOrDefault() : Sets.FirstOrDefault(s => s.Id == set.Id);
        if (set == null)
        {
            Message = "There are no backup sets yet.";
            return;
        }
        if (ReferenceEquals(set, _set)) _ = LoadSnapshotsAsync();
        else SelectedSet = set;
    }

    private async Task LoadSnapshotsAsync()
    {
        var set = _set;
        if (set == null) return;
        // Only the newest request may fill the list; an older one finishing late would add duplicates.
        int load = ++_snapshotsLoad;
        Message = "";
        Status = "";
        Snapshots.Clear();
        Files.Clear();
        _details = null;
        _snapshot = null;
        RaiseHeader();
        Loading = true;
        try
        {
            var list = await Task.Run(() => _app.Service.Engine.ListSnapshots(set));
            if (load != _snapshotsLoad || !ReferenceEquals(set, _set)) return;
            foreach (var s in Enumerable.Reverse(list)) Snapshots.Add(new SnapshotItem(s, this));
            if (Snapshots.Count == 0)
            {
                Message = "No snapshots yet. The first backup of this set has not finished.";
                return;
            }
            SelectedSnapshot = Snapshots[0];
        }
        catch (DriveNotAvailableException)
        {
            Message = $"Plug in {_app.DriveDisplayName(set)} to see and restore older versions of {set.Name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not read the backup drive: {ex.Message}";
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
        Files.Clear();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DriveNotAvailableException)
        {
            Message = ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) Loading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyFilter()
    {
        Files.Clear();
        if (_details == null) return;
        var q = Search.Trim().Replace('\\', '/');
        int shown = 0;
        foreach (var f in _details.Files)
        {
            if (OnlyChanged && f.Change == ChangeKind.Unchanged) continue;
            if (q.Length > 0 && f.Key.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Files.Add(new FileRowViewModel(this, f, _details.Roots.Count > 1));
            // The list virtualises, but building millions of rows still costs; cap and say so.
            if (++shown >= 5000) break;
        }
        Raise(nameof(FilesEmpty));
        Raise(nameof(EmptyFilesText));
    }

    // ---- actions ----

    private async Task RestoreEverythingAsync()
    {
        if (_set == null || _snapshot == null) return;
        if (!Ui.Confirm($"Put every file in {_set.Name} back as it was at {_snapshot.Label}?\n\n" +
                        "Files that changed since then are kept as a new version first, so you can undo this. Files added since then are left alone.",
                        "Restore everything")) return;
        Status = "Restoring…";
        var r = await _app.Service.Engine.RestoreSnapshotAsync(_set, _snapshot.Info.Name, null);
        Status = Summary(r, null);
        await LoadSnapshotsAsync();
        Status = Summary(r, null);
    }

    private async Task RestoreToFolderAsync()
    {
        if (_set == null || _snapshot == null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Restore {_set.Name} ({_snapshot.Label}) to…" };
        if (dlg.ShowDialog() != true) return;
        Status = "Restoring…";
        var r = await _app.Service.Engine.RestoreSnapshotAsync(_set, _snapshot.Info.Name, dlg.FolderName);
        Status = Summary(r, dlg.FolderName);
        if (r.Restored > 0) WindowsIntegration.OpenWithShell(dlg.FolderName);
    }

    internal async Task RestoreFileAsync(SnapshotFile file)
    {
        if (_set == null) return;
        Status = $"Restoring {file.Name}…";
        var r = await _app.Service.Engine.RestoreAsync(_set, new[] { (file.Key, file.Entry) }, null);
        Status = r.Failed.Count > 0 ? $"Could not restore {file.Name}. Is it open in another program?"
            : r.Restored == 0 ? $"{file.Name} is already this version."
            : $"Restored {file.Name}. The replaced copy was kept as a new version.";
        var keep = Status;
        await LoadSnapshotsAsync();
        Status = keep;
    }

    internal async Task OpenFileAsync(SnapshotFile file)
    {
        if (_set == null || _snapshot == null) return;
        var set = _set;
        var label = _snapshot.Label;
        var path = await Task.Run(() => _app.Service.Engine.ExtractForViewing(set, file.Entry, label));
        WindowsIntegration.OpenWithShell(path);
    }

    private static string Summary(RestoreResult r, string? folder)
    {
        var s = $"Restored {Format.Plural(r.Restored, "file", "files")}{(folder != null ? " to " + folder : "")}.";
        if (r.Skipped > 0) s += $" {r.Skipped} already matched.";
        if (r.Failed.Count > 0) s += $" {r.Failed.Count} could not be written (open in another program?).";
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
    public string Meta => $"{Info.TriggerText} · {Info.Changed} changed";
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
    public string Change => _file.Change switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Modified => "Modified",
        ChangeKind.Deleted => "Deleted",
        _ => "Unchanged",
    };
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
    public string RestoreText => _file.Change == ChangeKind.Deleted ? "Bring back" : "Restore";
}
