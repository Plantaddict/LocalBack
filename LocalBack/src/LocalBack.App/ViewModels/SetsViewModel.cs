using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
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
        var live = statuses.Where(s => s.Set.Schedule == RunSchedule.Live).ToList();
        var drives = statuses.Select(s => _app.DriveDisplayName(s.Set).Split(' ')[0]).Distinct().ToList();
        var check = DateTime.Today.Add(service.Settings.DailyCheckAt).ToString("HH:mm");
        Footer = statuses.Count == 0
            ? "LocalBack keeps a copy of chosen folders on an external drive, with every saved version, and brings files back with one click."
            : (live.Count > 0 ? "Watching folders live: changed files are backed up within seconds of saving. " : "")
              + $"Full check daily at {check} and whenever {(drives.Count == 1 ? "drive " + drives[0] + " is" : "a backup drive is")} plugged in.";
    }

    private static string Describe(List<SetStatus> statuses, BackupService service)
    {
        if (statuses.Count == 0) return "Add a backup set to start protecting your files.";
        if (service.RunningSetName is { } running) return $"Backing up {running}…";
        if (service.IsPaused) return $"Backups are paused until {service.Settings.PausedUntil!.Value.ToLocalTime():HH:mm}. Changes are being noted.";
        var missing = statuses.Where(s => s.Health == SetHealth.DriveMissing).ToList();
        if (missing.Count > 0) return $"{missing[0].DriveName} is not connected. Changes are queued until it is plugged in.";
        var failed = statuses.FirstOrDefault(s => s.Health == SetHealth.Error);
        if (failed != null) return $"The last backup of {failed.Set.Name} failed: {failed.Error}";
        int pending = statuses.Count(s => s.Health == SetHealth.Pending);
        if (pending > 0) return pending == 1 ? "1 set has changes waiting to be backed up." : $"{pending} sets have changes waiting to be backed up.";
        var last = statuses.Where(s => s.Latest != null).Select(s => s.Latest!.CreatedUtc).DefaultIfEmpty().Max();
        return last == default ? "Waiting for the first backup." : $"All sets are up to date. Last change saved {Format.When(last)}.";
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
    public ICommand ShowFolder { get; }
    public string ToggleEnabledText => _status?.Set.Enabled == false ? "Start backing up" : "Stop backing up (keep history)";

    public string Name => _status?.Set.Name ?? "";
    public string Path => _status?.FoldersText ?? "";
    public string Status => _status?.StatusText ?? "";
    public string When => _status?.WhenText ?? "";
    public string Size => _status?.SizeText ?? "";
    public string Versions => _status?.VersionsText ?? "";
    public Brush Dot => Ui.Dot(_status?.Health ?? SetHealth.NeverRun);
    public string Tooltip => _status == null ? "" : $"{_status.Set.ScheduleText} · on {_status.DriveName}";

    public void Update(SetStatus status)
    {
        _status = status;
        RaiseAll();
    }

    private async Task RemoveAsync()
    {
        var set = Set;
        var answer = System.Windows.MessageBox.Show(
            $"Stop backing up \"{set.Name}\"?\n\nYes: also delete its backups from the drive.\nNo: keep the backups on the drive (you can add them back from Drives).",
            "Remove backup set", System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.Cancel);
        if (answer == System.Windows.MessageBoxResult.Cancel) return;
        await _app.Service.RemoveSetAsync(set, deleteBackups: answer == System.Windows.MessageBoxResult.Yes);
    }
}
