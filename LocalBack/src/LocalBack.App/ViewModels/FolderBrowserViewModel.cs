using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LocalBack.App.Localization;
using LocalBack.App.Services;
using LocalBack.Core.Engine;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>
/// The "Folders" view of a snapshot: browse it like a drive in Explorer, with an address bar, back/forward/up,
/// a details list (name, date, type, size) and Open/Restore on the selection.
/// </summary>
public sealed class FolderBrowserViewModel : ObservableObject
{
    private readonly HistoryViewModel _owner;
    private SnapshotTree? _tree;
    private SnapshotFolder? _folder;
    private string? _lastPath;
    private readonly List<SnapshotFolder> _back = new();
    private readonly List<SnapshotFolder> _forward = new();
    private string _search = "";
    private string _sort = "Name";
    private bool _descending;
    private ObservableCollection<BrowseItemViewModel> _items = new();
    private BrowseItemViewModel? _selectedItem;
    private IReadOnlyList<BrowseItemViewModel> _selected = Array.Empty<BrowseItemViewModel>();

    public FolderBrowserViewModel(HistoryViewModel owner)
    {
        _owner = owner;
        Back = new RelayCommand(GoBack, () => _back.Count > 0);
        Forward = new RelayCommand(GoForward, () => _forward.Count > 0);
        Up = new RelayCommand(GoUp, () => CanGoUp);
        OpenSelected = new AsyncCommand(OpenSelectedAsync, () => _selected.Count > 0);
        RestoreSelected = new AsyncCommand(() => _owner.RestoreItemsAsync(_selected, toFolder: false), () => _selected.Count > 0);
        RestoreSelectedTo = new AsyncCommand(() => _owner.RestoreItemsAsync(_selected, toFolder: true), () => _selected.Count > 0);
    }

    public ICommand Back { get; }
    public ICommand Forward { get; }
    public ICommand Up { get; }
    public ICommand OpenSelected { get; }
    public ICommand RestoreSelected { get; }
    public ICommand RestoreSelectedTo { get; }

    public ObservableCollection<BrowseItemViewModel> Items { get => _items; private set => Set(ref _items, value); }
    public ObservableCollection<CrumbViewModel> Crumbs { get; } = new();

    public SnapshotFolder? Folder => _folder;
    public bool HasTree => _tree != null;

    /// <summary>The row with keyboard focus; the full selection comes from the view through <see cref="SetSelection"/>.</summary>
    public BrowseItemViewModel? SelectedItem { get => _selectedItem; set => Set(ref _selectedItem, value); }

    public IReadOnlyList<BrowseItemViewModel> Selected => _selected;

