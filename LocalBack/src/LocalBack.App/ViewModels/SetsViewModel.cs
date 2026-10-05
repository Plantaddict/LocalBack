using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LocalBack.App.Localization;
using LocalBack.Core.Model;
using LocalBack.Core.Service;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>"Backup sets" page.</summary>
public sealed class SetsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly App _app = App.Current;
    private string _subtitle = "";
    private string _footer = "";

    public ObservableCollection<SetRowViewModel> Rows { get; } = new();

    public SetsViewModel(MainViewModel main)
    {
        _main = main;
        AddSet = new RelayCommand(() => _app.ShowAddSet(null));
        BackUpAll = new AsyncCommand(() => _app.Service.BackUpNowAsync(), () => Rows.Count > 0);
    }

    public ICommand AddSet { get; }
    public ICommand BackUpAll { get; }

    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string Footer { get => _footer; private set => Set(ref _footer, value); }
    public bool IsEmpty => Rows.Count == 0;

    public void Refresh()
    {
        var service = _app.Service;
        var statuses = service.GetStatuses();

        // Keep row objects stable so buttons don't flicker while a run updates the status.
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!statuses.Any(s => s.Set.Id == Rows[i].Id)) Rows.RemoveAt(i);
        for (int i = 0; i < statuses.Count; i++)
        {
            var row = Rows.FirstOrDefault(r => r.Id == statuses[i].Set.Id);
            if (row == null)
            {
                row = new SetRowViewModel(_main, statuses[i].Set.Id);
                Rows.Insert(Math.Min(i, Rows.Count), row);
            }
            row.Update(statuses[i]);
        }
        Raise(nameof(IsEmpty));

        Subtitle = Describe(statuses, service);
        var live = statuses.Where(s => s.Set.Schedule == RunSchedule.Live && s.Set.Enabled).ToList();
        var drives = statuses.Select(s => _app.DriveDisplayName(s.Set).Split(' ')[0]).Distinct().ToList();
        var check = DateTime.Today.Add(service.Settings.DailyCheckAt).ToString("HH:mm");
        Footer = statuses.Count == 0
            ? Loc.T("sets.footer.none")
            : (live.Count > 0 ? Loc.T("sets.footer.live") : "")
              + (drives.Count == 1 ? Loc.T("sets.footer.checkDrive", check, drives[0]) : Loc.T("sets.footer.checkAny", check));
    }

    private static string Describe(List<SetStatus> statuses, BackupService service)
    {
        if (statuses.Count == 0) return Loc.T("sets.subtitle.none");
        if (service.RunningSetName is { } running) return Loc.T("sets.subtitle.running", running);
        if (service.IsPaused) return Loc.T("sets.subtitle.paused", service.Settings.PausedUntil!.Value.ToLocalTime().ToString("HH:mm"));
        var locked = statuses.FirstOrDefault(s => s.Health == SetHealth.Locked);
        if (locked != null) return Loc.T("sets.subtitle.locked", locked.DriveName);
        var missing = statuses.FirstOrDefault(s => s.Health == SetHealth.DriveMissing);
        if (missing != null) return Loc.T("sets.subtitle.driveMissing", missing.DriveName);
        var failed = statuses.FirstOrDefault(s => s.Health == SetHealth.Error);
        if (failed != null) return Loc.T("sets.subtitle.failed", failed.Set.Name, failed.Error ?? "");
        int pending = statuses.Count(s => s.Health == SetHealth.Pending);
        if (pending > 0) return Loc.Instance.Plural(pending, "set has changes waiting", "setsPending");
        var last = statuses.Where(s => s.Latest != null).Select(s => s.Latest!.CreatedUtc).DefaultIfEmpty().Max();
        return last == default ? Loc.T("sets.subtitle.first") : Loc.T("sets.subtitle.upToDate", Format.When(last));
    }
}

public sealed class SetRowViewModel : ObservableObject
{
    private readonly App _app = App.Current;
    private SetStatus? _status;

    public string Id { get; }

    public SetRowViewModel(MainViewModel main, string id)
    {
        Id = id;
        BackUp = new AsyncCommand(() => _app.Service.BackUpNowAsync(Set), () => _status?.Health != SetHealth.Running);
        Restore = new RelayCommand(() => main.ShowHistory(Set, null));
        Edit = new RelayCommand(() => _app.ShowAddSet(Set));
        Remove = new AsyncCommand(RemoveAsync);
        ToggleEnabled = new RelayCommand(() => _app.Service.SetEnabled(Set, !Set.Enabled));
        Unlock = new RelayCommand(() => { if (_app.Service.DriveFor(Set) is { } d) _app.ShowUnlock(d); });
        ForgetPassword = new RelayCommand(() => { if (_app.Service.DriveFor(Set) is { } d) _app.Service.LockDrive(d); },
            () => _app.Service.DriveFor(Set) is { IsEncrypted: true, IsLocked: false });
        ShowFolder = new RelayCommand(() =>
        {
            var f = Set.Folders.FirstOrDefault();
            if (f != null && Directory.Exists(f)) Services.WindowsIntegration.OpenWithShell(f);
        });
    }

    private BackupSet Set => _status!.Set;

    public ICommand BackUp { get; }
    public ICommand Restore { get; }
    public ICommand Edit { get; }
    public ICommand Remove { get; }
    public ICommand ToggleEnabled { get; }
    public ICommand Unlock { get; }
    public ICommand ForgetPassword { get; }
    public ICommand ShowFolder { get; }
    public string ToggleEnabledText => Loc.T(_status?.Set.Enabled == false ? "menu.startBackingUp" : "menu.stopBackingUp");

    public string Name => _status?.Set.Name ?? "";
    public string Path => _status?.FoldersText ?? "";
    public string Status => _status == null ? "" : Ui.StatusText(_status.Health);
    public string When => _status == null ? "" : Ui.WhenText(_status);
    public string Size => _status == null ? "" : Ui.SizeText(_status);
    public string Versions => _status == null ? "" : Ui.VersionsText(_status);
    public Brush Dot => Ui.Dot(_status?.Health ?? SetHealth.NeverRun);
    public bool IsLocked => _status?.Health == SetHealth.Locked;
    public bool CanBackUp => !IsLocked;
    public string Tooltip => _status == null ? "" : Loc.T("row.tooltip", Ui.ScheduleText(_status.Set.Schedule), _status.DriveName);

    public void Update(SetStatus status)
    {
        _status = status;
        RaiseAll();
    }

    private async Task RemoveAsync()
    {
        var set = Set;
        var answer = System.Windows.MessageBox.Show(Loc.T("remove.question", set.Name), Loc.T("remove.title"),
            System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.Cancel);
        if (answer == System.Windows.MessageBoxResult.Cancel) return;
        await _app.Service.RemoveSetAsync(set, deleteBackups: answer == System.Windows.MessageBoxResult.Yes);
    }
}
