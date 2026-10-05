using System.Windows.Input;
using LocalBack.Core.Model;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

public enum Page { Sets, History, Drives, Settings }

/// <summary>The main window: left navigation, drive usage, and the current page.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly App _app = App.Current;
    private object _current;
    private Page _page;

    public SetsViewModel Sets { get; }
    private HistoryViewModel? _history;
    private DrivesViewModel? _drives;
    private SettingsViewModel? _settings;

    public MainViewModel()
    {
        Sets = new SetsViewModel(this);
        _current = Sets;
        _app.StatusRefreshed += Refresh;
        GoSets = new RelayCommand(() => Navigate(Page.Sets));
        GoHistory = new RelayCommand(() => Navigate(Page.History));
        GoDrives = new RelayCommand(() => Navigate(Page.Drives));
        GoSettings = new RelayCommand(() => Navigate(Page.Settings));
        Refresh();
    }

    public ICommand GoSets { get; }
    public ICommand GoHistory { get; }
    public ICommand GoDrives { get; }
    public ICommand GoSettings { get; }

    public object Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    public Page Page
    {
        get => _page;
        private set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(IsSets));
                Raise(nameof(IsHistory));
                Raise(nameof(IsDrives));
                Raise(nameof(IsSettings));
                Raise(nameof(ShowNav));
                Raise(nameof(Title));
            }
        }
    }

    public bool IsSets { get => Page == Page.Sets; set { if (value) Navigate(Page.Sets); } }
    public bool IsHistory { get => Page == Page.History; set { if (value) Navigate(Page.History); } }
    public bool IsDrives { get => Page == Page.Drives; set { if (value) Navigate(Page.Drives); } }
    public bool IsSettings { get => Page == Page.Settings; set { if (value) Navigate(Page.Settings); } }

    /// <summary>The history screen has its own side panel, like in the design.</summary>
    public bool ShowNav => Page != Page.History;

    public string Title => Page == Page.History ? Localization.Loc.T("title.history") : "LocalBack";

    public void Navigate(Page page)
    {
        Current = page switch
        {
            Page.History => _history ??= new HistoryViewModel(this),
            Page.Drives => _drives ??= new DrivesViewModel(this),
            Page.Settings => _settings ??= new SettingsViewModel(),
            _ => Sets,
        };
        Page = page;
        if (page == Page.History) _history!.Activate();
        if (page == Page.Drives) _drives!.Refresh();
    }

    public void ShowHistory(BackupSet? set, string? path)
    {
        Navigate(Page.History);
        _history!.Show(set, path);
    }

    // ---- drive usage box at the bottom of the nav ----

    private string _driveName = "";
    private double _driveUsed;
    private string _driveFree = "";
    private bool _hasDrive;

    public string DriveName { get => _driveName; private set => Set(ref _driveName, value); }
    public double DriveUsedPercent { get => _driveUsed; private set => Set(ref _driveUsed, value); }
    public string DriveFreeText { get => _driveFree; private set => Set(ref _driveFree, value); }
    public bool HasDrive { get => _hasDrive; private set => Set(ref _hasDrive, value); }

    private void Refresh()
    {
        Sets.Refresh();
        _history?.RefreshSets();
        if (Page == Page.History) _history?.OnStatusChanged();
        var service = _app.Service;
        var set = service.Sets.FirstOrDefault(s => service.DriveFor(s) != null) ?? service.Sets.FirstOrDefault();
        if (set == null)
        {
            HasDrive = false;
            return;
        }
        HasDrive = true;
        DriveName = _app.DriveDisplayName(set);
        var drive = service.DriveFor(set);
        if (drive == null)
        {
            DriveUsedPercent = 0;
            DriveFreeText = Localization.Loc.T("drive.notConnected");
            return;
        }
        var (free, total) = drive.Space();
        DriveUsedPercent = total > 0 ? 100.0 * (total - free) / total : 0;
        DriveFreeText = total > 0 ? Localization.Loc.T("drive.freeOf", Format.Size(free), Format.Size(total)) : "";
    }

    public void Dispose()
    {
        _app.StatusRefreshed -= Refresh;
        _history?.Dispose();
    }
}