    public void SetSelection(IEnumerable<BrowseItemViewModel> items)
    {
        _selected = items.ToList();
        Raise(nameof(StatusText));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Folder path on the PC, for the address bar tooltip.</summary>
    public string AddressText => _folder?.FullPath ?? _folder?.Name ?? "";

    public bool IsSearching => _search.Length > 0;

    public bool IsEmpty => _tree != null && Items.Count == 0;
    public string EmptyText => _tree == null ? "" : IsSearching ? Loc.T("browse.emptySearch") : Loc.T("browse.emptyFolder");

    /// <summary>"12 items", "12 items · 3 selected", like Explorer's status bar.</summary>
    public string StatusText
    {
        get
        {
            if (_tree == null) return "";
            var s = Format.Plural(Items.Count, "item", "items");
            if (_selected.Count > 0) s += " · " + Loc.T("browse.selected", _selected.Count);
            return s;
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value?.Trim() ?? "")) Fill();
        }
    }

    /// <summary>Shows a snapshot's tree, staying in the folder shown before (another snapshot of the set) when it still exists.</summary>
    public void SetTree(SnapshotTree? tree)
    {
        if (_folder != null) _lastPath = _folder.Path;
        _tree = tree;
        _back.Clear();
        _forward.Clear();
        _folder = tree == null ? null : (_lastPath != null ? tree.Find(_lastPath) : null) ?? tree.Home;
        Raise(nameof(HasTree));
        Show();
    }

    /// <summary>Another set: start at its home folder, not where the last set was browsed.</summary>
    public void ForgetPlace() => _lastPath = null;

    /// <summary>Goes to the folder holding a file and selects it (coming from the Explorer menu).</summary>
    public void Reveal(FileKey key)
    {
        if (_tree?.FolderOf(key) is not { } folder) return;
        ClearSearch();
        Navigate(folder);
        SelectedItem = Items.FirstOrDefault(i => i.File != null && FileKey.Comparer.Equals(i.File.Key, key));
    }

    /// <summary>The search box is the owner's; clearing it there clears it here too.</summary>
    private void ClearSearch()
    {
        if (_search.Length > 0) _owner.Search = "";
    }

    public void Open(SnapshotFolder folder) => Navigate(folder);

    private bool CanGoUp => _folder is { Parent: { } p } && (!p.IsTop || _tree?.Home == p);

    private void GoUp()
    {
        if (CanGoUp) Navigate(_folder!.Parent!);
    }

    private void GoBack()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        if (_folder != null) _forward.Add(_folder);
        _folder = target;
        Show();
    }

    private void GoForward()
    {
        if (_forward.Count == 0) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        if (_folder != null) _back.Add(_folder);
        _folder = target;
        Show();
    }

    private void Navigate(SnapshotFolder folder)
    {
        if (ReferenceEquals(folder, _folder)) return;
        if (_folder != null) _back.Add(_folder);
        _forward.Clear();
        _folder = folder;
        ClearSearch();
        Show();
    }

    public void SortBy(string column)
    {
        if (_sort == column) _descending = !_descending;
        else { _sort = column; _descending = false; }
        Fill();
    }

    private void Show()
    {
        Crumbs.Clear();
        if (_folder != null && _tree != null)
        {
            var chain = _folder.Ancestry;
            if (_tree.Home.IsTop || chain.Count == 0) Crumbs.Add(new CrumbViewModel(_tree.Top, _owner.SetName, this));
            foreach (var f in chain) Crumbs.Add(new CrumbViewModel(f, f.Name, this));
            Crumbs[^1].IsLast = true;
        }
        Raise(nameof(AddressText));
        Fill();
    }

    private void Fill()
    {
        var rows = new List<BrowseItemViewModel>();
        if (_folder != null)
        {
            if (_search.Length == 0)
            {
                foreach (var d in _folder.Folders) rows.Add(new BrowseItemViewModel(this, d));
                foreach (var f in _folder.Files) rows.Add(new BrowseItemViewModel(this, f, null));
            }
            else
            {
                var q = _search;
                AddMatches(_folder, "", q, rows);
            }
            Sort(rows);
        }
        Items = new ObservableCollection<BrowseItemViewModel>(rows);
        _selected = Array.Empty<BrowseItemViewModel>();
        SelectedItem = null;
        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyText));
        Raise(nameof(StatusText));
        Raise(nameof(IsSearching));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Search looks through the current folder and everything beneath it, like Explorer's search box.</summary>
    private void AddMatches(SnapshotFolder folder, string location, string q, List<BrowseItemViewModel> rows)
    {
        foreach (var f in folder.Files)
        {
            if (f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) rows.Add(new BrowseItemViewModel(this, f, location));
            if (rows.Count >= 5000) return;
        }
        foreach (var d in folder.Folders)
        {
            if (d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) rows.Add(new BrowseItemViewModel(this, d, location));
            AddMatches(d, location.Length == 0 ? d.Name : location + "\\" + d.Name, q, rows);
            if (rows.Count >= 5000) return;
        }
    }

    private void Sort(List<BrowseItemViewModel> rows)
    {
        Comparison<BrowseItemViewModel> by = _sort switch
        {
            "Modified" => (a, b) => Nullable.Compare(a.ModifiedUtc, b.ModifiedUtc),
            "Type" => (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            "Size" => (a, b) => a.Bytes.CompareTo(b.Bytes),
            "Change" => (a, b) => a.ChangeRank.CompareTo(b.ChangeRank),
            _ => (a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name),
        };
        rows.Sort((a, b) =>
        {
            // Folders stay above files whichever column is sorted, as in Explorer.
            int c = b.IsFolder.CompareTo(a.IsFolder);
            if (c != 0) return c;
            c = by(a, b);
            if (_descending) c = -c;
            return c != 0 ? c : NaturalComparer.Instance.Compare(a.Name, b.Name);
        });
    }

    private async Task OpenSelectedAsync()
    {
        var items = _selected;
        if (items.Count == 1 && items[0].Folder is { } folder)
        {
            Navigate(folder);
            return;
        }
        foreach (var item in items.Where(i => i.File != null).Take(10))
            await _owner.OpenFileAsync(item.File!);
    }

    internal Task OpenAsync(BrowseItemViewModel item)
    {
        if (item.Folder is { } folder)
        {
            Navigate(folder);
            return Task.CompletedTask;
        }
        return _owner.OpenFileAsync(item.File!);
    }

    internal Task RestoreAsync(BrowseItemViewModel item, bool toFolder) => _owner.RestoreItemsAsync(new[] { item }, toFolder);
}

