using System.Windows.Input;
using System.Windows.Media;
using LocalBack.Core.Model;
using LocalBack.Core.Retention;
using LocalBack.Core.Service;
using LocalBack.Core.Storage;
using LocalBack.Core.Util;

namespace LocalBack.App.ViewModels;

/// <summary>"Free up space": three retention plans, each with the exact space it frees.</summary>
public sealed class FreeSpaceViewModel : ObservableObject
{
    private readonly App _app = App.Current;
    private readonly DriveStore _drive;
    private readonly bool _lowSpace;
    private PlanOption? _picked;
    private bool _auto;
    private bool _busy;
    private string _offenders = "";
    private double _currentPct, _olderPct;
    private string _currentText = "Current files …", _olderText = "Older versions …";
    private readonly CancellationTokenSource _cts = new();

    public List<PlanOption> Options { get; }
    public event Action? CloseRequested;

    public FreeSpaceViewModel(DriveStore drive, bool lowSpace)
    {
        _drive = drive;
        _lowSpace = lowSpace;
        var standing = _app.Service.Settings.Retention;
        Options = RetentionPlan.Choices.Select(p => new PlanOption(p, this)).ToList();
        Picked = Options.FirstOrDefault(o => o.Plan.Kind == standing.Kind) ?? Options[1];
        _auto = _app.Service.Settings.AutoFreeSpace || lowSpace;
        Apply = new AsyncCommand(ApplyAsync, () => !_busy && Picked?.Preview is { BytesFreed: > 0 });
        NotNow = new RelayCommand(() => { _cts.Cancel(); CloseRequested?.Invoke(); });
    }

    public ICommand Apply { get; }
    public ICommand NotNow { get; }

    public string DriveName => BackupService.DriveName(_drive);
    public string Title => _lowSpace ? $"{DriveName} is almost full" : $"Free up space on {DriveName}";

    public string Summary
    {
        get
        {
            var info = _app.Service.SpaceInfo(_drive)!;
            var s = $"{Format.Size(info.Free)} free of {Format.Size(info.Total)}.";
            if (info.NextRunEstimate > 0) s += $" The next backup needs about {Format.Size(info.NextRunEstimate)}.";
            return s + " Old copies of files you have changed many times take most of the space.";
        }
    }

    public double CurrentPercent { get => _currentPct; private set => Set(ref _currentPct, value); }
    public double OlderPercent { get => _olderPct; private set => Set(ref _olderPct, value); }
    public string CurrentText { get => _currentText; private set => Set(ref _currentText, value); }
    public string OlderText { get => _olderText; private set => Set(ref _olderText, value); }
    public string Offenders { get => _offenders; private set { if (Set(ref _offenders, value)) Raise(nameof(HasOffenders)); } }
    public bool HasOffenders => Offenders.Length > 0;

    public PlanOption? Picked
    {
        get => _picked;
        set
        {
            if (!Set(ref _picked, value)) return;
            foreach (var o in Options) o.Refresh();
            Raise(nameof(ApplyText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool AutoFree { get => _auto; set => Set(ref _auto, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) Raise(nameof(ApplyText)); } }

    public string ApplyText => Busy ? "Freeing up…"
        : Picked?.Preview is { } p ? (p.BytesFreed > 0 ? $"Free up {Format.Size(p.BytesFreed)}" : "Nothing to free")
        : "Free up…";

    /// <summary>Works out each plan's exact saving in the background.</summary>
    public async Task LoadAsync()
    {
        var (_, total) = _drive.Space();
        List<Core.Retention.PrunePreview> previews;
        try
        {
            previews = await _app.Service.PreviewAllAsync(_drive, Options.Select(o => o.Plan).ToList(), _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Offenders = $"Could not read the drive: {ex.Message}";
            return;
        }
        for (int i = 0; i < Options.Count; i++) Options[i].Preview = previews[i];
        var first = previews[0];
        CurrentPercent = total > 0 ? 100.0 * first.CurrentBytes / total : 0;
        OlderPercent = total > 0 ? 100.0 * first.OlderBytes / total : 0;
        CurrentText = $"Current files {Format.Size(first.CurrentBytes)}";
        OlderText = $"Older versions {Format.Size(first.OlderBytes)}";
        var top = first.Offenders.Take(2).Select(x => $"{x.Name} ({x.Versions} versions, {Format.Size(x.Bytes)})").ToList();
        Offenders = top.Count > 0 ? "Biggest offenders: " + string.Join(", ", top) + "." : "";
        Raise(nameof(ApplyText));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ApplyAsync()
    {
        if (Picked?.Preview is not { } preview) return;
        Busy = true;
        try
        {
            var freed = await _app.Service.FreeUpAsync(_drive, preview);
            _app.Service.SaveSettings(s =>
            {
                s.AutoFreeSpace = AutoFree;
                if (AutoFree) s.Retention = Picked.Plan;
            });
            Ui.Info($"{Format.Size(freed)} freed on {DriveName}. The newest version of every file was kept.", "Free up space");
            CloseRequested?.Invoke();
        }
        finally
        {
            Busy = false;
        }
    }

    public void Cancel() => _cts.Cancel();
}

public sealed class PlanOption : ObservableObject
{
    private readonly FreeSpaceViewModel _owner;
    private PrunePreview? _preview;

    public PlanOption(RetentionPlan plan, FreeSpaceViewModel owner)
    {
        Plan = plan;
        _owner = owner;
    }

    public RetentionPlan Plan { get; }
    public string Title => Plan.Title;
    public string Description => Plan.Description;
    public string Frees => _preview == null ? "…" : Format.Size(_preview.BytesFreed);

    public PrunePreview? Preview
    {
        get => _preview;
        set { if (Set(ref _preview, value)) Raise(nameof(Frees)); }
    }

    public bool IsChecked
    {
        get => ReferenceEquals(_owner.Picked, this);
        set { if (value) _owner.Picked = this; }
    }

    public Brush Border => IsChecked ? Ui.Brush("Accent") : Ui.Brush("Border");
    public Brush Background => IsChecked ? Ui.Brush("AccentTint") : Brushes.White;

    internal void Refresh()
    {
        Raise(nameof(IsChecked));
        Raise(nameof(Border));
        Raise(nameof(Background));
    }
}