/// <summary>One segment of the address bar.</summary>
public sealed class CrumbViewModel : ObservableObject
{
    private bool _last;

    public CrumbViewModel(SnapshotFolder folder, string name, FolderBrowserViewModel browser)
    {
        Folder = folder;
        Name = name;
        Go = new RelayCommand(() => browser.Open(folder));
    }

    public SnapshotFolder Folder { get; }
    public string Name { get; }
    public ICommand Go { get; }
    public bool IsLast { get => _last; set { if (Set(ref _last, value)) Raise(nameof(Weight)); } }
    public System.Windows.FontWeight Weight => IsLast ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
}

/// <summary>A row of the details list: a folder or a file.</summary>
public sealed class BrowseItemViewModel
{
    private readonly FolderBrowserViewModel _browser;

    public BrowseItemViewModel(FolderBrowserViewModel browser, SnapshotFolder folder, string? location = null)
    {
        _browser = browser;
        Folder = folder;
        Location = location ?? "";
        Open = new AsyncCommand(() => browser.OpenAsync(this));
        Restore = new AsyncCommand(() => browser.RestoreAsync(this, false));
        RestoreTo = new AsyncCommand(() => browser.RestoreAsync(this, true));
    }

    public BrowseItemViewModel(FolderBrowserViewModel browser, SnapshotFile file, string? location)
    {
        _browser = browser;
        File = file;
        Location = location ?? "";
        Open = new AsyncCommand(() => browser.OpenAsync(this));
        Restore = new AsyncCommand(() => browser.RestoreAsync(this, false));
        RestoreTo = new AsyncCommand(() => browser.RestoreAsync(this, true));
    }

    public SnapshotFolder? Folder { get; }
    public SnapshotFile? File { get; }
    public bool IsFolder => Folder != null;

    public ICommand Open { get; }
    public ICommand Restore { get; }
    public ICommand RestoreTo { get; }

    public string Name => Folder?.Name ?? File!.Name;
    /// <summary>In search results: the subfolder the item is in, relative to the folder searched.</summary>
    public string Location { get; }
    public bool HasLocation => Location.Length > 0;

    /// <summary>In search results, shown after the name: "  in Invoices\2026".</summary>
    public string LocationText => HasLocation ? "   " + Loc.T("browse.in", Location) : "";

    public ImageSource? Icon => ShellInfo.Icon(Name, IsFolder);
    public bool HasIcon => Icon != null;
    /// <summary>Line icon used when the shell gives none (not expected on Windows).</summary>
    public Geometry FallbackIcon => (Geometry)System.Windows.Application.Current.FindResource(IsFolder ? "IconFolder" : "IconFile");
    public string TypeName => ShellInfo.TypeName(Name, IsFolder);

    public DateTime? ModifiedUtc => Folder?.ModifiedUtc ?? File?.Entry.MTimeUtc;
    public string Modified => ModifiedUtc is { } m ? m.ToLocalTime().ToString("g", Format.Culture) : "";

    public long Bytes => Folder?.Bytes ?? File!.Entry.Size;
    public string Size => Format.Size(Bytes);
    /// <summary>"3 files" under a folder's size, so the row says what the total covers.</summary>
    public string Detail => Folder != null ? Format.Plural(Folder.FileCount, "file", "files") : "";

    public string FullPath => Folder?.FullPath ?? File!.Key.FullPath;
    public string Tooltip => Folder != null ? $"{FullPath}\n{Detail} · {Size}" : $"{FullPath}\n{TypeName} · {Size}";

    public int ChangeRank => Folder != null ? (Folder.ChangedCount > 0 ? 1 : 2) : File!.Change switch
    {
        ChangeKind.Added => 0,
        ChangeKind.Modified => 1,
        _ => 2,
    };
    public string Change => Folder != null
        ? (Folder.ChangedCount > 0 ? Format.Plural(Folder.ChangedCount, "changed", "changed") : "")
        : File!.Change == ChangeKind.Unchanged ? "" : Loc.T("change." + File.Change);
    public Brush ChangeForeground => File?.Change switch
    {
        ChangeKind.Added => Ui.Brush("Green"),
        ChangeKind.Modified => Ui.Brush("AccentDark"),
        _ => Ui.Brush("TextSecondary"),
    };
    public string RestoreText => Loc.T(IsFolder ? "browse.restoreFolder" : "history.restore");
}
